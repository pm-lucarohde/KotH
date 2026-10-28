using System;
using System.Buffers;
using System.Collections.Generic;
using KotH.Shared.Bots;
using KotH.Shared.Player;
using KotH.Shared.Simulation;

namespace KotH.Shared.Net
{
    /// <summary>
    /// The host's referee. Simulates nothing itself: it takes the peers'
    /// inputs, decides each tick's final inputs and joins and leaves, and
    /// sends them to everyone, its own founder peer included. A tick is
    /// final once every player's input is in, or a few ticks after it is
    /// due; a missing input becomes the player's last one without presses.
    /// </summary>
    public sealed class HostSession
    {
        /// <summary>Ticks past due the host waits for a late input before it decides without it.</summary>
        public const int ConfirmDelay = 3;

        const int MaxRecordsPerMessage = 24;
        const int SpareRecords = 8;     // newest plus spares per datagram, against loss
        const int KeepRecords = 600;
        const int LeadSamples = 20;
        public const int LocalPeer = -1;

        enum Phase { Waiting, Playing, Left }

        sealed class Client
        {
            public int Peer;
            public bool IsBot;
            public int NetId;
            public int Slot;
            public string Name;
            public Phase Phase;
            public int JoinTick = -1;
            public int LeaveTick = int.MaxValue;
            public int FirstInputTick = int.MaxValue;
            public int NewestInput = -1;
            public int AckConfirmed = -1;
            public MoveInput Last;
            public readonly Dictionary<int, MoveInput> Inputs = new Dictionary<int, MoveInput>();
            public double EchoTime, EchoReceived;
            public readonly Queue<float> Leads = new Queue<float>();
            public double LastSent = double.MinValue;
            public int SentThrough = -1;
            public double LastResend;

            // Round trip as the host sees it: from a tick going final to its
            // acknowledgement, the least of the recent ones.
            public double Rtt = 300.0;
            public readonly Queue<double> RttSamples = new Queue<double>();
            public long BytesSent;
        }

        sealed class Record
        {
            public readonly List<(int NetId, MoveInput Input)> Inputs = new List<(int, MoveInput)>();
            public readonly List<NetEvent> Events = new List<NetEvent>();
            public double At;   // when it became final
            public byte[] Encoded;
        }

        readonly INetTransport transport;
        readonly LocalLink link = new LocalLink();
        readonly double start;

        readonly Dictionary<int, Client> clients = new Dictionary<int, Client>();
        readonly Dictionary<int, Record> records = new Dictionary<int, Record>();
        readonly List<NetEvent> pending = new List<NetEvent>();
        readonly Dictionary<int, ulong> hostHashes = new Dictionary<int, ulong>();
        readonly Dictionary<(int Peer, int Tick), ulong> peerHashes = new Dictionary<(int, int), ulong>();
        readonly ArrayBufferWriter<byte> outgoing = new ArrayBufferWriter<byte>();
        readonly ArrayBufferWriter<byte> state = new ArrayBufferWriter<byte>();

        PeerSession founder;
        int nextConfirm;
        int nextNetId = NetWorld.FirstPlayerId;
        int nextBotPeer = LocalPeer - 1;
        double now;

        public HostSession(INetTransport transport, double now)
        {
            this.transport = transport;
            start = now;
            this.now = now;
        }

        /// <summary>What the host's own peer talks through: instant, never lost.</summary>
        public INetTransport LocalTransport => link.PeerSide;

        /// <summary>Where the host's bots get their inputs; needed once AddBot was called.</summary>
        public IBotInputs Bots { get; set; }

        /// <summary>
        /// A bot joins like a player, NetId and slot included, but nobody is
        /// on the other end: it plays at once, is never sent anything, and its
        /// inputs come from Bots in the tick they are confirmed.
        /// </summary>
        public int AddBot(string name)
        {
            var c = new Client
            {
                Peer = nextBotPeer--, IsBot = true, NetId = nextNetId++, Slot = FreeSlot(NetWorld.BotSlots),
                Name = UniqueName(name), Phase = Phase.Playing,
            };
            clients[c.Peer] = c;
            Players++;
            pending.Add(new NetEvent { Join = true, NetId = c.NetId, Slot = c.Slot, Name = c.Name });
            return c.NetId;
        }

