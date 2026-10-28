using System;
using System.Buffers;
using System.Collections.Generic;
using KotH.Shared.Bots;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;

namespace KotH.Shared.Net
{
    /// <summary>
    /// One peer's side of a rollback game, the host's own player included.
    /// Steps the world at once with the inputs it has: its own, the ones the
    /// host confirmed, and for everyone else a guess. When a confirmed tick
    /// shows a guess was wrong, it goes back to that tick and steps again.
    /// Keeps its clock just far enough ahead of the host that its own inputs
    /// arrive in time, and reports a checksum of every 40th final state.
    /// </summary>
    public sealed class PeerSession : IFinalStates
    {
        /// <summary>Ticks between sampling an input and stepping with it.</summary>
        public const int InputDelay = 1;

        /// <summary>How far past the last confirmed tick the peer may guess; then it waits.</summary>
        public const int MaxPredict = 15;

        /// <summary>Ticks the own input should reach the host before the host needs it.</summary>
        public const float LeadTarget = 2f;

        public const int ChecksumEvery = 40;

        const int Ring = 128;
        const int MaxTicksPerUpdate = 20;
        const int MaxInputsPerMessage = 32;

        struct Used
        {
            public int NetId;
            public MoveInput Input;
        }

        sealed class Record
        {
            public readonly List<Used> Inputs = new List<Used>();
            public readonly List<NetEvent> Events = new List<NetEvent>();
        }

        readonly INetWorld world;
        readonly SimulationSystem sim;
        readonly INetTransport transport;
        readonly Func<MoveInput> sampleLocal;
        readonly string name;

        readonly Dictionary<int, SimulationBody> bodies = new Dictionary<int, SimulationBody>();
        readonly Dictionary<int, BodyState> joinStates = new Dictionary<int, BodyState>();
        readonly Dictionary<int, int> leaveTicks = new Dictionary<int, int>();
        readonly Dictionary<int, Record> confirmed = new Dictionary<int, Record>();
        readonly Dictionary<int, Known> lastConfirmed = new Dictionary<int, Known>();
        readonly List<(SimulationBody Body, Vector3 At, float Yaw)> guessed = new List<(SimulationBody, Vector3, float)>();

        /// <summary>A player's last two confirmed inputs, the base of every guess.</summary>
        struct Known
        {
            public MoveInput Last, Prev;
            public int Tick;
            public bool HasPrev;
        }

        readonly WorldSnapshot[] snaps = new WorldSnapshot[Ring];
        readonly int[] snapTicks = new int[Ring];
        readonly List<Used>[] used = new List<Used>[Ring];
        readonly int[] usedTicks = new int[Ring];
        readonly MoveInput[] local = new MoveInput[Ring];
        readonly int[] localTicks = new int[Ring];

        readonly List<MoveInput> row = new List<MoveInput>();
        readonly ArrayBufferWriter<byte> outgoing = new ArrayBufferWriter<byte>();
        readonly ArrayBufferWriter<byte> stateBuffer = new ArrayBufferWriter<byte>();
        readonly WorldSnapshot decoded = new WorldSnapshot();

        int confirmedTick = -1;     // every record up to here is known and applied
        int ackInputTick = -1;      // the host has our inputs up to here
        int firstLocalTick = int.MaxValue, newestLocalTick = -1;
        int checkedTick = -1;

        // The tick this peer should be at, as a line over local time.
        double syncTick, syncTime;
        double steadyFrom;
        double lastSent = double.MinValue;
        bool sendNow;

        /// <summary>
        /// A peer that joins: says hello and waits for the host's state. A
        /// founder is the host's own peer, whose world is the start of the game.
        /// With join false a guest only asks for the map first and joins with
        /// Join once it has loaded it, so it never stands in the game half ready.
        /// </summary>
        public PeerSession(INetWorld world, INetTransport transport, Func<MoveInput> sampleLocal,
                           string name, bool founder, double now, bool join = true)
        {
            this.world = world;
            sim = world.Simulation;
            this.transport = transport;
            this.sampleLocal = sampleLocal;
            this.name = name;

            for (int i = 0; i < Ring; i++)
            {
                snaps[i] = new WorldSnapshot();
                snapTicks[i] = usedTicks[i] = localTicks[i] = -1;
                used[i] = new List<Used>();
            }

            foreach (SimulationBody body in sim.Bodies)
                if (body.NetId != 0)
                    bodies[body.NetId] = body;

            if (founder)
            {
                Joined = true;
                syncTick = LeadTarget - InputDelay;
                syncTime = now;
            }

            if (join)
            {
                Join(now);
                return;
            }
            var w = new Writer(outgoing);
            outgoing.Clear();
            w.Byte((byte)MsgType.Ask);
            w.Bytes(NetProtocol.BuildId);
            transport.Send(0, outgoing.WrittenSpan, reliable: true);
        }

