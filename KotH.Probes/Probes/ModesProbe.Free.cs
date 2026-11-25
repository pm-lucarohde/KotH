using System;
using System.Collections.Generic;
using KotH.Probes.Harness;
using KotH.Shared.Combat;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Mathematics;
using KotH.Shared.Net;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    /// <summary>1, 2, 3, Free on the Test arena: the draw, the wheel and the head start (standing, 15 m off, no
    /// attacks), catching by cane, spore and dash (a shot back), the zone count, marking with two left, the
    /// last one's rescue, who won, the next draw's weights, a catcher leaving, two catchers from 11, and a
    /// rollback through the head start and a catch bit-identical.</summary>
    public sealed partial class ModesProbe
    {
        HillMatch match;
        ScriptedBody[] all;
        ScriptedBody catcher;
        readonly List<ScriptedBody> runners = new List<ScriptedBody>();

        // The plateau's top (y 5, x -6..6, z -22..-10) holds the zone; open ground lies at y 0 towards +z.
        static readonly Vector3 InZone = new Vector3(0f, 5.95f, -16f);

        int RunFree(HeadlessWorld host)
        {
            world = host;
            Begin(6, 7u);
            int first = catcher.NetId;
            Check(match.State.Catchers == 1 && runners.Count == 5 && catcher.Entity.Get<DamagePercent>().Percent == HillMatch.StartPercent,
                  $"draw: one catcher ({catcher.Entity.Name}) at {catcher.Entity.Get<DamagePercent>().Percent} %, five runners");
            Check(runners.TrueForAll(r => r.Entity.Get<DamagePercent>().Percent == HillMatch.StartPercent),
                  $"the runners at {HillMatch.StartPercent} %");

            Wheel();
            HeadStart();
            CatchAll();

            // The same seed draws the same catcher.
            world = new HeadlessWorld();
            Begin(6, 7u);
            Check(catcher.NetId == first, $"the same seed draws the same catcher ({first}, again {catcher.NetId})");

            Weights();
            Leaving();
            TwoCatchers();
            Rollback();

            Log(failures == 0 ? "[modes] PASSED" : $"[modes] {failures} errors");
            return failures == 0 ? 0 : 1;
        }

        // A fresh round of n on open ground, ticked once: everyone a runner, then the draw.
        void Begin(int n, uint seed)
        {
            world.LoadArena(Maps.Test);
            match = world.StartMatch();
            match.Mode = MatchMode.Free;
            match.Seed = seed;
            all = new ScriptedBody[n];
            for (int i = 0; i < n; i++)
                all[i] = Spawn("R" + i, Ground(i), NetWorld.FirstPlayerId + i);
            Step(1);
            runners.Clear();
            catcher = null;
            foreach (ScriptedBody b in all)
            {
                if (b.Role == FreeRole.Catcher)
                    catcher ??= b;
                else if (b.Role == FreeRole.Runner)
                    runners.Add(b);
                b.Motor.Teleport(Ground(Array.IndexOf(all, b)));
            }
            catcher?.Motor.Teleport(new Vector3(22f, 0.9f, -5f));
        }

        static Vector3 Ground(int i) => new Vector3(-12f + 5f * i, 0.9f, 10f);

        // The wheel: nobody moves, the catcher neither.
        void Wheel()
        {
            Vector3 runner = runners[0].Motor.Position, own = catcher.Motor.Position;
            runners[0].Input.Move = catcher.Input.Move = new Vector2(0f, 1f);
            Step(HillMatch.DrawTicks - 1 - match.State.RoundTicks);
            Check(match.Drawing && Flat(runners[0].Motor.Position, runner) < 1e-3f && Flat(catcher.Motor.Position, own) < 1e-3f,
                  $"wheel: nobody moves (runner {Flat(runners[0].Motor.Position, runner):F4} m, catcher {Flat(catcher.Motor.Position, own):F4} m)");
            Stop();
            Step(1);
        }

        // The head start: runners stand and cannot be caught, the catcher runs but keeps 15 m off and does not swing.
        void HeadStart()
        {
            Check(match.HeadStart, "head start after the wheel");
            Vector3 runner = runners[0].Motor.Position;
            runners[0].Input.Move = new Vector2(0f, 1f);
            catcher.Input.Move = new Vector2(0f, 1f);
            Step(20);
            Check(Flat(runners[0].Motor.Position, runner) < 1e-3f, "head start: a runner stands");
            Check(catcher.Motor.Velocity.Length() > 1f, "head start: the catcher runs");
            Stop();
            catcher.Motor.Teleport(UMath.Add(runners[1].Motor.Position, new Vector3(0f, 0f, 5f)));
            Step(10);
            float apart = float.MaxValue;
            foreach (ScriptedBody r in runners)
                apart = MathF.Min(apart, Flat(catcher.Motor.Position, r.Motor.Position));
            Check(apart >= HillMatch.KeepAway - 0.1f, $"head start: the catcher kept off every runner ({apart:F2} m)");
            catcher.Motor.Teleport(UMath.Add(runners[1].Motor.Position, new Vector3(0f, 0f, 1.5f)));
            Face(catcher, runners[1].Motor.BodyCenter);
            catcher.Input.AttackPresses = 1;
            Step(1);
            catcher.Input.AttackPresses = 0;
            Check(runners[1].Role == FreeRole.Runner && catcher.Entity.Get<MeleeAttack>().SwingCount == 0,
                  "head start: no swing, nobody caught");
            catcher.Motor.Teleport(new Vector3(22f, 0.9f, -5f));
            Step(match.HeadStartLeft);
            Check(match.Playing, "play after the head start");
        }

        // Cane, spore and dash catch; two left, a hit marks and the catcher's 3 s in the zone catch;
        // the last one gets free and, staying, rescues everyone; the catcher has lost.
        void CatchAll()
        {
            ScriptedBody r1 = runners[0], r2 = runners[1], r3 = runners[2], r4 = runners[3], r5 = runners[4];
            Blaster gun = catcher.Entity.Get<Blaster>();

            // A spore into the sky first, so the hit has a shot to bring back.
            catcher.Input.Blaster = true;
            Step(12);
            catcher.Input.Pitch = Up(catcher, 60f);
            catcher.Input.AttackPresses = 1;
            Step(1);
            catcher.Input.AttackPresses = 0;
            int left = gun.State.Shots;
            catcher.Input.Blaster = false;
            Step(12);

            // The cane.
            Strike(r1);
            Check(r1.Role == FreeRole.Caught && r1.CaughtOrder == 1 && r1.MarkedBy == catcher.NetId && gun.State.Shots == left + 1,
                  $"cane catches R ({r1.Role}, order {r1.CaughtOrder}), a shot back ({left} -> {gun.State.Shots})");
            Vector3 still = r1.Motor.Position;
            r1.Input.Move = new Vector2(0f, 1f);
            Step(20);
            r1.Input.Move = Vector2.Zero;
            Check(Flat(r1.Motor.Position, still) < 1e-3f, "caught: out, stands");

            // A spore from 10 m.
            catcher.Motor.Teleport(UMath.Add(r2.Motor.Position, new Vector3(0f, 0f, 10f)));
            catcher.Input.Blaster = true;
            Step(12);
            Face(catcher, r2.Motor.BodyCenter);
            catcher.Input.AttackPresses = 1;
            Step(1);
            catcher.Input.AttackPresses = 0;
            Step(4);
            catcher.Input.Blaster = false;
            Check(r2.Role == FreeRole.Caught && r2.CaughtOrder == 2, $"a spore catches ({r2.Role})");
            Step(12);

            // The dash beside the head.
            catcher.Motor.Teleport(UMath.Add(r3.Motor.Position, new Vector3(1.2f, 4.5f, 0f)));
            Step(2);
            catcher.Input.CrouchPressed = true;
            Step(1);
            catcher.Input.CrouchPressed = false;
            Step(30);
            Check(r3.Role == FreeRole.Caught && r3.CaughtOrder == 3, $"the dash catches ({r3.Role})");

            // Two left: the cane only marks; 3 s in the zone catch.
            Strike(r4);
            Check(r4.Role == FreeRole.Runner && r4.MarkedBy == catcher.NetId, $"two left: the cane marks ({r4.Role}, by {r4.MarkedBy})");
            catcher.Motor.Teleport(InZone);
            Step(HillMatch.MarkTicks + 1);
            Check(r4.Role == FreeRole.Caught && r4.CaughtOrder == 4, $"3 s in the zone: the marked one is caught ({r4.Role})");
            catcher.Motor.Teleport(new Vector3(22f, 0.9f, -5f));
            Step(1);

            // The last runner: the catcher stepping in starts the count over, 3 s free, 6 s everyone.
            r5.Motor.Teleport(UMath.Add(InZone, new Vector3(2f, 0f, 0f)));
            Step(60);
            catcher.Motor.Teleport(InZone);
            Step(2);
            Check(r5.RoleTicks == 0, $"the catcher in the zone starts the count over ({r5.RoleTicks})");
            catcher.Motor.Teleport(new Vector3(22f, 0.9f, -5f));
            Step(HillMatch.FreeTicks + 1);
            Check(r5.Role == FreeRole.Rescuing && r1.Role == FreeRole.Caught, $"the last one free after 3 s, counting on ({r5.Role})");
            Step(HillMatch.RescueTicks - HillMatch.FreeTicks);
            IReadOnlyList<SimulationBody> bodies = world.Simulation.Bodies;
            Check(match.State.Phase == MatchPhase.Over && r1.Role == FreeRole.Free && r4.Role == FreeRole.Free && r5.Role == FreeRole.Free,
                  $"6 s: everyone free, the round over ({match.State.Phase})");
            Check(HillMatch.Won(r1, bodies) && HillMatch.Won(r5, bodies) && !HillMatch.Won(catcher, bodies),
                  "the free ones win, the catcher with none left caught loses");
            Check(catcher.Odds == HillMatch.Certain && r1.Odds == 0, $"next draw: the catcher certain ({catcher.Odds})");
        }

        // Everyone caught: the first one caught has half the next draw, the others share the rest; a new round draws by it.
        void Weights()
        {
            world = new HeadlessWorld();
            Begin(5, 11u);
            Step(match.HeadStartLeft);
            ScriptedBody r1 = runners[0], r2 = runners[1], r3 = runners[2], r4 = runners[3];
            Strike(r1);
            Strike(r2);
            r3.Motor.Teleport(InZone);
            Step(HillMatch.FreeTicks + 1);
            r3.Motor.Teleport(new Vector3(-22f, 0.9f, -5f));
            Strike(r4);
            catcher.Motor.Teleport(InZone);
            Step(HillMatch.MarkTicks + 1);
            IReadOnlyList<SimulationBody> bodies = world.Simulation.Bodies;
            Check(match.State.Phase == MatchPhase.Over && r3.Role == FreeRole.Free && r4.Role == FreeRole.Caught,
                  $"all caught or free: over ({match.State.Phase}, R {r3.Role}, R {r4.Role})");
            Check(HillMatch.Won(catcher, bodies) && HillMatch.Won(r3, bodies) && !HillMatch.Won(r1, bodies), "the catcher and the free one win");
            float s1 = HillMatch.Share(r1, bodies), s2 = HillMatch.Share(r2, bodies), s4 = HillMatch.Share(r4, bodies), s3 = HillMatch.Share(r3, bodies);
            Check(MathF.Abs(s1 - 0.5f) < 1e-4f && MathF.Abs(s2 - 0.25f) < 1e-4f && MathF.Abs(s4 - 0.25f) < 1e-4f && s3 == 0f,
                  $"next draw: the first caught 50 %, the others {s2:P0} and {s4:P0}, the free one 0");

            // Over 200 rounds' draws the first caught takes about half.
            int firstDrawn = 0;
            for (int round = 0; round < 200; round++)
            {
                match.Seed = (uint)(1000 + round);
                match.Restore(new MatchState { Seed = match.Seed, Mode = MatchMode.Free });
                foreach (ScriptedBody b in all)
                {
                    b.Role = FreeRole.None;
                    b.ResetToSpawn();
                }
                Step(1);
                firstDrawn += r1.Role == FreeRole.Catcher ? 1 : 0;
                foreach (ScriptedBody b in all)
                    b.Odds = (byte)(b == r1 ? 2 : b == r2 || b == r4 ? 1 : 0);
            }
            Check(firstDrawn > 75 && firstDrawn < 125, $"drawn by the weights: the first caught {firstDrawn} of 200");
        }

        // A catcher leaving: another at once, alike among the runners. Time up: whoever stands is next.
        void Leaving()
        {
            world = new HeadlessWorld();
            Begin(5, 3u);
            Step(match.HeadStartLeft);
            ScriptedBody gone = catcher;
            world.Simulation.Leave(gone);
            Step(1);
            int catchers = 0;
            ScriptedBody next = null;
            foreach (ScriptedBody b in runners)
                if (b.Role == FreeRole.Catcher)
                {
                    catchers++;
                    next = b;
                }
            Check(catchers == 1 && match.State.DrawnAt == match.State.RoundTicks - 1 && next.Entity.Get<DamagePercent>().Percent == HillMatch.StartPercent,
                  $"the catcher left: {catchers} drawn anew at {match.State.DrawnAt}");

            match.Restore(match.State with { RoundTicks = match.RoundLimitTicks - 1 });
            Step(1);
            IReadOnlyList<SimulationBody> bodies = world.Simulation.Bodies;
            bool standing = true;
            foreach (ScriptedBody b in runners)
                if (b != next)
                    standing &= b.Odds == 1 && !HillMatch.Won(b, bodies);
            Check(match.State.Phase == MatchPhase.Over && standing && next.Odds == HillMatch.Certain,
                  $"time up: the runners left over lose and are drawn next, the catcher who caught none certain ({next.Odds})");
        }

        void TwoCatchers()
        {
            world = new HeadlessWorld();
            Begin(11, 5u);
            Check(match.State.Catchers == 2 && runners.Count == 9, $"11 players: two catchers ({match.State.Catchers})");
        }

        // Inputs only from a snapshot: the wheel, the head start's barrier, then the catcher runs at a runner
        // swinging until it is caught; stepped again from the snapshot, every body is bit-identical.
        void Rollback()
        {
            world = new HeadlessWorld();
            Begin(5, 9u);
            ScriptedBody prey = runners[0];
            catcher.Motor.Teleport(UMath.Add(prey.Motor.Position, new Vector3(0f, 0f, 5f)));
            var snap = new WorldSnapshot();
            world.Simulation.Capture(snap);
            var inputs = new List<MoveInput[]>();
            recording = inputs;
            for (int t = 0; t < 1400 && prey.Role == FreeRole.Runner; t++)
            {
                Face(catcher, prey.Motor.BodyCenter);
                catcher.Input.Move = new Vector2(0f, Flat(catcher.Motor.Position, prey.Motor.Position) > 1.5f ? 1f : 0f);
                catcher.Input.AttackPresses = (byte)(t % 14 == 0 ? 1 : 0);
                Step(1);
            }
            recording = null;
            Check(prey.Role == FreeRole.Caught, $"caught by running at it after the head start ({prey.Role}, {inputs.Count} ticks)");
            var end = new string[all.Length];
            for (int i = 0; i < all.Length; i++)
                end[i] = Bytes(all[i].CaptureState());
            MatchState endMatch = match.State;
            world.Simulation.Resimulate(snap, inputs, SimulationSystem.TickSeconds);
            bool same = match.State.RoundTicks == endMatch.RoundTicks && match.State.Catchers == endMatch.Catchers;
            for (int i = 0; i < all.Length; i++)
                same &= Bytes(all[i].CaptureState()) == end[i];
            Check(same, $"rollback through wheel, head start and catch bit-identical ({inputs.Count} ticks)");
        }

        // The catcher beside the runner, facing it, swings once.
        void Strike(ScriptedBody runner)
        {
            catcher.Motor.Teleport(UMath.Add(runner.Motor.Position, new Vector3(0f, 0f, 1.5f)));
            Step(14);
            Face(catcher, runner.Motor.BodyCenter);
            catcher.Input.AttackPresses = 1;
            Step(1);
            catcher.Input.AttackPresses = 0;
        }

        // Yaw and pitch from the eye towards a point, the signs found from the motor's own rotation.
        static void Face(ScriptedBody body, Vector3 at)
        {
            Vector3 d = UMath.Sub(at, body.Motor.EyePosition);
            float flat = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
            float yaw = MathF.Atan2(-d.X, -d.Z) * (180f / MathF.PI);
            if (UMath.Dot(UMath.Rotate(UMath.Euler(0f, yaw), UMath.Forward), new Vector3(d.X, 0f, d.Z)) < 0f)
                yaw = -yaw;
            body.Input.Yaw = yaw;
            body.Input.Pitch = Up(body, MathF.Atan2(d.Y, flat) * (180f / MathF.PI));
        }

        // The pitch that looks this many degrees up (negative: down).
        static float Up(ScriptedBody body, float degrees)
        {
            Vector3 aim = UMath.Rotate(UMath.Euler(10f, body.Input.Yaw), UMath.Forward);
            return aim.Y > 0f ? degrees : -degrees;
        }

        List<MoveInput[]> recording;

        void Step(int n)
        {
            for (int t = 0; t < n; t++)
            {
                if (recording != null)
                {
                    IReadOnlyList<SimulationBody> bodies = world.Simulation.Bodies;
                    var tick = new MoveInput[bodies.Count];
                    for (int i = 0; i < bodies.Count; i++)
                        tick[i] = ((ScriptedBody)bodies[i]).Input;
                    recording.Add(tick);
                }
                world.Simulation.Tick();
            }
        }

        void Stop()
        {
            foreach (ScriptedBody b in all)
                b.Input = default;
        }

        static float Flat(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

        static string Bytes(BodyState s)
            => Convert.ToHexString(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<BodyState>(in s)));
    }
}