        /// <summary>Whether a new player may join right now; the lobby decides (N5).</summary>
        public Func<bool> CanJoin { get; set; } = () => true;

        public int NextConfirm => nextConfirm;
        public int Players { get; private set; }
        public int Desyncs { get; private set; }
        public int Substituted { get; private set; }

        readonly ArrayBufferWriter<byte> oneRecord = new ArrayBufferWriter<byte>();

        static void WriteRecord(ref Writer w, Record rec)
        {
            w.Byte((byte)rec.Inputs.Count);
            foreach ((int id, MoveInput input) in rec.Inputs)
            {
                w.Int(id);
                NetProtocol.WriteInput(ref w, input);
            }
            w.Byte((byte)rec.Events.Count);
            foreach (NetEvent e in rec.Events)
                NetProtocol.WriteEvent(ref w, e);
        }

        /// <summary>Inputs the host had to clamp before confirming them; honest peers send none.</summary>
        public int Sanitized { get; private set; }

        /// <summary>Bytes of confirmed ticks sent to the peer with this NetId so far.</summary>
        public long BytesSentTo(int netId)
        {
            foreach (Client c in clients.Values)
                if (c.NetId == netId)
                    return c.BytesSent;
            return 0;
        }

        /// <summary>The host's own peer: its world is the game's, its states go to joiners.</summary>
        public void AttachFounder(PeerSession peer)
        {
            founder = peer;
            founder.Checksum += (tick, hash) =>
            {
                hostHashes[tick] = hash;
                hostHashes.Remove(tick - 50 * PeerSession.ChecksumEvery);
            };
        }

        double HostTick => (now - start) / 1000.0 / SimulationSystem.TickSeconds;

        public void Update(double time)
        {
            now = time;
            while (link.HostSide.Poll(out TransportEvent e))
                Handle(e);
            while (transport.Poll(out TransportEvent e))
                Handle(e);

            ConfirmReady();
            Welcome();
            CompareChecksums();
            Broadcast();
        }

        void Handle(in TransportEvent e)
        {
            switch (e.Kind)
            {
                case TransportEventKind.Disconnected:
                    if (clients.TryGetValue(e.Peer, out Client gone))
                        Leave(gone);
                    break;
                case TransportEventKind.Data:
                    if (e.Data.Length > 0)
                        Receive(e.Peer, e.Data);
                    break;
            }
        }

        void Receive(int peer, byte[] data)
        {
            var r = new Reader(data);
            var type = (MsgType)r.Byte();
            if (type == MsgType.Hello)
            {
                Hello(peer, ref r);
                return;
            }
            if (type == MsgType.Ask)
            {
                Ask(peer, ref r);
                return;
            }
            if (!clients.TryGetValue(peer, out Client c) || c.Phase == Phase.Left)
                return;

            if (type == MsgType.Inputs)
                Inputs(c, ref r);
            else if (type == MsgType.Checksum)
            {
                int tick = r.Int();
                ulong hash = r.ULong();
                if (!r.Failed)
                    peerHashes[(peer, tick)] = hash;
            }
        }

        // A peer about to join asks for the map first: it loads it before its Hello.
        void Ask(int peer, ref Reader r)
        {
            ReadOnlySpan<byte> build = r.Bytes(NetProtocol.BuildId.Length);
            if (r.Failed || clients.ContainsKey(peer) || Refused(peer, build))
                return;
            outgoing.Clear();
            var w = new Writer(outgoing);
            w.Byte((byte)MsgType.Map);
            w.Text(founder?.Map ?? "");
            Send(peer, reliable: true);
        }