        /// <summary>Asks the host to let this peer in; once, at once unless asked not to.</summary>
        public void Join(double now)
        {
            if (joining)
                return;
            joining = true;
            var w = new Writer(outgoing);
            outgoing.Clear();
            w.Byte((byte)MsgType.Hello);
            w.Bytes(NetProtocol.BuildId);
            w.Double(now);
            w.Text(name);
            transport.Send(0, outgoing.WrittenSpan, reliable: true);
        }

        bool joining;

        /// <summary>The host has said which map, and it is loaded; see Join.</summary>
        public bool MapKnown { get; private set; }

        public SimulationSystem Simulation => sim;

        /// <summary>The world's map, which a host hands to everyone who joins.</summary>
        public string Map => world.Map;

        /// <summary>This peer's player, 0 until the host has said who it is.</summary>
        public int LocalNetId { get; private set; }
        public bool Joined { get; private set; }
        public RejectReason? Rejected { get; private set; }
        public int ConfirmedTick => confirmedTick;

        /// <summary>Last measured round trip in milliseconds.</summary>
        public double RoundTripMs { get; private set; }

        /// <summary>How many ticks ahead of need the host last saw our input.</summary>
        public float Lead { get; private set; }

        public int Rollbacks { get; private set; }
        public int RolledTicks { get; private set; }
        public int DeepestRollback { get; private set; }

        /// <summary>Ticks the most recent rollback went back.</summary>
        public int LastRollback { get; private set; }

        /// <summary>The host went away; the game is over for this peer.</summary>
        public bool HostLost { get; private set; }

        /// <summary>Metres the farthest other player moved per rollback, summed; see WorstCorrection.</summary>
        public double CorrectionSum { get; private set; }
        public float WorstCorrection { get; private set; }

        /// <summary>Degrees the facing of the farthest turned other player snapped per rollback, summed.</summary>
        public double TurnCorrectionSum { get; private set; }

        /// <summary>How many ticks a guess keeps turning; 0 holds the last view.</summary>
        public int TurnAhead { get; set; } = DefaultTurnAhead;
        public const int DefaultTurnAhead = 8;
        public int Stalls { get; private set; }
        public int Repairs { get; private set; }

        /// <summary>Raised for every checksum tick once its state is final.</summary>
        public event Action<int, ulong> Checksum;

        public SimulationBody BodyOf(int netId) => bodies.TryGetValue(netId, out SimulationBody b) ? b : null;

        /// <summary>
        /// Takes in what arrived, corrects any wrong guess, steps up to where
        /// the clock says, and sends this peer's inputs. Call once per frame.
        /// </summary>
        public void Update(double now)
        {
            while (transport.Poll(out TransportEvent e))
            {
                if (e.Kind == TransportEventKind.Data && e.Data.Length > 0)
                    Receive(e.Data, now);
                else if (e.Kind == TransportEventKind.Disconnected)
                    HostLost = true;
            }

            if (!Joined)
                return;

            ApplyConfirmed();
            Advance(now);
            SendInputs(now);
            ReportChecksums();
            DropLeavers();
        }

        /// <summary>
        /// The newest state no rollback can change any more, as bytes, if it is
        /// at least the given tick. What the host sends a joining peer.
        /// </summary>
        public bool TryEncodeFinal(int atLeast, ArrayBufferWriter<byte> into, out int tick)
        {
            tick = FinalTick;
            int slot = tick % Ring;
            if (tick < atLeast || tick < 0 || snapTicks[slot] != tick)
                return false;
            StateCodec.Write(snaps[slot], into);
            return true;
        }

