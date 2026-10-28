using System;
using System.Collections.Generic;
using System.Diagnostics;
using KotH.Probes.Harness;
using KotH.Shared.Combat;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Net;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    /// <summary>A whole rollback game over a seeded in-memory network: host and up to eleven peers,
    /// 15-75 ms one way, jitter, 5 % loss; joins at start and mid-game, a leave, a damaged state, a
    /// wrong build, a late joiner, lobby and countdown. Every final state a peer reports must equal
    /// the host's (the damaged one after repair), and rollbacks must have happened.</summary>
    public sealed class NetPlayProbe : Probe
    {
        protected override string Tag => "netplay";

        const double FrameMs = 8.0;
        const double EndMs = 60000.0, SettleMs = 4000.0;
        const double LateJoinMs = 20000.0, LeaveMs = 30000.0, DamageMs = 35000.0, WrongBuildMs = 40000.0, TooLateMs = 45000.0;
        const int CountdownTicks = 400, JoinTicks = 800;
        const int EarlyPeers = 6, LatePeers = 4;
        const double LoadMs = 1000.0;    // how long a late peer takes to load the map before its Hello
        const int Seed = 20261021;

        sealed class Peer
        {
            public string Name;
            public HeadlessWorld World;
            public PeerSession Session;
            public MemoryNetwork.Endpoint Endpoint;
            public Random Bot;
            public MoveInput Held;
            public int HoldTicks;
            public float Turn;
            public bool Gone;
            public bool Asking;          // asked for the map, joins once it has "loaded" it
            public double? MapAt;
            public readonly Dictionary<int, ulong> Hashes = new Dictionary<int, ulong>();

            // Every report, also a tick reported again after a repair.
            public readonly List<(int Tick, ulong Hash)> Reports = new List<(int, ulong)>();
            public double WorstUpdateMs;
        }

        readonly List<Peer> peers = new List<Peer>();
        MemoryNetwork net;
        MemoryNetwork.Endpoint hostEnd;
        HostSession host;
        bool botsOn = true;
        int failures;

        public override int Run(HeadlessWorld unused, int maxTicks)
        {
            net = new MemoryNetwork(Seed);
            hostEnd = net.CreateEndpoint();
            host = new HostSession(hostEnd, net.Now);
            Peer founder = AddPeer("Host", host.LocalTransport, founder: true);
            host.AttachFounder(founder.Session);
            HillMatch round = founder.World.Simulation.Match;
            round.MinPlayers = HillMatch.DefaultMinPlayers;
            host.CanJoin = () => round.AcceptsJoin;
            int countdownAt = -1, runningAt = -1;

            var rng = new Random(Seed);
            var joins = new List<double>();
            for (int i = 0; i < EarlyPeers; i++)
                joins.Add(300.0 * i);
            for (int i = 0; i < LatePeers; i++)
                joins.Add(LateJoinMs + 700.0 * i);

            int damagedTick = -1, leaverIndex = -1;
            Peer damaged = null;
            MemoryNetwork.Endpoint wrong = null, tooLate = null;
            bool rejected = false, closed = false;
            var stopwatch = Stopwatch.StartNew();

            while (net.Now < EndMs + SettleMs)
            {
                while (joins.Count > 0 && joins[0] <= net.Now)
                {
                    joins.RemoveAt(0);
                    var end = net.CreateEndpoint();
                    // The last one has a bad line: long, jittery and lossy.
                    bool bad = joins.Count == 0;
                    double latency = bad ? 140.0 : 15.0 + rng.NextDouble() * 60.0;
                    net.Connect(hostEnd, end, latency, bad ? 70.0 : latency * 0.15, bad ? 0.10 : 0.05);
                    // The late ones join as the client does: map first, Hello once loaded.
                    Peer p = AddPeer($"P{peers.Count}", end, founder: false, ask: net.Now >= LateJoinMs);
                    p.Endpoint = end;
                    Log($"[netplay] {p.Name} connects at {net.Now / 1000:F1} s, {latency:F0} ms one way");
                }

                if (leaverIndex < 0 && net.Now >= LeaveMs)
                {
                    leaverIndex = 2;
                    Peer leaver = peers[leaverIndex];
                    leaver.Endpoint.Disconnect(0);
                    leaver.Gone = true;
                    Log($"[netplay] {leaver.Name} leaves at {net.Now / 1000:F1} s");
                }

                if (damaged == null && net.Now >= DamageMs)
                {
                    // A desync on purpose: the first dummy's percent changes in one
                    // peer's final state, which no rollback of its own undoes.
                    damaged = peers[4];
                    var bytes = new System.Buffers.ArrayBufferWriter<byte>();
                    damaged.Session.TryEncodeFinal(0, bytes, out damagedTick);
                    byte[] state = bytes.WrittenSpan.ToArray();
                    const int firstBody = StateCodec.HeadBytes + StateCodec.MatchBytes, percent = 5 + 186 + 4;
                    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(state.AsSpan(firstBody + percent), 777f);
                    bool loaded = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(firstBody)) == 1 &&
                                  damaged.Session.LoadState(state);
                    Check(loaded, $"{damaged.Name} at tick {damagedTick} tampered (dummy 1 to 777 %)");
                }

                if (wrong == null && net.Now >= WrongBuildMs)
                    wrong = Knock(new byte[NetProtocol.BuildId.Length], "Wrong");
                rejected |= Refused(wrong, RejectReason.Version);
                if (tooLate == null && net.Now >= TooLateMs)
                    tooLate = Knock(NetProtocol.BuildId, "Late");
                closed |= Refused(tooLate, RejectReason.Closed);

                MatchPhase phase = round.State.Phase;
                if (countdownAt < 0 && phase == MatchPhase.Countdown)
                    countdownAt = founder.World.Simulation.CurrentTick;
                if (runningAt < 0 && countdownAt >= 0 && phase == MatchPhase.Running)
                    runningAt = founder.World.Simulation.CurrentTick;

                botsOn = net.Now < EndMs;
                host.Update(net.Now);
                foreach (Peer p in peers)
                {
                    if (p.Gone)
                        continue;
                    if (p.Asking && p.Session.MapKnown && (p.MapAt ??= net.Now) + LoadMs <= net.Now)
                    {
                        p.Session.Join(net.Now);
                        p.Asking = false;
                    }
                    long t0 = Stopwatch.GetTimestamp();
                    p.Session.Update(net.Now);
                    p.WorstUpdateMs = Math.Max(p.WorstUpdateMs, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                }
                net.Now += FrameMs;
            }

            Log($"[netplay] {EndMs / 1000 + SettleMs / 1000:F0} s played in {stopwatch.Elapsed.TotalSeconds:F1} s, " +
                $"{net.Sent} packets, {net.Lost} lost, {net.Bytes / 1024} KiB, host confirmed up to {host.NextConfirm - 1}, " +
                $"{host.Substituted} inputs substituted");
            Log($"[netplay] Build {NetProtocol.BuildText}");

            Report(founder, damaged, damagedTick, rejected);
            Check(closed, "join after the window rejected");
            Check(host.Sanitized == 0, $"honest inputs never clamped ({host.Sanitized})");
            Sanitizing();
            Check(countdownAt > 0 && runningAt - countdownAt >= CountdownTicks && runningAt - countdownAt < CountdownTicks + 40,
                  $"countdown from tick {countdownAt}, round from tick {runningAt}");
            // The camera shake keys on the tick of the last hit taken.
            int marked = 0;
            foreach (SimulationBody body in founder.World.Simulation.Bodies)
                if (body.Entity.Get<DamagePercent>() is { LastHitTick: >= 0 } d && d.LastHitTick < founder.World.Simulation.CurrentTick)
                    marked++;
            Check(marked > 0, $"{marked} bodies carry the tick of their last hit");

            foreach (Peer p in peers)
                p.World.Dispose();
            LoadPause();
            Log(failures == 0 ? "[netplay] PASSED" : $"[netplay] {failures} errors");
            return failures == 0 ? 0 : 1;
        }

        Peer AddPeer(string name, INetTransport transport, bool founder, bool ask = false)
        {
            var world = new HeadlessWorld();
            world.LoadArena(Maps.Test);
            NetWorld.SpawnDummies(world.Builder);
            HillMatch match = world.StartMatch();
            match.CountdownTicks = CountdownTicks;
            match.JoinTicks = JoinTicks;
            world.Simulation.Deactivate();

            var p = new Peer { Name = name, World = world, Bot = new Random(Seed + peers.Count * 7919), Asking = ask };
            p.Session = new PeerSession(new HeadlessNetWorld(world), transport, () => BotInput(p), name, founder, net.Now, join: !ask);
            if (int.TryParse(Environment.GetEnvironmentVariable("KOTH_TURN_AHEAD"), out int ahead))
                p.Session.TurnAhead = ahead;
            p.Session.Checksum += (tick, hash) =>
            {
                p.Hashes[tick] = hash;
                p.Reports.Add((tick, hash));
            };
            peers.Add(p);
            return p;
        }

        /// <summary>A hosting client says hello while still loading and runs its first
        /// frame 1.5 s later: its clock starts far ahead and has to come back, not wobble
        /// on stale measurements. Waiting is fine while it settles, not after the first 6
        /// s.</summary>
        void LoadPause()
        {
            peers.Clear();
            net = new MemoryNetwork(Seed + 1);
            hostEnd = net.CreateEndpoint();
            host = new HostSession(hostEnd, net.Now);
            Peer founder = AddPeer("Host", host.LocalTransport, founder: true);
            host.AttachFounder(founder.Session);
            net.Now = 1500.0;

            var end = net.CreateEndpoint();
            net.Connect(hostEnd, end, 15.0, 2.0, 0.0);
            AddPeer("Gast", end, founder: false);

            int settled = -1;
            while (net.Now < 12000.0)
            {
                if (settled < 0 && net.Now >= 6000.0)
                    settled = founder.Session.Stalls;
                host.Update(net.Now);
                foreach (Peer p in peers)
                    p.Session.Update(net.Now);
                net.Now += FrameMs;
            }

            PeerSession s = founder.Session;
            Check(s.Stalls == settled && Math.Abs(s.Lead - PeerSession.LeadTarget) <= 1.0,
                  $"host after 1.5 s load pause: lead {s.Lead:F1}, {settled} waits during start-up, then {s.Stalls - settled}");
            foreach (Peer p in peers)
                p.World.Dispose();
        }

        /// <summary>What the host does to bad inputs, and that it leaves good ones alone.</summary>
        void Sanitizing()
        {
            var bad = new MoveInput
            {
                Move = new Vector2(float.NaN, -7f), Yaw = float.PositiveInfinity, Pitch = float.NaN,
                JumpPresses = 200, AttackPresses = 9,
            };
            MoveInput clean = NetProtocol.Sanitize(bad);
            Check(clean.Move == new Vector2(0f, -1f) && clean.Yaw == NetProtocol.MaxYaw && clean.Pitch == 0f &&
                  clean.JumpPresses == NetProtocol.MaxPresses && clean.AttackPresses == NetProtocol.MaxPresses,
                  "NaN, infinity, too large movement and key presses are clamped");

            var r = new Random(Seed);
            int changed = 0;
            for (int i = 0; i < 100000; i++)
            {
                var good = new MoveInput
                {
                    Move = new Vector2((float)(r.NextDouble() * 2 - 1), (float)(r.NextDouble() * 2 - 1)),
                    Yaw = (float)((r.NextDouble() * 2 - 1) * 1e5), Pitch = (float)(r.NextDouble() * 178 - 89),
                    JumpPresses = (byte)r.Next(3), AttackPresses = (byte)r.Next(3), Sprint = r.Next(2) == 0,
                };
                if (!NetProtocol.Same(NetProtocol.Sanitize(good), good))
                    changed++;
            }
            Check(changed == 0, $"100 000 valid inputs passed bit-identical ({changed} changed)");
        }

        /// <summary>Says hello from a fresh endpoint with the given build, like a peer would.</summary>
        MemoryNetwork.Endpoint Knock(byte[] build, string name)
        {
            var end = net.CreateEndpoint();
            net.Connect(hostEnd, end, 20.0, 0.0, 0.0);
            var hello = new System.Buffers.ArrayBufferWriter<byte>();
            var w = new Writer(hello);
            w.Byte((byte)MsgType.Hello);
            w.Bytes(build);
            w.Double(net.Now);
            w.Text(name);
            end.Send(0, hello.WrittenSpan, reliable: true);
            return end;
        }

        static bool Refused(MemoryNetwork.Endpoint end, RejectReason why)
        {
            bool refused = false;
            while (end != null && end.Poll(out TransportEvent e))
                refused |= e.Kind == TransportEventKind.Data && e.Data[0] == (byte)MsgType.Reject && e.Data[1] == (byte)why;
            return refused;
        }

        /// <summary>Holds a random input for a while, with presses now and then; idle once the bots stop.</summary>
        MoveInput BotInput(Peer p)
        {
            if (!botsOn)
                return new MoveInput { Yaw = p.Held.Yaw };

            if (--p.HoldTicks <= 0)
            {
                Random r = p.Bot;
                p.HoldTicks = 10 + r.Next(40);
                // Turns like a mouse: steady for a while, a third of the time not at all.
                p.Turn = r.Next(3) == 0 ? 0f : (float)(r.NextDouble() * 8.0 - 4.0);
                p.Held = new MoveInput
                {
                    Move = new Vector2(r.Next(3) - 1, r.Next(3) - 1),
                    Yaw = p.Held.Yaw,
                    Pitch = (float)(r.NextDouble() * 40.0 - 20.0),
                    Sprint = r.Next(3) == 0,
                    Crouch = r.Next(12) == 0,
                };
            }

            MoveInput m = p.Held;
            Random b = p.Bot;
            if (p.Turn != 0f)
                m.Yaw += p.Turn + (float)(b.NextDouble() - 0.5) * 0.3f;
            p.Held.Yaw = m.Yaw;
            if (b.Next(30) == 0) { m.JumpPresses = 1; m.JumpHeld = true; }
            if (b.Next(25) == 0) m.AttackPresses = 1;
            return m;
        }

        void Report(Peer founder, Peer damaged, int damagedTick, bool rejected)
        {
            int compared = 0, mismatches = 0, damagedMismatches = 0, lastDamagedMismatch = -1;
            foreach (Peer p in peers)
            {
                if (p == founder)
                    continue;
                foreach ((int at, ulong hash) in p.Reports)
                {
                    if (!founder.Hashes.TryGetValue(at, out ulong mine))
                        continue;
                    compared++;
                    if (mine == hash)
                        continue;
                    if (p == damaged && at >= damagedTick)
                    {
                        damagedMismatches++;
                        lastDamagedMismatch = Math.Max(lastDamagedMismatch, at);
                    }
                    else
                    {
                        mismatches++;
                        if (mismatches <= 5)
                            LogError($"[netplay] {p.Name} differs at tick {at} from the host");
                    }
                }
            }

            Check(compared > 400, $"{compared} checksums compared with the host");
            Check(mismatches == 0, $"without interference not a single desync ({mismatches})");
            Check(damagedMismatches > 0 && host.Desyncs > 0 && damaged.Session.Repairs > 0,
                  $"tampered peer detected ({damagedMismatches} checksums different, {host.Desyncs} desyncs) and repaired ({damaged.Session.Repairs})");
            Check(lastDamagedMismatch >= 0 && lastDamagedMismatch < damagedTick + 4 * PeerSession.ChecksumEvery,
                  $"same again after the repair (last deviation tick {lastDamagedMismatch}, tampered at {damagedTick})");
            Check(rejected, "wrong build rejected");

            int joined = 0, rollbacks = 0, deepest = 0, stalls = 0;
            double worst = 0, jumps = 0, turns = 0;
            float worstJump = 0f;
            foreach (Peer p in peers)
            {
                if (p.Session.Joined)
                    joined++;
                rollbacks += p.Session.Rollbacks;
                deepest = Math.Max(deepest, p.Session.DeepestRollback);
                stalls += p.Session.Stalls;
                jumps += p.Session.CorrectionSum;
                turns += p.Session.TurnCorrectionSum;
                worstJump = Math.Max(worstJump, p.Session.WorstCorrection);
                worst = Math.Max(worst, p.WorstUpdateMs);
                Log($"[netplay] {p.Name,-5} NetId {p.Session.LocalNetId,2}, Ping {p.Session.RoundTripMs,5:F0} ms, " +
                    $"lead {p.Session.Lead,5:F1}, Rollbacks {p.Session.Rollbacks,5} ({p.Session.RolledTicks} ticks, " +
                    $"deepest {p.Session.DeepestRollback}), stalls {p.Session.Stalls}, repairs {p.Session.Repairs}, " +
                    $"confirmed up to {p.Session.ConfirmedTick}, worst update {p.WorstUpdateMs:F1} ms, " +
                    $"from the host {host.BytesSentTo(p.Session.LocalNetId) / 1024.0 / (EndMs + SettleMs) * 1000:F1} KiB/s");
            }

            Check(joined == peers.Count, $"all {peers.Count} joined, the late ones too (first Map, then Hello)");
            Check(rollbacks > 100, $"{rollbacks} rollbacks, so the prediction was often off");
            Check(deepest <= PeerSession.MaxPredict, $"deepest rollback {deepest} ticks, at most {PeerSession.MaxPredict}");
            Log($"[netplay] stalls in total {stalls}, worst update {worst:F1} ms");
            Log($"[netplay] turn {peers[0].Session.TurnAhead} ticks extrapolated: mean correction " +
                $"{jumps / Math.Max(rollbacks, 1) * 100:F1} cm and {turns / Math.Max(rollbacks, 1):F2}°, largest {worstJump * 100:F0} cm");

            // Everyone still in the game reaches the same final tick with the same state.
            int last = int.MaxValue;
            foreach (Peer p in peers)
                if (!p.Gone)
                    last = Math.Min(last, p.Session.ConfirmedTick);
            int tick = last / PeerSession.ChecksumEvery * PeerSession.ChecksumEvery;
            bool same = true;
            foreach (Peer p in peers)
                if (!p.Gone && (!p.Hashes.TryGetValue(tick, out ulong h) || h != founder.Hashes[tick]))
                    same = false;
            Check(same, $"at the end all at tick {tick} bit-identical with the host");
        }

        void Check(bool ok, string what)
        {
            if (!ok)
                failures++;
            Log($"[netplay] {(ok ? "ok    " : "ERROR ")} {what}");
        }
    }
}