        // Rejects a peer of another build, into a full game or a closed round; true then.
        bool Refused(int peer, ReadOnlySpan<byte> build)
        {
            RejectReason? why = !build.SequenceEqual(NetProtocol.BuildId) ? RejectReason.Version
                              : Players >= NetWorld.MaxPlayers ? RejectReason.Full
                              : !CanJoin() ? RejectReason.Closed
                              : null;
            if (why == null)
                return false;
            outgoing.Clear();
            var w = new Writer(outgoing);
            w.Byte((byte)MsgType.Reject);
            w.Byte((byte)why.Value);
            Send(peer, reliable: true);
            return true;
        }

        void Hello(int peer, ref Reader r)
        {
            ReadOnlySpan<byte> build = r.Bytes(NetProtocol.BuildId.Length);
            double sent = r.Double();
            string name = r.Text();
            if (r.Failed || clients.ContainsKey(peer) || Refused(peer, build))
                return;

            name = UniqueName(string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim());
            var c = new Client
            {
                Peer = peer, NetId = nextNetId++, Slot = FreeSlot(), Name = name,
                Phase = Phase.Waiting, EchoTime = sent, EchoReceived = now,
            };
            clients[peer] = c;
            Players++;
            pending.Add(new NetEvent { Join = true, NetId = c.NetId, Slot = c.Slot, Name = name });
        }

        // A bot's slot: the first free one in the given order.
        int FreeSlot(int[] order)
        {
            foreach (int slot in order)
            {
                bool taken = false;
                foreach (Client c in clients.Values)
                    if (c.Phase != Phase.Left && c.Slot == slot)
                        taken = true;
                if (!taken)
                    return slot;
            }
            return FreeSlot();
        }

        int FreeSlot()
        {
            for (int slot = 0; ; slot++)
            {
                bool taken = false;
                foreach (Client c in clients.Values)
                    if (c.Phase != Phase.Left && c.Slot == slot)
                        taken = true;
                if (!taken)
                    return slot;
            }
        }

        void Inputs(Client c, ref Reader r)
        {
            double sent = r.Double();
            int ack = r.Int();
            int first = r.Int();
            int count = r.Byte();

            c.EchoTime = sent;
            c.EchoReceived = now;
            if (ack > c.AckConfirmed && records.TryGetValue(ack, out Record acked))
            {
                c.RttSamples.Enqueue(now - acked.At);
                while (c.RttSamples.Count > LeadSamples)
                    c.RttSamples.Dequeue();
                c.Rtt = double.MaxValue;
                foreach (double sample in c.RttSamples)
                    c.Rtt = Math.Min(c.Rtt, sample);
            }
            c.AckConfirmed = Math.Max(c.AckConfirmed, ack);

            for (int i = 0; i < count; i++)
            {
                MoveInput input = NetProtocol.ReadInput(ref r);
                int tick = first + i;
                if (r.Failed)
                    return;
                if (tick >= nextConfirm && tick >= c.JoinTick)
                {
                    MoveInput clean = NetProtocol.Sanitize(input);
                    if (!NetProtocol.Same(clean, input))
                        Sanitized++;
                    c.Inputs[tick] = clean;
                }
                c.FirstInputTick = Math.Min(c.FirstInputTick, tick);
                c.NewestInput = Math.Max(c.NewestInput, tick);
            }

            if (count > 0)
            {
                c.Leads.Enqueue((float)(first + count - 1 - HostTick));
                while (c.Leads.Count > LeadSamples)
                    c.Leads.Dequeue();
            }
        }

        /// <summary>A name nobody in the game has, "Luca 2" for the second Luca.</summary>
        string UniqueName(string wanted)
        {
            if (wanted.Length > 24)
                wanted = wanted[..24];
            for (int n = 1; ; n++)
            {
                string name = n == 1 ? wanted : $"{wanted} {n}";
                bool taken = false;
                foreach (Client other in clients.Values)
                    taken |= other.Phase != Phase.Left && other.Name == name;
                if (!taken)
                    return name;
            }
        }