        // The newest tick whose starting state is in the ring and settled:
        // every tick before it confirmed, and stepped once, so recorded.
        int FinalTick => Math.Min(confirmedTick + 1, sim.CurrentTick - 1);

        /// <summary>Where the shared clock stands now, in ticks with a fraction.</summary>
        public double ClockTick(double now) => syncTick + (now - syncTime) / 1000.0 / SimulationSystem.TickSeconds;

        /// <summary>
        /// How far the shown frame lies between the last two stepped ticks,
        /// 0 to 1, for drawing between them.
        /// </summary>
        public float Alpha(double now) => (float)Math.Clamp(ClockTick(now) - (sim.CurrentTick - 1), 0.0, 1.0);

        /// <summary>
        /// The state a tick started from, once no rollback can change it any
        /// more and while it is still in the ring. What the host's bots see.
        /// </summary>
        public bool TryFinalState(int tick, out WorldSnapshot state)
        {
            state = snaps[tick % Ring];
            return tick >= 0 && tick <= FinalTick && snapTicks[tick % Ring] == tick;
        }

        public int NewestFinal => FinalTick;

        /// <summary>
        /// Inputs known before the host confirms them: on the host, its own
        /// bots. A guess for them would only be wrong where the answer is known.
        /// </summary>
        public IBotInputs KnownInputs { get; set; }

        /// <summary>
        /// The round as no rollback can change it any more. What the HUD
        /// shows for a win, so a banner never flashes up and vanishes again.
        /// </summary>
        public bool TryConfirmedMatch(out Match.MatchState state)
        {
            int tick = FinalTick;
            if (tick >= 0 && snapTicks[tick % Ring] == tick)
            {
                state = snaps[tick % Ring].Match;
                return true;
            }
            state = default;
            return false;
        }

        void Receive(byte[] data, double now)
        {
            var r = new Reader(data);
            switch ((MsgType)r.Byte())
            {
                case MsgType.Welcome: Welcome(ref r, now); break;
                case MsgType.Reject: Rejected = (RejectReason)r.Byte(); break;
                case MsgType.Map:
                    string map = r.Text();
                    if (!r.Failed)
                    {
                        world.LoadMap(map);
                        MapKnown = true;
                    }
                    break;
                case MsgType.Confirmed: Confirm(ref r, now); break;
                case MsgType.Repair: Repair(ref r, now); break;
            }
        }

        void Welcome(ref Reader r, double now)
        {
            LocalNetId = r.Int();
            int hostTick = r.Int();
            double echo = r.Double();
            RoundTripMs = Math.Max(0.0, now - echo);
            string map = r.Text();

            // A founder already has the world; anyone else builds the host's map and takes its state.
            int length = r.Int();
            if (length > 0)
            {
                if (r.Failed)
                    return;
                world.LoadMap(map);
                if (!TakeState(ref r, length))
                    return;
            }

            Joined = true;
            syncTick = hostTick + RoundTripMs / 1000.0 / SimulationSystem.TickSeconds + LeadTarget - InputDelay;
            syncTime = now;
        }

        void Repair(ref Reader r, double now)
        {
            int length = r.Int();
            if (TakeState(ref r, length))
                Repairs++;
        }

        /// <summary>
        /// Builds the world a transfer describes: spawns every player of the
        /// roster this peer lacks, in NetId order like everyone else did, and
        /// restores. Confirmed history before the state no longer matters.
        /// </summary>
        bool TakeState(ref Reader r, int length)
        {
            ReadOnlySpan<byte> state = r.Bytes(length);
            int players = r.Int();
            var roster = new List<NetEvent>();
            for (int i = 0; i < players; i++)
                roster.Add(NetProtocol.ReadEvent(ref r));
            if (r.Failed)
                return false;

            roster.Sort((a, b) => a.NetId.CompareTo(b.NetId));
            foreach (NetEvent p in roster)
                if (!bodies.ContainsKey(p.NetId))
                    Spawn(p);

            return LoadState(state);
        }

