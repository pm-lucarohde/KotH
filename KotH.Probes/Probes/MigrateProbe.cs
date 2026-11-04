using System;
using System.Collections.Generic;
using KotH.Probes.Harness;
using KotH.Shared.Bots;
using KotH.Shared.Hosting;
using KotH.Shared.Net;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    /// <summary>A host change over the in-memory network: a host with two bots and two players; at 8 s
    /// the host is gone, player A takes the game over from its newest final state (bots included) and B
    /// says hello to A as who it was. Then every final state A and B report must agree, the bots go on,
    /// the old host's body leaves after the wait, and a peer claiming a NetId it never had joins anew.</summary>
    public sealed class MigrateProbe : Probe
    {
        protected override string Tag => "migrate";

        const double FrameMs = 8.0, HostDiesMs = 8000.0, NoticeMs = 1500.0, ImpostorMs = 12000.0, EndMs = 34000.0;
        const int Seed = 20261104;

        sealed class Peer
        {
            public string Name;
            public HeadlessWorld World;
            public PeerSession Session;
            public int Turn;
            public readonly Dictionary<int, ulong> Hashes = new Dictionary<int, ulong>();
        }

        MemoryNetwork net;
        int failures;

        public override int Run(HeadlessWorld unused, int maxTicks)
        {
            net = new MemoryNetwork(Seed);
            var hostEnd = net.CreateEndpoint();
            var host = new HostSession(hostEnd, net.Now);
            Peer founder = Make("Host", host.LocalTransport, founder: true);
            host.AttachFounder(founder.Session);
            BotDirector bots = BotDirector.Create(founder.World.Builder, founder.World.Simulation, founder.Session, Seed);
            host.Bots = bots;
            founder.Session.KnownInputs = bots;
            for (int i = 0; i < 2; i++)
                bots.Add(host.AddBot($"Bot {i + 1}"));

            var endA = net.CreateEndpoint();
            net.Connect(hostEnd, endA, 25.0, 4.0, 0.03);
            Peer a = Make("A", endA, founder: false);
            var endB = net.CreateEndpoint();
            net.Connect(hostEnd, endB, 40.0, 6.0, 0.03);
            Peer b = Make("B", endB, founder: false);

            HostSession next = null;
            Takeover took = null;
            Peer impostor = null;
            int oldHostId = 0, bLocal = 0;
            var botsAt = new Dictionary<int, Vector3>();
            var peers = new List<Peer> { founder, a, b };

            while (net.Now < EndMs)
            {
                if (next == null && net.Now >= HostDiesMs)
                {
                    // The host is gone without a word; A takes over at once, as the lobby's next owner.
                    peers.Remove(founder);
                    oldHostId = founder.Session.LocalNetId;
                    bLocal = b.Session.LocalNetId;
                    took = a.Session.Takeover();
                    var nextEnd = net.CreateEndpoint();
                    next = new HostSession(nextEnd, net.Now) { Claims = (peer, id) => id != oldHostId };
                    next.Resume(took, net.Now);
                    a.Session = new PeerSession(a.Session, next.LocalTransport, founder: true, net.Now);
                    Check(a.Session.StartFrom(took), $"A takes over at tick {took.Tick} with {took.Roster.Count} in the game");
                    Hash(a);
                    next.AttachFounder(a.Session);
                    BotDirector taken = BotDirector.Create(a.World.Builder, a.World.Simulation, a.Session, Seed);
                    taken.Since = took.Tick;
                    next.Bots = taken;
                    a.Session.KnownInputs = taken;
                    foreach (NetEvent e in took.Roster)
                        if (e.Bot)
                        {
                            taken.Add(e.NetId);
                            botsAt[e.NetId] = a.Session.BodyOf(e.NetId).Motor.Position;
                        }
                    Check(botsAt.Count == 2, $"{botsAt.Count} bots taken over");
                    nextHostEnd = nextEnd;
                }
                if (next != null && b.Session.ResumeNetId == 0 && net.Now >= HostDiesMs + NoticeMs)
                {
                    // B notices the silence and says hello to the new host as who it was.
                    var end = net.CreateEndpoint();
                    net.Connect(nextHostEnd, end, 35.0, 6.0, 0.03);
                    b.Session = new PeerSession(b.Session, end, founder: false, net.Now);
                    Hash(b);
                }
                if (impostor == null && net.Now >= ImpostorMs)
                {
                    // Claims to be the old host, which it never was: a fresh player.
                    var end = net.CreateEndpoint();
                    net.Connect(nextHostEnd, end, 30.0, 4.0, 0.0);
                    impostor = Make("Impostor", end, founder: false, resumeAs: oldHostId);
                    peers.Add(impostor);
                }

                if (next == null)
                    host.Update(net.Now);
                else
                    next.Update(net.Now);
                foreach (Peer p in peers)
                    p.Session.Update(net.Now);
                (next == null ? bots : (BotDirector)next.Bots).Pump(1.0);
                net.Now += FrameMs;
            }

            int compared = 0, differ = 0;
            foreach ((int tick, ulong hash) in a.Hashes)
                if (tick > took.Tick && b.Hashes.TryGetValue(tick, out ulong other))
                {
                    compared++;
                    if (other != hash)
                        differ++;
                }
            Check(compared >= 20 && differ == 0, $"A and B agree on {compared - differ} of {compared} final states after the change");
            Check(b.Session.Joined && b.Session.LocalNetId == bLocal && b.Session.Repairs == 0,
                  $"B goes on as player {b.Session.LocalNetId} (was {bLocal}), {b.Session.Repairs} repairs");
            Check(a.Session.BodyOf(oldHostId) == null && b.Session.BodyOf(oldHostId) == null, "the old host's body left after the wait");
            Check(next.Awaited == 0, "nobody awaited any more");
            float moved = 0f;
            foreach ((int id, Vector3 at) in botsAt)
                moved = Math.Max(moved, Vector3.Distance(a.Session.BodyOf(id).Motor.Position, at));
            Check(moved > 2f, $"the bots go on under the new host ({moved:F1} m)");
            Check(impostor.Session.Joined && impostor.Session.LocalNetId != oldHostId && impostor.Session.LocalNetId != 0,
                  $"a false claim joins as a new player ({impostor.Session.LocalNetId})");
            Check(next.Desyncs == 0, $"{next.Desyncs} desyncs");

            bots.Dispose();
            foreach (Peer p in new[] { founder, a, b, impostor })
                p.World.Dispose();
            Log(failures == 0 ? "[migrate] PASSED" : $"[migrate] {failures} errors");
            return failures == 0 ? 0 : 1;
        }

        MemoryNetwork.Endpoint nextHostEnd;

        Peer Make(string name, INetTransport transport, bool founder, int resumeAs = 0)
        {
            var world = new HeadlessWorld();
            world.LoadArena(Maps.Test);
            NetWorld.SpawnDummies(world.Builder);
            world.StartMatch();
            world.Simulation.Deactivate();
            var p = new Peer { Name = name, World = world };
            p.Session = new PeerSession(new HeadlessNetWorld(world), transport, () => Walk(p), name, founder, net.Now, join: resumeAs == 0);
            if (resumeAs != 0)
            {
                p.Session.ResumeNetId = resumeAs;
                p.Session.Join(net.Now);
            }
            Hash(p);
            return p;
        }

        static void Hash(Peer p) => p.Session.Checksum += (tick, hash) => p.Hashes[tick] = hash;

        // A slow circle with a jump now and then: inputs a guess gets wrong.
        static MoveInput Walk(Peer p)
        {
            p.Turn++;
            return new MoveInput
            {
                Move = new Vector2(0f, 1f), Yaw = p.Turn * 1.5f % 360f,
                JumpPresses = (byte)(p.Turn % 90 == 0 ? 1 : 0), JumpHeld = p.Turn % 90 < 6,
            };
        }

        void Check(bool ok, string what)
        {
            if (!ok)
                failures++;
            Log($"[migrate] {(ok ? "ok    " : "ERROR ")} {what}");
        }
    }
}