        void Leave(Client c)
        {
            if (c.Phase == Phase.Left)
                return;
            c.Phase = Phase.Left;
            Players--;
            pending.Add(new NetEvent { Join = false, NetId = c.NetId });
        }

        /// <summary>
        /// Finalises ticks in order: as soon as every player's input is in,
        /// at most LeadTarget ticks before they are due, or ConfirmDelay
        /// ticks after they were due with whatever is there.
        /// </summary>
        void ConfirmReady()
        {
            double hostTick = HostTick;
            while (true)
            {
                int t = nextConfirm;
                bool complete = true;
                foreach (Client c in clients.Values)
                    if (Present(c, t) && c.Phase == Phase.Playing && t >= c.FirstInputTick && !c.Inputs.ContainsKey(t))
                        complete = false;

                bool due = t + ConfirmDelay <= hostTick;
                if (!(complete && t < hostTick + PeerSession.LeadTarget) && !due)
                    return;

                // A bot's input needs a final state from before; never guessed.
                foreach (Client c in clients.Values)
                    if (c.IsBot && c.Phase != Phase.Left && t < c.LeaveTick && !Bots.TryInput(c.NetId, t, out _))
                        return;

                Finalise(t);
            }
        }

        static bool Present(Client c, int tick) => c.JoinTick >= 0 && c.JoinTick <= tick && tick < c.LeaveTick;

        void Finalise(int t)
        {
            var rec = new Record();

            // Joins and leaves decided since the last tick take effect here.
            foreach (NetEvent e in pending)
            {
                rec.Events.Add(e);
                foreach (Client c in clients.Values)
                    if (c.NetId == e.NetId)
                    {
                        if (e.Join)
                            c.JoinTick = t;
                        else
                            c.LeaveTick = t;
                    }
            }
            pending.Clear();

            var present = new List<Client>();
            foreach (Client c in clients.Values)
                if (Present(c, t))
                    present.Add(c);
            present.Sort((a, b) => a.NetId.CompareTo(b.NetId));

            foreach (Client c in present)
            {
                if (c.IsBot)
                {
                    Bots.TryInput(c.NetId, t, out MoveInput thought);
                    rec.Inputs.Add((c.NetId, thought));
                    continue;
                }
                if (c.Inputs.Remove(t, out MoveInput input))
                    c.Last = input;
                else
                {
                    input = NetProtocol.Hold(c.Last);
                    if (c.Phase == Phase.Playing && t >= c.FirstInputTick)
                        Substituted++;
                }
                rec.Inputs.Add((c.NetId, input));
            }

            rec.At = now;
            oneRecord.Clear();
            var rw = new Writer(oneRecord);
            WriteRecord(ref rw, rec);
            rec.Encoded = oneRecord.WrittenSpan.ToArray();
            records[t] = rec;
            records.Remove(t - KeepRecords);
            nextConfirm = t + 1;

            // Inputs for ticks now final arrived too late; forget them.
            foreach (Client c in clients.Values)
                c.Inputs.Remove(t);
        }

        /// <summary>
        /// A waiting player gets the world once a final state includes it:
        /// the founder's, at the first tick after its join or later. The
        /// founder itself already has the world and only learns its NetId.
        /// </summary>
        void Welcome()
        {
            foreach (Client c in clients.Values)
            {
                if (c.Phase != Phase.Waiting || c.JoinTick < 0)
                    continue;

                state.Clear();
                int tick;
                bool isFounder = c.Peer == LocalPeer;
                if (!isFounder && (founder == null || !founder.TryEncodeFinal(c.JoinTick + 1, state, out tick)))
                    continue;
                if (isFounder)
                    tick = c.JoinTick;
                else
                    tick = ReadTick(state.WrittenSpan);

                outgoing.Clear();
                var w = new Writer(outgoing);
                w.Byte((byte)MsgType.Welcome);
                w.Int(c.NetId);
                w.Int((int)HostTick);
                w.Double(c.EchoTime + (now - c.EchoReceived));
                w.Text(founder?.Map ?? "");
                WriteState(ref w, isFounder);
                Send(c.Peer, reliable: true);

                c.Phase = Phase.Playing;
                c.AckConfirmed = isFounder ? -1 : tick - 1;
            }
        }