        /// <summary>
        /// Restores a final state as bytes and starts over from it; every body
        /// in it must exist here. Probes use it to damage a peer on purpose.
        /// </summary>
        public bool LoadState(ReadOnlySpan<byte> state)
        {
            if (!StateCodec.Read(state, BodyOf, decoded))
                return false;
            sim.Restore(decoded);

            int tick = decoded.Tick;
            confirmedTick = tick - 1;
            confirmed.Clear();
            for (int i = 0; i < Ring; i++)
                snapTicks[i] = usedTicks[i] = -1;
            checkedTick = tick - 1;
            Capture(tick);
            return true;
        }

        void Confirm(ref Reader r, double now)
        {
            int hostTick = r.Int();
            double echo = r.Double();
            float lead = r.Float();
            int ackInputs = r.Int();
            int first = r.Int();
            int count = r.Byte();

            // The welcome's round trip includes any pause before either side
            // ran its next frame, such as loading. A far shorter one now
            // means the clock started wrong: set it again from this one.
            double sample = Math.Max(0.0, now - echo);
            if (sample * 2.0 < RoundTripMs && sample + 50.0 < RoundTripMs)
            {
                RoundTripMs = sample;
                syncTick = hostTick + sample / 1000.0 / SimulationSystem.TickSeconds + LeadTarget - InputDelay;
                syncTime = now;
                steadyFrom = now + sample + 600.0;
            }
            else
                RoundTripMs = RoundTripMs * 0.9 + sample * 0.1;
            ackInputTick = Math.Max(ackInputTick, ackInputs);

            // Steer the clock: our input should reach the host LeadTarget
            // ticks early. Slowly, the measurement is a round trip old. After
            // a jump the host's last samples still show the old lead: wait
            // for fresh ones, or every message would jump again.
            if (!float.IsNaN(lead))
            {
                Lead = lead;
                double error = LeadTarget - lead;
                if (now >= steadyFrom && Math.Abs(error) > 12.0)
                {
                    syncTick += error;
                    steadyFrom = now + RoundTripMs + 600.0;
                }
                else if (now >= steadyFrom)
                    syncTick += Math.Clamp(error * 0.05, -0.5, 0.5);
            }

            for (int i = 0; i < count; i++)
            {
                int tick = first + i;
                var rec = new Record();
                int inputs = r.Byte();
                for (int k = 0; k < inputs; k++)
                {
                    int id = r.Int();
                    rec.Inputs.Add(new Used { NetId = id, Input = NetProtocol.ReadInput(ref r) });
                }
                int events = r.Byte();
                for (int k = 0; k < events; k++)
                    rec.Events.Add(NetProtocol.ReadEvent(ref r));
                if (r.Failed)
                    return;
                if (tick > confirmedTick && !confirmed.ContainsKey(tick))
                    confirmed[tick] = rec;
            }
        }

        /// <summary>
        /// Takes every confirmed tick that follows on from the last one. Any
        /// already stepped with a different guess, or with a join or leave
        /// it did not know of, is where the world has to go back to.
        /// </summary>
        void ApplyConfirmed()
        {
            int from = int.MaxValue;
            while (confirmed.TryGetValue(confirmedTick + 1, out Record rec))
            {
                int tick = confirmedTick + 1;
                foreach (NetEvent e in rec.Events)
                {
                    if (e.Join && !bodies.ContainsKey(e.NetId))
                        Spawn(e);
                    else if (!e.Join)
                        leaveTicks[e.NetId] = tick;
                }

                if (tick < sim.CurrentTick && (rec.Events.Count > 0 || !SameAsUsed(tick, rec)))
                    from = Math.Min(from, tick);

                foreach (Used u in rec.Inputs)
                {
                    lastConfirmed.TryGetValue(u.NetId, out Known k);
                    bool follows = k.Tick == tick - 1 && lastConfirmed.ContainsKey(u.NetId);
                    lastConfirmed[u.NetId] = new Known { Last = u.Input, Prev = k.Last, Tick = tick, HasPrev = follows };
                }
                confirmedTick = tick;
            }

            if (from != int.MaxValue)
                Rollback(from);
        }

