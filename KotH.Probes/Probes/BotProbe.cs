using System;
using System.Buffers;
using System.Collections.Generic;
using KotH.Probes.Harness;
using KotH.Shared.Bots;
using KotH.Shared.Combat;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Net;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Probes
{
    /// <summary>The bots, headless: the climbing grid reaches the hill over every ramp,
    /// three bots and a body standing on the hill play a round, the bots get up, score
    /// and hit it, and a second run with the same seed gives the same inputs bit for bit.
    /// Prints the hash for JIT against AOT. New, no Unity reference.</summary>
    public sealed partial class BotProbe : Probe
    {
        protected override string Tag => "bots";

        const int Seed = 7;
        const int PlayTicks = 40 * 90;

        // Stride metres: the hill's top at y 5, the ramp feet in front and to the side.
        static readonly Vector3 Hill = new Vector3(0f, 5f, -16f);
        static readonly Vector3[] Starts =
        {
            new Vector3(0f, 0f, 0f), new Vector3(-3.5f, 0f, 6f), new Vector3(3.5f, 0f, 1f),
            new Vector3(14f, 0f, -16f), new Vector3(-16f, 0f, 12f), new Vector3(0f, 0f, -24f),
        };

        int failures;
        bool reported;

        public override int Run(HeadlessWorld world, int maxTicks)
        {
            if (Climb > 0)
                return Climbs(Climb);
            if (Lab > 0)
                return Labs(Lab);
            if (Stats > 0)
            {
                var sum = new Result();
                for (int k = 1; k <= Stats; k++)
                {
                    Result r = Play(1000 + k);
                    sum.Jumps += r.Jumps; sum.Doubles += r.Doubles; sum.Slides += r.Slides;
                    sum.Hits += r.Hits; sum.Swings += r.Swings; sum.Points += r.Points; sum.ZoneTicks += r.ZoneTicks;
                }
                float per = Stats * 3 * PlayTicks / 2400f;
                Log($"[bots] per bot and minute over {Stats} seeds: {sum.Jumps / per:F1} jumps, {sum.Doubles / per:F1} double jumps, " +
                    $"{sum.Slides / per:F1} slides, {sum.Swings / per:F1} swings, {sum.Hits / per:F1} hits; " +
                    $"points per round {sum.Points / (float)Stats:F1}, first in the zone on average after {sum.ZoneTicks / 40f / Stats:F1} s");
                return 0;
            }
            world.LoadArena(Maps.Test);
            world.StartMatch();
            Check(world.Builder.Bots != null, "bot settings in the arena prefab");
            var states = new LocalFinalStates(world.Simulation);
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            BotDirector director = BotDirector.Create(world.Builder, world.Simulation, states, Seed);
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Navmesh(director.Navigation, ms);

            Result first = Play(Seed);
            Result second = Play(Seed);
            Result other = Play(Seed + 1);
            Check(first.Inputs == second.Inputs && first.End == second.End,
                  $"same seed, same inputs ({first.Inputs:X16} / {second.Inputs:X16})");
            Check(first.Inputs != other.Inputs, "other seed, other inputs");
            // Worked out ahead, on a thread or a little each frame: the same bits.
            Result threaded = Play(Seed, ahead: 1), pumped = Play(Seed, ahead: 2);
            Check(threaded.Inputs == first.Inputs && threaded.End == first.End &&
                  pumped.Inputs == first.Inputs && pumped.End == first.End,
                  $"computed ahead the same ({threaded.Inputs:X16} thread, {pumped.Inputs:X16} pump)");
            Log($"[bots] hash chain inputs {first.Inputs:X16}, end state {first.End:X16}");

            Lobby();

            // On a host: the same inputs however the frames fall, and a guest stays in step.
            ulong steady = Hosted(16.7, 1, guest: false, out _, out _);
            ulong ragged = Hosted(7.3, 2, guest: false, out _, out _);
            Check(steady == ragged, $"host bots independent of the frame rate ({steady:X16} / {ragged:X16})");
            ulong hostThread = Hosted(16.7, 1, guest: false, out _, out _, ahead: 1);
            ulong hostPump = Hosted(7.3, 2, guest: false, out _, out _, ahead: 2);
            Check(hostThread == steady && hostPump == steady,
                  $"host bots computed ahead the same ({hostThread:X16} thread, {hostPump:X16} pump)");
            Hosted(16.7, 3, guest: true, out PeerSession founder, out PeerSession guest);
            Hosted(16.7, 4, guest: false, out PeerSession paused, out _, pause: 3000.0);
            Check(paused.Stalls < 200 && Math.Abs(paused.Lead - PeerSession.LeadTarget) <= 1.0,
                  $"host with bots after 3 s load pause: {paused.Stalls} stalls, lead {paused.Lead:F1}");
            Log($"[bots] Host: {founder.Rollbacks} rollbacks; guest: {guest.Rollbacks} rollbacks, deepest {guest.DeepestRollback}, " +
                $"{guest.Repairs} repairs");

            Log(failures == 0 ? "[bots] PASSED" : $"[bots] {failures} errors");
            return failures == 0 ? 0 : 1;
        }

        void Navmesh(BotNavigation nav, double ms)
        {
            ClimbGrid g = nav.Grid;
            Log($"[bots] climb grid {g.FloorCount} floors in {ms:F0} ms, moves: {g.Drops} drop, {g.Jumps} jump, " +
                $"{g.DoubleJumps} double jump, {g.Gaps} gap, {g.Variants} route variants");
            Check(g.FloorCount > 20 && g.Drops > 10, "grid with edges to drop down");

            var corners = new List<Corner>();
            foreach (Vector3 start in Starts)
            {
                bool found = nav.Plan(start, Hill, corners);
                Vector3 end = found ? corners[^1].Position : start;
                Log($"[bots] path from {start.X:F1} {start.Z:F1}: {corners.Count} corners, ends at {end.X:F2} {end.Y:F2} {end.Z:F2}");
                Check(found && Vector3.Distance(end, Hill) < 0.6f, $"path from {start.X:F1} {start.Z:F1} onto the plateau");
            }

            // Down again: the way off the hill drops or walks a ramp.
            bool back = nav.Plan(Hill, Starts[0], corners);
            Check(back && Vector3.Distance(corners[^1].Position, Starts[0]) < 0.6f, "path down from the plateau");
        }

        struct Result
        {
            public ulong Inputs, End;
            public int Jumps, Doubles, Slides, Hits, Swings, Points, ZoneTicks;
        }

        /// <summary>Averages over many seeds, for tuning: "bots stats N".</summary>
        public int Stats = 0;

        /// <summary>A round with three bots and a still body on the hill. The bots must
        /// get up there, take points and hit the body; their inputs go into a hash chain,
        /// tick by tick.</summary>
        // ahead: 0 as the server does, 1 on the bots' own thread, 2 a little each tick (Pump).
        Result Play(int seed, int ahead = 0)
        {
            using var world = new HeadlessWorld();
            world.LoadArena(Maps.Test);
            HillMatch match = world.StartMatch();
            var states = new LocalFinalStates(world.Simulation);
            BotDirector director = BotDirector.Create(world.Builder, world.Simulation, states, seed);
            if (ahead == 1)
                director.StartWorker();

            var still = new ScriptedBody { NetId = NetWorld.FirstPlayerId };
            Entity stillEntity = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, new Vector3(2f, 5.95f, -14f), Quaternion.Identity,
                                                 still, new DamagePercent());
            stillEntity.Name = "Target";

            var bots = new List<BotBody>();
            for (int i = 0; i < 3; i++)
            {
                Entity marker = NetWorld.SlotMarker(world.Builder, NetWorld.BotSlots[i]);
                var bot = new BotBody(director, states, NetWorld.FirstPlayerId + 1 + i);
                Entity e = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, marker.Transform.Position, marker.Transform.Rotation,
                                           bot, new DamagePercent());
                e.Name = $"Bot {i + 1}";
                bots.Add(bot);
            }

            bool trace = Environment.GetEnvironmentVariable("KOTH_BOTTRACE") != null;
            int traceUntil = int.Parse(Environment.GetEnvironmentVariable("KOTH_BOTTRACE") ?? "0");
            var intents = new int[5];
            var was = new MotorState[3];
            int slides = 0, jumps = 0, doubles = 0, sprintTicks = 0;
            var maneuvers = new int[ManeuverWfc.Count];
            ulong chain = 14695981039346656037UL;
            int firstInZone = -1, zoneTicks = 0, respawns = 0;
            var buffer = new ArrayBufferWriter<byte>();
            for (int t = 0; t < PlayTicks; t++)
            {
                world.Simulation.Tick();
                if (ahead > 0)
                    director.Pump(0.05);
                foreach (BotBody bot in bots)
                {
                    director.TryInput(bot.NetId, world.Simulation.CurrentTick - 1, out MoveInput input);
                    buffer.Clear();
                    var w = new Writer(buffer);
                    NetProtocol.WriteInput(ref w, input);
                    foreach (byte b in buffer.WrittenSpan)
                        chain = (chain ^ b) * 1099511628211UL;
                    MotorState now = bot.Motor.CaptureState(), before = was[bots.IndexOf(bot)];
                    respawns += before.Position.Y < -1f && now.Position.Y > 0.5f ? 1 : 0;
                    slides += now.Sliding && !before.Sliding ? 1 : 0;
                    jumps += now.JumpCutPending && !before.JumpCutPending ? 1 : 0;
                    doubles += now.DoubleJumpUsed && !before.DoubleJumpUsed ? 1 : 0;
                    sprintTicks += now.Sprinting ? 1 : 0;
                    was[bots.IndexOf(bot)] = now;
                    BotBrain mind = director.BrainOf(bot.NetId);
                    intents[(int)mind.Intent]++;
                    maneuvers[(int)mind.Running]++;
                    if (match.Contains(bot.Motor.Position))
                    {
                        zoneTicks++;
                        if (firstInZone < 0)
                            firstInZone = t;
                    }
                    if (trace && seed == Seed && (t % 5 == 0 || bot.Motor.Position.Y < -0.5f && t % 1 == 0) && t < traceUntil)
                    {
                        BotBrain br = director.BrainOf(bot.NetId);
                        MotorState ms = bot.Motor.CaptureState();
                        Log($"[spur] {t} {bot.Entity.Name} {br.Intent} {br.Running} pos {ms.Position.X:F2} {ms.Position.Y:F2} {ms.Position.Z:F2} v {ms.Velocity.X:F1} {ms.Velocity.Y:F1} {ms.Velocity.Z:F1} g {(ms.Grounded ? 1 : 0)} kb {ms.KnockbackTime:F2} in {input.Move.X:F2} {input.Move.Y:F2} j {input.JumpPresses} a {input.AttackPresses} c {(input.Crouch ? 1 : 0)}");
                    }
                }
            }

            director.Dispose();
            if (ahead > 0)
            {
                int share = director.StepsAhead * 100 / Math.Max(1, director.StepsAhead + director.StepsAsked);
                Check(share >= 90, $"{(ahead == 1 ? "Thread" : "Pump")} computes {share} % of the steps ahead");
            }
            var snap = new WorldSnapshot();
            world.Simulation.Capture(snap);
            buffer.Clear();
            StateCodec.Write(snap, buffer);
            ulong end = StateCodec.Hash(buffer.WrittenSpan);

            if (seed == Seed && !reported)
            {
                reported = true;
                int hits = 0, swings = 0, score = 0;
                foreach (BotBody bot in bots)
                {
                    MeleeAttack fists = bot.Entity.Get<MeleeAttack>();
                    hits += fists.HitCount;
                    swings += fists.SwingCount;
                    score += bot.ScoreTicks;
                    BotBrain brain = director.BrainOf(bot.NetId);
                    Log($"[bots] {bot.Entity.Name}: {fists.SwingCount} swings, {fists.HitCount} hits, " +
                        $"{HillMatch.Points(bot.ScoreTicks)} points, {bot.Entity.Get<DamagePercent>().Percent:F1} %, " +
                        $"last {brain.Intent} at {bot.Motor.Position.X:F1} {bot.Motor.Position.Y:F1} {bot.Motor.Position.Z:F1}");
                }
                Log($"[bots] target: {stillEntity.Get<DamagePercent>().Percent:F1} %, at {still.Motor.Position.X:F1} {still.Motor.Position.Y:F1} {still.Motor.Position.Z:F1}; " +
                    $"first bot in the zone after {firstInZone / 40.0:F1} s");
                var shares = new List<string>();
                for (int i = 0; i < intents.Length; i++)
                    shares.Add($"{(BotIntent)i} {intents[i] * 100 / (PlayTicks * 3)} %");
                Log("[bots] intents: " + string.Join(", ", shares));
                shares.Clear();
                for (int i = 0; i < maneuvers.Length; i++)
                    shares.Add($"{(Maneuver)i} {maneuvers[i] * 100 / (PlayTicks * 3)} %");
                Log("[bots] manoeuvres: " + string.Join(", ", shares));
                Log($"[bots] executed in the motor: {jumps} jumps, {doubles} double jumps, {slides} slides, " +
                    $"sprint {sprintTicks * 100 / (PlayTicks * 3)} % of the time");
                Check(doubles > 0 && slides > 0, "bots use double jump and slide");
                int lost = 0;
                foreach (BotBody bot in bots)
                    lost += bot.Motor.Position.Y < -1f ? 1 : 0;
                Log($"[bots] {respawns} times fell out of the arena and back at the spawn");
                Check(lost == 0, $"no bot falls forever ({lost} below the arena)");
                Check(firstInZone >= 0 && firstInZone < 40 * 20, "a bot in the zone within 20 s");
                Log($"[bots] points together {HillMatch.Points(score)}; alone counts only who pushes the others out");
                Check(zoneTicks > PlayTicks, $"bots stand in the zone over a third of the time ({zoneTicks * 100 / (PlayTicks * 3)} %)");
                Check(hits > 0, $"bots hit ({hits} of {swings})");
                Check(stillEntity.Get<DamagePercent>().Percent > 0f, "the standing body is hit");
            }
            int allHits = 0, allSwings = 0, allScore = 0;
            foreach (BotBody bot in bots)
            {
                allHits += bot.Entity.Get<MeleeAttack>().HitCount;
                allSwings += bot.Entity.Get<MeleeAttack>().SwingCount;
                allScore += HillMatch.Points(bot.ScoreTicks);
            }
            return new Result
            {
                Inputs = chain, End = end, Jumps = jumps, Doubles = doubles, Slides = slides,
                Hits = allHits, Swings = allSwings, Points = allScore, ZoneTicks = firstInZone,
            };
        }

        /// <summary>
        /// A lobby waiting for more players, 30 s: the bots stand next to a
        /// still body at the spawn and must never swing at it.
        /// </summary>
        void Lobby()
        {
            using var world = new HeadlessWorld();
            world.LoadArena(Maps.Test);
            HillMatch match = world.StartMatch();
            match.MinPlayers = 8;
            var states = new LocalFinalStates(world.Simulation);
            BotDirector director = BotDirector.Create(world.Builder, world.Simulation, states, Seed);

            Entity marker = NetWorld.SlotMarker(world.Builder, 1);
            Entity stillEntity = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, marker.Transform.Position, marker.Transform.Rotation,
                                                 new ScriptedBody { NetId = NetWorld.FirstPlayerId }, new DamagePercent());
            var fists = new List<MeleeAttack>();
            for (int i = 0; i < 3; i++)
            {
                marker = NetWorld.SlotMarker(world.Builder, NetWorld.BotSlots[i]);
                Entity e = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, marker.Transform.Position, marker.Transform.Rotation,
                                           new BotBody(director, states, NetWorld.FirstPlayerId + 1 + i), new DamagePercent());
                fists.Add(e.Get<MeleeAttack>());
            }

            int swings = 0;
            for (int t = 0; t < 1200; t++)
                world.Simulation.Tick();
            foreach (MeleeAttack f in fists)
                swings += f.SwingCount;
            Log($"[bots] lobby 30 s: phase {match.State.Phase}, {swings} swings, target {stillEntity.Get<DamagePercent>().Percent:F1} %");
            Check(match.State.Phase == MatchPhase.Waiting && swings == 0, "no bot swings in the lobby");
        }

        /// <summary>A host with bots over the memory network, 40 s. Returns the hash
        /// chain of the bot inputs the host confirmed. With a guest, checks the checksums
        /// agree and the host never had to roll back: its own player and the guest stand
        /// still, and the bots it knows. ahead as in Play.</summary>
        ulong Hosted(double frameMs, int netSeed, bool guest, out PeerSession founder, out PeerSession visitor, double pause = 0.0,
                     int ahead = 0)
        {
            var net = new MemoryNetwork(netSeed);
            MemoryNetwork.Endpoint hostEnd = net.CreateEndpoint();
            var host = new HostSession(hostEnd, net.Now);
            var worlds = new List<HeadlessWorld>();

            HeadlessWorld World()
            {
                var w = new HeadlessWorld();
                w.LoadArena(Maps.Test);
                w.StartMatch().CountdownTicks = 40;
                w.Simulation.Deactivate();
                worlds.Add(w);
                return w;
            }

            HeadlessWorld home = World();
            founder = new PeerSession(new HeadlessNetWorld(home), host.LocalTransport, () => default, "Host", true, net.Now);
            host.AttachFounder(founder);
            home.Simulation.Match.MinPlayers = HillMatch.DefaultMinPlayers;
            BotDirector director = BotDirector.Create(home.Builder, home.Simulation, founder, Seed);
            host.Bots = director;
            founder.KnownInputs = director;
            if (ahead == 1)
                director.StartWorker();
            var ids = new List<int>();
            for (int i = 0; i < (guest ? 2 : 3); i++)
            {
                ids.Add(host.AddBot($"Bot {i + 1}"));
                director.Add(ids[^1]);
            }

            visitor = null;
            var hashes = new Dictionary<int, ulong>();
            if (guest)
            {
                MemoryNetwork.Endpoint end = net.CreateEndpoint();
                net.Connect(hostEnd, end, 40.0, 5.0, 0.0);
                visitor = new PeerSession(new HeadlessNetWorld(World()), end, () => default, "Gast", false, net.Now);
                visitor.Checksum += (tick, hash) => hashes[tick] = hash;
            }
            var mine = new Dictionary<int, ulong>();
            founder.Checksum += (tick, hash) => mine[tick] = hash;

            ulong chain = 14695981039346656037UL;
            int hashed = 0;
            var buffer = new ArrayBufferWriter<byte>();
            net.Now += pause;
            while (net.Now < 40000.0)
            {
                host.Update(net.Now);
                founder.Update(net.Now);
                visitor?.Update(net.Now);
                if (ahead > 0)
                    director.Pump(0.05);
                for (; hashed < host.NextConfirm; hashed++)
                    foreach (int id in ids)
                    {
                        director.TryInput(id, hashed, out MoveInput input);
                        buffer.Clear();
                        var w = new Writer(buffer);
                        NetProtocol.WriteInput(ref w, input);
                        foreach (byte b in buffer.WrittenSpan)
                            chain = (chain ^ b) * 1099511628211UL;
                    }
                net.Now += frameMs;
            }

            int inZone = 0;
            foreach (int id in ids)
                if (home.Simulation.Match.Contains(founder.BodyOf(id).Motor.Position))
                    inZone++;
            Log($"[bots] host with {ids.Count} bots, frame {frameMs} ms, load pause {pause} ms: {hashed} ticks confirmed, " +
                $"{founder.Stalls} stalls, lead {founder.Lead:F1}, {inZone} bots in the zone, " +
                $"phase {home.Simulation.Match.State.Phase}");
            Check(inZone > 0, "host bots reach the zone");

            if (guest)
            {
                int compared = 0, differ = 0;
                foreach (KeyValuePair<int, ulong> h in hashes)
                    if (mine.TryGetValue(h.Key, out ulong m))
                    {
                        compared++;
                        differ += m == h.Value ? 0 : 1;
                    }
                Check(compared > 20 && differ == 0 && host.Desyncs == 0, $"guest and host the same ({compared} checksums, {differ} different)");
                Check(founder.Rollbacks == 0, $"host knows its bots, no rollback ({founder.Rollbacks})");
            }
            director.Dispose();
            if (ahead > 0)
            {
                int share = director.StepsAhead * 100 / Math.Max(1, director.StepsAhead + director.StepsAsked);
                Check(share >= 90, $"Host: {(ahead == 1 ? "Thread" : "Pump")} computes {share} % of the steps ahead");
            }
            foreach (HeadlessWorld w in worlds)
                w.Dispose();
            return chain;
        }

        void Check(bool ok, string what)
        {
            Log((ok ? "[bots] ok: " : "[bots] ERROR: ") + what);
            if (!ok)
                failures++;
        }
    }
}