        void WriteState(ref Writer w, bool none)
        {
            if (none)
            {
                w.Int(0);
                w.Int(0);
                return;
            }

            w.Int(state.WrittenCount);
            w.Bytes(state.WrittenSpan);
            var roster = new List<Client>();
            foreach (Client c in clients.Values)
                if (c.Phase != Phase.Left && c.JoinTick >= 0)
                    roster.Add(c);
            w.Int(roster.Count);
            foreach (Client c in roster)
                NetProtocol.WriteEvent(ref w, new NetEvent { Join = true, NetId = c.NetId, Slot = c.Slot, Name = c.Name });
        }

        // The tick sits after the codec's magic and version.
        static int ReadTick(ReadOnlySpan<byte> state) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(state[8..]);

        /// <summary>A peer whose checksum differs from the founder's gets the founder's state.</summary>
        void CompareChecksums()
        {
            List<(int, int)> done = null;
            foreach (KeyValuePair<(int Peer, int Tick), ulong> p in peerHashes)
            {
                if (!hostHashes.TryGetValue(p.Key.Tick, out ulong mine))
                {
                    if (p.Key.Tick < nextConfirm - 50 * PeerSession.ChecksumEvery)
                        (done ??= new List<(int, int)>()).Add(p.Key);
                    continue;
                }
                (done ??= new List<(int, int)>()).Add(p.Key);
                if (mine == p.Value || !clients.TryGetValue(p.Key.Peer, out Client c) || c.Phase != Phase.Playing)
                    continue;

                Desyncs++;
                state.Clear();
                if (founder == null || !founder.TryEncodeFinal(0, state, out int tick))
                    continue;
                outgoing.Clear();
                var w = new Writer(outgoing);
                w.Byte((byte)MsgType.Repair);
                WriteState(ref w, false);
                Send(c.Peer, reliable: true);
                c.AckConfirmed = tick - 1;
            }

            if (done != null)
                foreach ((int, int) key in done)
                    peerHashes.Remove(key);
        }

        /// <summary>
        /// Final ticks and the clock feedback to every peer: whenever new
        /// ticks are final, when an acknowledgement is overdue, else every
        /// 50 ms as a heartbeat. Each datagram stays within MaxDatagram.
        /// </summary>
        void Broadcast()
        {
            foreach (Client c in clients.Values)
            {
                if (c.Phase != Phase.Playing || c.IsBot)
                    continue;

                // The newest ticks go out at once, with as many older ones as
                // still fit, in case one got lost. From the oldest unacknowledged
                // only once its acknowledgement is overdue.
                int unacked = c.AckConfirmed + 1;
                int last = nextConfirm - 1;
                c.SentThrough = Math.Max(c.SentThrough, c.AckConfirmed);
                bool fresh = last > c.SentThrough;
                bool overdue = unacked <= c.SentThrough && records.TryGetValue(unacked, out Record oldest) &&
                               now - oldest.At > c.Rtt * 1.5 + 40.0 && now - c.LastResend > c.Rtt;
                if (!fresh && !overdue && now - c.LastSent < 50.0)
                    continue;
                if (unacked <= last && !records.ContainsKey(unacked))
                    continue;   // fell out of the history; a repair will catch it up

                const int Head = 1 + 4 + 8 + 4 + 4 + 4 + 1;
                int first, fitting = 0, size = Head;
                if (c.Peer == LocalPeer && fresh)
                {
                    // The own peer's line loses nothing: everything new, in order,
                    // or after a load pause it would wait for resends, and the
                    // bots for the final states it then lacks.
                    for (first = c.SentThrough + 1; first <= last; first += fitting)
                    {
                        fitting = Math.Min(MaxRecordsPerMessage, last - first + 1);
                        SendRecords(c, first, fitting);
                    }
                    continue;
                }
                if (overdue)
                {
                    c.LastResend = now;
                    first = unacked;
                    while (first + fitting <= last && fitting < MaxRecordsPerMessage &&
                           (fitting == 0 || size + records[first + fitting].Encoded.Length <= NetProtocol.MaxDatagram))
                        size += records[first + fitting++].Encoded.Length;
                }
                else
                {
                    first = last + 1;
                    while (first - 1 >= unacked && fitting < SpareRecords &&
                           (fitting == 0 || size + records[first - 1].Encoded.Length <= NetProtocol.MaxDatagram))
                    {
                        size += records[--first].Encoded.Length;
                        fitting++;
                    }
                    if (fitting == 0)
                        first = unacked;
                }
                SendRecords(c, first, fitting);
            }

            // Gone players stay until every peer has their leave.
            List<int> drop = null;
            foreach (Client c in clients.Values)
                if (c.Phase == Phase.Left && c.LeaveTick < nextConfirm - KeepRecords)
                    (drop ??= new List<int>()).Add(c.Peer);
            if (drop != null)
                foreach (int peer in drop)
                    clients.Remove(peer);
        }