        bool SameAsUsed(int tick, Record rec)
        {
            int slot = tick % Ring;
            if (usedTicks[slot] != tick)
                return false;
            List<Used> was = used[slot];
            if (was.Count < rec.Inputs.Count)
                return false;

            foreach (Used u in rec.Inputs)
            {
                bool found = false;
                foreach (Used v in was)
                    if (v.NetId == u.NetId)
                    {
                        if (!NetProtocol.Same(u.Input, v.Input))
                            return false;
                        found = true;
                        break;
                    }
                if (!found)
                    return false;
            }
            return true;
        }

        void Rollback(int from)
        {
            int slot = from % Ring;
            int until = sim.CurrentTick;
            if (snapTicks[slot] != from)
                throw new InvalidOperationException($"No snapshot for tick {from} to roll back to.");

            // Where the guess had the other players: the jump a correction shows.
            guessed.Clear();
            foreach (SimulationBody body in sim.Bodies)
                if (body.NetId >= NetWorld.FirstPlayerId && body.NetId != LocalNetId)
                    guessed.Add((body, body.Motor.Position, UMath.YawOf(body.Motor.Rotation)));

            sim.Restore(snaps[slot]);
            for (int t = from; t < until; t++)
                StepTick(t);

            float jump = 0f, turn = 0f;
            foreach ((SimulationBody body, Vector3 at, float yaw) in guessed)
                if (!body.IsParked)
                {
                    jump = Math.Max(jump, Vector3.Distance(body.Motor.Position, at));
                    turn = Math.Max(turn, Math.Abs(UMath.DeltaAngle(yaw, UMath.YawOf(body.Motor.Rotation))));
                }
            CorrectionSum += jump;
            WorstCorrection = Math.Max(WorstCorrection, jump);
            TurnCorrectionSum += turn;

            Rollbacks++;
            LastRollback = until - from;
            RolledTicks += until - from;
            DeepestRollback = Math.Max(DeepestRollback, until - from);
        }

        /// <summary>Steps new ticks up to the clock, never more than MaxPredict past the confirmed.</summary>
        void Advance(double now)
        {
            double wanted = ClockTick(now);
            for (int n = 0; n < MaxTicksPerUpdate && sim.CurrentTick < wanted; n++)
            {
                int tick = sim.CurrentTick;
                if (tick - confirmedTick > MaxPredict)
                {
                    // Too far ahead: the clock holds here instead of running on,
                    // so the lead the host measures is the clock's own again.
                    Stalls++;
                    syncTick = tick;
                    syncTime = now;
                    break;
                }

                if (sampleLocal != null && LocalNetId != 0)
                {
                    int at = tick + InputDelay;
                    // Clamped as the host would, so a stray value never makes us roll back ourselves.
                    local[at % Ring] = NetProtocol.Sanitize(sampleLocal());
                    localTicks[at % Ring] = at;
                    firstLocalTick = Math.Min(firstLocalTick, at);
                    newestLocalTick = at;
                }

                StepTick(tick);
                sendNow = true;
            }
        }

        /// <summary>
        /// One tick: records the state it starts from, applies its confirmed
        /// joins and leaves, and steps with the best input there is per body.
        /// </summary>
        void StepTick(int tick)
        {
            Capture(tick);
            confirmed.TryGetValue(tick, out Record rec);

            if (rec != null)
                foreach (NetEvent e in rec.Events)
                {
                    SimulationBody body = BodyOf(e.NetId);
                    if (body == null)
                        continue;
                    if (e.Join)
                        sim.Rejoin(body, joinStates[e.NetId]);
                    else
                        sim.Leave(body);
                }

            int slot = tick % Ring;
            List<Used> was = used[slot];
            was.Clear();
            usedTicks[slot] = tick;
            row.Clear();

            foreach (SimulationBody body in sim.Bodies)
            {
                MoveInput input = InputOf(body, tick, rec);
                row.Add(input);
                if (body.NetId >= NetWorld.FirstPlayerId)
                    was.Add(new Used { NetId = body.NetId, Input = input });
            }

            sim.Step(row, SimulationSystem.TickSeconds);
        }

