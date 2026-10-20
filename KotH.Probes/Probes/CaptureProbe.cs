using System;
using System.Collections.Generic;
using KotH.Probes.Harness;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Probes
{
    /// <summary>
    /// Plays the hill with two scripted bodies and checks every rule tick by
    /// tick: three seconds alone before the first point, one point a second,
    /// nothing while shared, the warm-up starting over, the zone's edges,
    /// the win, the time limit with a leader and a draw, the reset to the
    /// spawn, and a rollback through all of it. No Unity reference: new.
    /// </summary>
    public sealed class CaptureProbe : Probe
    {
        protected override string Tag => "capture";

        // Stride metres. The plateau's top lies at y 5, x -6..6, z -22..-10.
        static readonly Vector3 SpawnA = new Vector3(0f, 0.9f, -2f);
        static readonly Vector3 SpawnB = new Vector3(3f, 0.9f, -2f);
        static readonly Vector3 HillA = new Vector3(-2f, 5.95f, -16f);
        static readonly Vector3 HillB = new Vector3(2f, 5.95f, -16f);

        HeadlessWorld world;
        HillMatch match;
        ScriptedBody a, b;
        int failures;

        public override int Run(HeadlessWorld host, int maxTicks)
        {
            world = host;
            world.LoadArena();
            match = world.StartMatch();
            a = Spawn("A", SpawnA);
            b = Spawn("B", SpawnB);
            Log($"[capture] Aufwärmen {match.WarmupTicks} Ticks, Sieg {match.WinTicks} Ticks, " +
                $"Zeitlimit {match.LimitTicks} Ticks, Pause {match.PauseTicks} Ticks");

            // The first tick records where both joined, before either moves.
            BodyState spawnA = a.CaptureState(), spawnB = b.CaptureState();
            Ticks(1);
            Check(a.HasSpawn && b.HasSpawn, "Spawn gemerkt");

            Warmup();
            Shared();
            Edges();
            Win(spawnA, spawnB);
            TimeLimit();
            Rollback();

            Log(failures == 0 ? "[capture] BESTANDEN" : $"[capture] {failures} Fehler");
            return failures == 0 ? 0 : 1;
        }

        ScriptedBody Spawn(string name, Vector3 at)
        {
            var body = new ScriptedBody();
            Entity e = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, at, Quaternion.Identity, body);
            e.Name = name;
            return body;
        }

        /// <summary>Alone on the hill: 120 ticks for nothing, then one point per 40.</summary>
        void Warmup()
        {
            a.Motor.Teleport(HillA);
            Ticks(match.WarmupTicks);
            Check(match.State.Holder == a && a.AloneTicks == match.WarmupTicks && a.ScoreTicks == 0,
                  $"3 s allein ohne Punkte (allein {a.AloneTicks}, Punkte-Ticks {a.ScoreTicks})");

            Ticks(1);
            Check(a.ScoreTicks == 1, $"erster Punkte-Tick direkt danach ({a.ScoreTicks})");

            Ticks(3 * HillMatch.TicksPerPoint - 1);
            Check(HillMatch.Points(a.ScoreTicks) == 3 && a.ScoreTicks == 3 * HillMatch.TicksPerPoint,
                  $"nach 6 s genau 3 Punkte ({a.ScoreTicks} Ticks)");
            Check(b.ScoreTicks == 0 && b.AloneTicks == 0, "B draußen bekommt nichts");
        }

        /// <summary>Two inside: nobody scores, and the warm-up starts over afterwards.</summary>
        void Shared()
        {
            int before = a.ScoreTicks;
            b.Motor.Teleport(HillB);
            Ticks(1);
            Check(match.State.Inside == 2 && match.State.Holder == null && a.AloneTicks == 0 && b.AloneTicks == 0,
                  $"umkämpft: drin {match.State.Inside}, allein A {a.AloneTicks} B {b.AloneTicks}");

            Ticks(200);
            Check(a.ScoreTicks == before && b.ScoreTicks == 0, $"umkämpft keine Punkte (A {a.ScoreTicks}, B {b.ScoreTicks})");

            b.Motor.Teleport(SpawnB);
            Ticks(match.WarmupTicks);
            Check(a.ScoreTicks == before, $"nach dem Teilen wieder 3 s ohne Punkte ({a.ScoreTicks - before})");
            Ticks(1);
            Check(a.ScoreTicks == before + 1, $"danach wieder Punkte ({a.ScoreTicks - before})");
        }

        /// <summary>The box against the body centre, and a jump that stays in.</summary>
        void Edges()
        {
            Check(match.Contains(new Vector3(5.99f, 5.9f, -16f)), "knapp innerhalb des Rands");
            Check(!match.Contains(new Vector3(6.01f, 5.9f, -16f)), "knapp außerhalb des Rands");
            Check(!match.Contains(new Vector3(0f, 5.9f, -9.99f)), "vor der Kante zur Rampe");
            Check(match.Contains(new Vector3(0f, 8.4f, -21.9f)), "hoch im Sprung am hinteren Rand");
            Check(!match.Contains(new Vector3(0f, 8.6f, -16f)), "über der Zone");
            Check(!match.Contains(new Vector3(-3.5f, 4.5f, -9f)), "auf der Rampe unterhalb");

            // A real jump on the hill must never break the streak.
            int alone = a.AloneTicks;
            a.Input.JumpPresses = 1;
            a.Input.JumpHeld = true;
            Ticks(1);
            a.Input.JumpPresses = 0;
            float top = 0f;
            for (int i = 0; i < 59; i++)
            {
                Ticks(1);
                top = Math.Max(top, a.Motor.Position.Y);
            }
            a.Input.JumpHeld = false;
            Ticks(30);
            Check(a.AloneTicks == alone + 90, $"Sprung bis y {top:F2} bleibt in der Zone (allein +{a.AloneTicks - alone})");
        }

        /// <summary>To the winning points, the pause, and back to the spawn.</summary>
        void Win(in BodyState spawnA, in BodyState spawnB)
        {
            int limit = match.WinTicks + match.WarmupTicks + 10;
            int t = 0;
            while (!match.State.Over && t++ < limit)
                Ticks(1);
            Check(match.State.Over && match.State.Winner == a && a.ScoreTicks == match.WinTicks,
                  $"Sieg bei {HillMatch.Points(a.ScoreTicks)} Punkten nach {t} Ticks, Sieger {Name(match.State.Winner)}");

            int score = a.ScoreTicks;
            Ticks(match.PauseTicks - 1);
            Check(match.State.Over && a.ScoreTicks == score && a.AloneTicks == 0, "Pause ohne Punkte");

            Ticks(1);
            Check(!match.State.Over && match.State.RoundTicks == 0, "neue Runde");
            Check(SameAsSpawn(a.CaptureState(), spawnA) && SameAsSpawn(b.CaptureState(), spawnB),
                  "beide bitgleich im Spawn-Zustand, Punkte 0");
        }

        /// <summary>Time runs out: the leader wins, an even top is a draw.</summary>
        void TimeLimit()
        {
            int saved = match.LimitTicks;

            a.Motor.Teleport(HillA);
            Ticks(match.WarmupTicks + 80);
            a.Motor.Teleport(SpawnA);
            b.Motor.Teleport(HillB);
            Ticks(match.WarmupTicks + 40);
            b.Motor.Teleport(SpawnB);
            match.LimitTicks = match.State.RoundTicks + 10;
            Ticks(10);
            Check(match.State.Over && match.State.Winner == a,
                  $"Zeit um: A {a.ScoreTicks} vor B {b.ScoreTicks}, Sieger {Name(match.State.Winner)}");
            Ticks(match.PauseTicks);
            Check(!match.State.Over && a.ScoreTicks == 0 && b.ScoreTicks == 0, "danach neue Runde");

            match.LimitTicks = 50;
            Ticks(50);
            Check(match.State.Over && match.State.Winner == null, "0 : 0 bei Zeitablauf ist unentschieden");
            Ticks(match.PauseTicks);

            match.LimitTicks = saved;
        }

        /// <summary>
        /// Captured mid warm-up, run on through points and off the hill,
        /// then restored and resimulated with the same inputs: same end.
        /// </summary>
        void Rollback()
        {
            a.Motor.Teleport(HillA);
            Ticks(60);

            var snap = new WorldSnapshot();
            world.Simulation.Capture(snap);

            // Walks to the -z edge and off it, past the warm-up on the way.
            var inputs = new List<MoveInput[]>();
            IReadOnlyList<SimulationBody> bodies = SimulationSystem.Bodies;
            for (int t = 0; t < 300; t++)
            {
                a.Input.Move = t >= 150 ? new Vector2(0f, 0.5f) : Vector2.Zero;
                var tick = new MoveInput[bodies.Count];
                for (int i = 0; i < bodies.Count; i++)
                    tick[i] = ((ScriptedBody)bodies[i]).Input;
                inputs.Add(tick);
                Ticks(1);
            }
            a.Input.Move = Vector2.Zero;

            BodyState endA = a.CaptureState(), endB = b.CaptureState();
            MatchState end = match.State;
            Check(endA.ScoreTicks > 0 && end.Holder == null, $"Lauf punktet und verlässt die Zone ({endA.ScoreTicks} Ticks)");

            world.Simulation.Resimulate(snap, inputs, SimulationSystem.TickSeconds);
            MatchState again = match.State;
            Check(Same(a.CaptureState(), endA) && Same(b.CaptureState(), endB) &&
                  again.RoundTicks == end.RoundTicks && again.Inside == end.Inside &&
                  again.Holder == end.Holder && again.Over == end.Over,
                  "Rollback und Resimulate bitgleich samt Runde");
        }

        void Ticks(int n)
        {
            for (int i = 0; i < n; i++)
                world.Simulation.Tick();
        }

        void Check(bool ok, string what)
        {
            if (!ok)
                failures++;
            Log($"[capture] {(ok ? "ok    " : "FEHLER")} {what}");
        }

        static string Name(SimulationBody body) => body == null ? "keiner" : body.Entity.Name;

        static bool SameAsSpawn(BodyState got, BodyState spawn) => Same(got, spawn) && got.ScoreTicks == 0;

        static bool Same(in BodyState x, in BodyState y)
            => Bits(x.Motor.Position, y.Motor.Position) && Bits(x.Motor.Velocity, y.Motor.Velocity) &&
               x.Motor.Rotation.Equals(y.Motor.Rotation) && x.Motor.Equals(y.Motor) &&
               x.DamagePercent.Equals(y.DamagePercent) && x.AttackCooldown == y.AttackCooldown &&
               x.ScoreTicks == y.ScoreTicks && x.AloneTicks == y.AloneTicks;

        static bool Bits(Vector3 x, Vector3 y)
            => BitConverter.SingleToInt32Bits(x.X) == BitConverter.SingleToInt32Bits(y.X) &&
               BitConverter.SingleToInt32Bits(x.Y) == BitConverter.SingleToInt32Bits(y.Y) &&
               BitConverter.SingleToInt32Bits(x.Z) == BitConverter.SingleToInt32Bits(y.Z);
    }
}