        /// <summary>Confirmed ticks from first on, with the clock feedback, to one peer.</summary>
        void SendRecords(Client c, int first, int fitting)
        {
            c.LastSent = now;
            outgoing.Clear();
            var w = new Writer(outgoing);
            w.Byte((byte)MsgType.Confirmed);
            w.Int((int)HostTick);
            w.Double(c.EchoTime + (now - c.EchoReceived));
            w.Float(MinLead(c));
            w.Int(c.NewestInput);
            w.Int(first);
            w.Byte((byte)fitting);
            for (int t = first; t < first + fitting; t++)
                w.Bytes(records[t].Encoded);
            c.SentThrough = Math.Max(c.SentThrough, first + fitting - 1);
            c.BytesSent += outgoing.WrittenCount;
            Send(c.Peer, reliable: false);
        }

        // The worst recent lead: inputs must be early even when the network jitters.
        static float MinLead(Client c)
        {
            if (c.Leads.Count == 0)
                return float.NaN;
            float min = float.MaxValue;
            foreach (float l in c.Leads)
                min = Math.Min(min, l);
            return min;
        }

        void Send(int peer, bool reliable)
        {
            if (peer == LocalPeer)
                link.HostSide.Send(0, outgoing.WrittenSpan, reliable);
            else
                transport.Send(peer, outgoing.WrittenSpan, reliable);
        }

        /// <summary>
        /// The founder's line to the host, in the same process: instant and
        /// lossless both ways. The host sees the founder as LocalPeer.
        /// </summary>
        sealed class LocalLink
        {
            public readonly Side HostSide, PeerSide;

            public LocalLink()
            {
                HostSide = new Side(LocalPeer);
                PeerSide = new Side(0);
                HostSide.Other = PeerSide;
                PeerSide.Other = HostSide;
            }

            public sealed class Side : INetTransport
            {
                readonly Queue<TransportEvent> inbox = new Queue<TransportEvent>();
                readonly int seenAs;
                public Side Other;

                public Side(int seenAs)
                {
                    this.seenAs = seenAs;
                }

                public void Send(int peer, ReadOnlySpan<byte> data, bool reliable)
                    => Other.inbox.Enqueue(new TransportEvent
                    {
                        Kind = TransportEventKind.Data, Peer = Other == null ? 0 : OtherName, Data = data.ToArray(),
                    });

                // How the other side names this one.
                int OtherName => seenAs == LocalPeer ? 0 : LocalPeer;

                public bool Poll(out TransportEvent e) => inbox.TryDequeue(out e);

                public void Disconnect(int peer) { }
            }
        }
    }
}