        MoveInput InputOf(SimulationBody body, int tick, Record rec)
        {
            int id = body.NetId;
            if (id < NetWorld.FirstPlayerId)
                return body.ReadInput();

            if (rec != null)
                foreach (Used u in rec.Inputs)
                    if (u.NetId == id)
                        return u.Input;

            if (id == LocalNetId && localTicks[tick % Ring] == tick)
                return local[tick % Ring];

            if (KnownInputs != null && KnownInputs.IsBot(id) && KnownInputs.TryInput(id, tick, out MoveInput thought))
                return thought;

            return lastConfirmed.TryGetValue(id, out Known known) ? Guess(known, tick) : default;
        }

        /// <summary>
        /// What another player does in a tick not yet confirmed: the last
        /// input without its presses, still turning as fast as between its
        /// last two inputs, for at most TurnAhead ticks.
        /// </summary>
        MoveInput Guess(in Known k, int tick)
        {
            MoveInput m = NetProtocol.Hold(k.Last);
            int ahead = Math.Min(tick - k.Tick, TurnAhead);
            if (k.HasPrev && ahead > 0)
            {
                m.Yaw = k.Last.Yaw + ahead * UMath.DeltaAngle(k.Prev.Yaw, k.Last.Yaw);
                m.Pitch = UMath.Clamp(k.Last.Pitch + ahead * (k.Last.Pitch - k.Prev.Pitch), -89f, 89f);
            }
            return m;
        }

        void Capture(int tick)
        {
            int slot = tick % Ring;
            sim.Capture(snaps[slot]);
            snapTicks[slot] = tick;
        }

        /// <summary>
        /// Own inputs the host has not acknowledged, at most once per tick
        /// stepped or every 25 ms; it also carries how far we have confirmed.
        /// </summary>
        void SendInputs(double now)
        {
            if (!sendNow && now - lastSent < 25.0)
                return;
            sendNow = false;
            lastSent = now;

            int from = Math.Max(Math.Max(ackInputTick + 1, firstLocalTick), newestLocalTick - MaxInputsPerMessage + 1);
            int count = newestLocalTick >= from ? newestLocalTick - from + 1 : 0;

            outgoing.Clear();
            var w = new Writer(outgoing);
            w.Byte((byte)MsgType.Inputs);
            w.Double(now);
            w.Int(confirmedTick);
            w.Int(count > 0 ? from : 0);
            w.Byte((byte)count);
            for (int t = from; t < from + count; t++)
                NetProtocol.WriteInput(ref w, local[t % Ring]);
            transport.Send(0, outgoing.WrittenSpan, reliable: false);
        }

        /// <summary>Hashes every 40th state once nothing can change it, and tells the host.</summary>
        void ReportChecksums()
        {
            int final = FinalTick;
            for (int t = checkedTick + 1; t <= final; t++)
            {
                if (snapTicks[t % Ring] != t)
                    break;
                checkedTick = t;
                if (t % ChecksumEvery != 0)
                    continue;

                stateBuffer.Clear();
                StateCodec.Write(snaps[t % Ring], stateBuffer);
                ulong hash = StateCodec.Hash(stateBuffer.WrittenSpan);
                Checksum?.Invoke(t, hash);

                outgoing.Clear();
                var w = new Writer(outgoing);
                w.Byte((byte)MsgType.Checksum);
                w.Int(t);
                w.ULong(hash);
                transport.Send(0, outgoing.WrittenSpan, reliable: true);
            }
        }

        /// <summary>A body that left before the confirmed tick is out of every rollback's reach.</summary>
        void DropLeavers()
        {
            List<int> gone = null;
            foreach (KeyValuePair<int, int> leave in leaveTicks)
                if (leave.Value <= confirmedTick)
                    (gone ??= new List<int>()).Add(leave.Key);
            if (gone == null)
                return;

            foreach (int id in gone)
            {
                leaveTicks.Remove(id);
                if (bodies.Remove(id, out SimulationBody body))
                    world.Despawn(body);
                joinStates.Remove(id);
                lastConfirmed.Remove(id);
            }
        }

        /// <summary>A player as a join announces it: fresh at its slot, parked until its tick.</summary>
        void Spawn(in NetEvent e)
        {
            SimulationBody body = world.SpawnPlayer(e.NetId, e.Slot, e.Name);
            body.NetId = e.NetId;
            bodies[e.NetId] = body;
            joinStates[e.NetId] = body.CaptureState();
            sim.Leave(body);
        }
    }
}
