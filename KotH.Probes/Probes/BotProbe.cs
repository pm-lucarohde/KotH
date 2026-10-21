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
    /// <summary>
    /// The bots, headless: the navmesh reaches the hill over every ramp,
    /// three bots and a body standing on the hill play a round, the bots get
    /// up, score and hit it, and a second run with the same seed gives the
    /// same inputs bit for bit. Prints the hash for JIT against AOT. New, no
    /// Unity reference.
    /// </summary>
    public sealed class BotProbe : Probe
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
                Log($"[bots] je Bot und Minute über {Stats} Seeds: {sum.Jumps / per:F1} Sprünge, {sum.Doubles / per:F1} Doppelsprünge, " +
                    $"{sum.Slides / per:F1} Gleiten, {sum.Swings / per:F1} Schläge, {sum.Hits / per:F1} Treffer; " +
                    $"Punkte je Runde {sum.Points / (float)Stats:F1}, erster in der Zone im Mittel nach {sum.ZoneTicks / 40f / Stats:F1} s");
                return 0;
            }
            world.LoadArena(Maps.Test);
            world.StartMatch();
            Check(world.Builder.Bots != null, "Bot-Einstellungen im Arena-Prefab");
            var states = new LocalFinalStates(world.Simulation);
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            BotDirector director = BotDirector.Create(world.Builder, world.Simulation, states, Seed);
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Navmesh(director.Navigation, ms);

            Result first = Play(Seed);
            Result second = Play(Seed);
            Result other = Play(Seed + 1);
            Check(first.Inputs == second.Inputs && first.End == second.End,
                  $"gleicher Seed, gleiche Eingaben ({first.Inputs:X16} / {second.Inputs:X16})");
            Check(first.Inputs != other.Inputs, "anderer Seed, andere Eingaben");
            Log($"[bots] Hashkette Eingaben {first.Inputs:X16}, Endzustand {first.End:X16}");

            Lobby();

            // On a host: the same inputs however the frames fall, and a guest stays in step.
            ulong steady = Hosted(16.7, 1, guest: false, out _, out _);
            ulong ragged = Hosted(7.3, 2, guest: false, out _, out _);
            Check(steady == ragged, $"Host-Bots unabhängig vom Bildtakt ({steady:X16} / {ragged:X16})");
            Hosted(16.7, 3, guest: true, out PeerSession founder, out PeerSession guest);
            Hosted(16.7, 4, guest: false, out PeerSession paused, out _, pause: 3000.0);
            Check(paused.Stalls < 200 && Math.Abs(paused.Lead - PeerSession.LeadTarget) <= 1.0,
                  $"Host mit Bots nach 3 s Ladepause: {paused.Stalls}-mal gewartet, Vorlauf {paused.Lead:F1}");
            Log($"[bots] Host: {founder.Rollbacks} Rollbacks; Gast: {guest.Rollbacks} Rollbacks, tiefster {guest.DeepestRollback}, " +
                $"{guest.Repairs} Reparaturen");

            Log(failures == 0 ? "[bots] BESTANDEN" : $"[bots] {failures} Fehler");
            return failures == 0 ? 0 : 1;
        }

        void Navmesh(BotNavigation nav, double ms)
        {
            int drops = 0, jumps = 0, doubles = 0;
            foreach ((Vector3 _, Vector3 _, LinkKind kind) in nav.Links)
            {
                drops += kind == LinkKind.Drop ? 1 : 0;
                jumps += kind == LinkKind.Jump ? 1 : 0;
                doubles += kind == LinkKind.DoubleJump ? 1 : 0;
            }
            Log($"[bots] Navmesh {nav.PolyCount} Polygone in {ms:F0} ms, Links: {drops} Fallen, {jumps} Sprung, {doubles} Doppelsprung");
            Check(nav.PolyCount > 20 && drops > 10, "Navmesh mit Kanten zum Herunterfallen");

            var corners = new List<Corner>();
            foreach (Vector3 start in Starts)
            {
                bool found = nav.Plan(start, Hill, corners);
                Vector3 end = found ? corners[^1].Position : start;
                Log($"[bots] Weg von {start.X:F1} {start.Z:F1}: {corners.Count} Ecken, endet bei {end.X:F2} {end.Y:F2} {end.Z:F2}");
                Check(found && Vector3.Distance(end, Hill) < 0.6f, $"Weg von {start.X:F1} {start.Z:F1} aufs Plateau");
            }

            // Down again: the way off the hill drops or walks a ramp.
            bool back = nav.Plan(Hill, Starts[0], corners);
            Check(back && Vector3.Distance(corners[^1].Position, Starts[0]) < 0.6f, "Weg vom Plateau herunter");
        }

        struct Result
        {
            public ulong Inputs, End;
            public int Jumps, Doubles, Slides, Hits, Swings, Points, ZoneTicks;
        }

        /// <summary>Averages over many seeds, for tuning: "bots stats N".</summary>
        public int Stats = 0;

        /// <summary>
        /// A round with three bots and a still body on the hill. The bots must
        /// get up there, take points and hit the body; their inputs go into
        /// a hash chain, tick by tick.
        /// </summary>
        Result Play(int seed)
        {
            using var world = new HeadlessWorld();
            world.LoadArena(Maps.Test);
            HillMatch match = world.StartMatch();
            var states = new LocalFinalStates(world.Simulation);
            BotDirector director = BotDirector.Create(world.Builder, world.Simulation, states, seed);

            var still = new ScriptedBody { NetId = NetWorld.FirstPlayerId };
            Entity stillEntity = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, new Vector3(2f, 5.95f, -14f), Quaternion.Identity,
                                                 still, new DamagePercent());
            stillEntity.Name = "Ziel";

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
                    Log($"[bots] {bot.Entity.Name}: {fists.SwingCount} Schläge, {fists.HitCount} Treffer, " +
                        $"{HillMatch.Points(bot.ScoreTicks)} Punkte, {bot.Entity.Get<DamagePercent>().Percent:F1} %, " +
                        $"zuletzt {brain.Intent} bei {bot.Motor.Position.X:F1} {bot.Motor.Position.Y:F1} {bot.Motor.Position.Z:F1}");
                }
                Log($"[bots] Ziel: {stillEntity.Get<DamagePercent>().Percent:F1} %, bei {still.Motor.Position.X:F1} {still.Motor.Position.Y:F1} {still.Motor.Position.Z:F1}; " +
                    $"erster Bot in der Zone nach {firstInZone / 40.0:F1} s");
                var shares = new List<string>();
                for (int i = 0; i < intents.Length; i++)
                    shares.Add($"{(BotIntent)i} {intents[i] * 100 / (PlayTicks * 3)} %");
                Log("[bots] Absichten: " + string.Join(", ", shares));
                shares.Clear();
                for (int i = 0; i < maneuvers.Length; i++)
                    shares.Add($"{(Maneuver)i} {maneuvers[i] * 100 / (PlayTicks * 3)} %");
                Log("[bots] Manöver: " + string.Join(", ", shares));
                Log($"[bots] im Motor ausgeführt: {jumps} Sprünge, {doubles} Doppelsprünge, {slides} Gleiten, " +
                    $"Sprint {sprintTicks * 100 / (PlayTicks * 3)} % der Zeit");
                Check(doubles > 0 && slides > 0, "Bots nutzen Doppelsprung und Gleiten");
                int lost = 0;
                foreach (BotBody bot in bots)
                    lost += bot.Motor.Position.Y < -1f ? 1 : 0;
                Log($"[bots] {respawns}-mal aus der Arena gefallen und am Startplatz wieder da");
                Check(lost == 0, $"kein Bot fällt endlos ({lost} unter der Arena)");
                Check(firstInZone >= 0 && firstInZone < 40 * 20, "ein Bot in höchstens 20 s in der Zone");
                Log($"[bots] Punkte zusammen {HillMatch.Points(score)}; allein zählt nur, wer die anderen hinausstößt");
                Check(zoneTicks > PlayTicks, $"Bots stehen über ein Drittel der Zeit in der Zone ({zoneTicks * 100 / (PlayTicks * 3)} %)");
                Check(hits > 0, $"Bots treffen ({hits} von {swings})");
                Check(stillEntity.Get<DamagePercent>().Percent > 0f, "der stehende Körper wird getroffen");
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
            Log($"[bots] Lobby 30 s: Phase {match.State.Phase}, {swings} Schläge, Ziel {stillEntity.Get<DamagePercent>().Percent:F1} %");
            Check(match.State.Phase == MatchPhase.Waiting && swings == 0, "in der Lobby schlägt kein Bot");
        }

        /// <summary>
        /// A host with bots over the memory network, 40 s. Returns the hash
        /// chain of the bot inputs the host confirmed. With a guest, checks
        /// the checksums agree and the host never had to roll back: its own
        /// player and the guest stand still, and the bots it knows.
        /// </summary>
        ulong Hosted(double frameMs, int netSeed, bool guest, out PeerSession founder, out PeerSession visitor, double pause = 0.0)
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
            Log($"[bots] Host mit {ids.Count} Bots, Takt {frameMs} ms, Ladepause {pause} ms: {hashed} Ticks bestätigt, " +
                $"{founder.Stalls}-mal gewartet, Vorlauf {founder.Lead:F1}, {inZone} Bots in der Zone, " +
                $"Phase {home.Simulation.Match.State.Phase}");
            Check(inZone > 0, "Host-Bots kommen in die Zone");

            if (guest)
            {
                int compared = 0, differ = 0;
                foreach (KeyValuePair<int, ulong> h in hashes)
                    if (mine.TryGetValue(h.Key, out ulong m))
                    {
                        compared++;
                        differ += m == h.Value ? 0 : 1;
                    }
                Check(compared > 20 && differ == 0 && host.Desyncs == 0, $"Gast und Host gleich ({compared} Prüfsummen, {differ} verschieden)");
                Check(founder.Rollbacks == 0, $"Host kennt seine Bots, kein Rollback ({founder.Rollbacks})");
            }
            foreach (HeadlessWorld w in worlds)
                w.Dispose();
            return chain;
        }

        void Check(bool ok, string what)
        {
            Log((ok ? "[bots] ok: " : "[bots] FEHLER: ") + what);
            if (!ok)
                failures++;
        }
    }
}
