using System;
using System.Collections.Generic;
using KotH.Probes.Harness;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Net;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Probes
{
    /// <summary>Plays the hill with two scripted bodies and checks every rule tick by tick: 3 s
    /// alone, a point a second, nothing while shared, warm-up restarts, zone edges, win, time limit
    /// with leader and draw, spawn reset and a rollback through it all; then a networked round's
    /// lobby and the twelve slots. No Unity reference.</summary>
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
            world.LoadArena(Maps.Test);
            match = world.StartMatch();
            a = Spawn("A", SpawnA);
            b = Spawn("B", SpawnB);
            Log($"[capture] capture time {match.WarmupTicks} ticks, win {match.WinTicks} ticks, " +
                $"time limit {match.LimitTicks} ticks, pause {match.PauseTicks} ticks");

            // The first tick records where both joined, before either moves.
            BodyState spawnA = a.CaptureState(), spawnB = b.CaptureState();
            Ticks(1);
            Check(a.HasSpawn && b.HasSpawn, "spawn remembered");

            Warmup();
            Shared();
            Edges();
            Win(spawnA, spawnB);
            TimeLimit();
            Rollback();
            OutOfArena(spawnA);
            Lobby();
            Slots();

            Log(failures == 0 ? "[capture] PASSED" : $"[capture] {failures} errors");
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
                  $"3 s alone without points (alone {a.AloneTicks}, point ticks {a.ScoreTicks})");

            Ticks(1);
            Check(a.ScoreTicks == 1, $"first point tick right after ({a.ScoreTicks})");

            Ticks(3 * HillMatch.TicksPerPoint - 1);
            Check(HillMatch.Points(a.ScoreTicks) == 3 && a.ScoreTicks == 3 * HillMatch.TicksPerPoint,
                  $"after 6 s exactly 3 points ({a.ScoreTicks} ticks)");
            Check(b.ScoreTicks == 0 && b.AloneTicks == 0, "B outside gets nothing");
        }

        /// <summary>Taken, the zone stays its holder's with others inside (Luca's rule);
        /// the holder gone, the other takes it in 3 s first; two inside while it is being
        /// taken: nobody scores and the warm-up starts over.</summary>
        void Shared()
        {
            int before = a.ScoreTicks;
            b.Motor.Teleport(HillB);
            Ticks(1);
            Check(match.State.Inside == 2 && match.State.Holder == a && b.AloneTicks == 0 && a.ScoreTicks == before + 1,
                  $"captured: A holds with B inside (inside {match.State.Inside}, holder {Name(match.State.Holder)})");
            Ticks(200);
            Check(a.ScoreTicks == before + 201 && b.ScoreTicks == 0,
                  $"A keeps scoring, B does not (A +{a.ScoreTicks - before}, B {b.ScoreTicks})");

            a.Motor.Teleport(SpawnA);
            Ticks(match.WarmupTicks);
            Check(match.State.Holder == b && b.AloneTicks == match.WarmupTicks && b.ScoreTicks == 0 && a.AloneTicks == 0,
                  $"A outside: B first captures for 3 s (alone {b.AloneTicks}, point ticks {b.ScoreTicks})");
            Ticks(1);
            Check(b.ScoreTicks == 1, $"then B scores ({b.ScoreTicks})");

            b.Motor.Teleport(SpawnB);
            Ticks(1);
            Check(match.State.Inside == 0 && match.State.Holder == null, "both outside: free");
            a.Motor.Teleport(HillA);
            Ticks(40);
            b.Motor.Teleport(HillB);
            Ticks(1);
            Check(match.State.Inside == 2 && match.State.Holder == null && a.AloneTicks == 0 && b.AloneTicks == 0,
                  $"contested while capturing: inside {match.State.Inside}, alone A {a.AloneTicks} B {b.AloneTicks}");
            int scoreA = a.ScoreTicks, scoreB = b.ScoreTicks;
            Ticks(200);
            Check(a.ScoreTicks == scoreA && b.ScoreTicks == scoreB,
                  $"contested no points (A +{a.ScoreTicks - scoreA}, B +{b.ScoreTicks - scoreB})");

            b.Motor.Teleport(SpawnB);
            Ticks(match.WarmupTicks);
            Check(a.ScoreTicks == scoreA, $"after sharing again 3 s without points ({a.ScoreTicks - scoreA})");
            Ticks(1);
            Check(a.ScoreTicks == scoreA + 1, $"then points again ({a.ScoreTicks - scoreA})");
        }

        /// <summary>The box against the body centre, and a jump that stays in.</summary>
        void Edges()
        {
            Check(match.Contains(new Vector3(5.99f, 5.9f, -16f)), "just inside the edge");
            Check(!match.Contains(new Vector3(6.01f, 5.9f, -16f)), "just outside the edge");
            Check(!match.Contains(new Vector3(0f, 5.9f, -9.99f)), "before the edge to the ramp");
            Check(match.Contains(new Vector3(0f, 8.4f, -21.9f)), "high in a jump at the back edge");
            Check(!match.Contains(new Vector3(0f, 8.6f, -16f)), "above the zone");
            Check(!match.Contains(new Vector3(-3.5f, 4.5f, -9f)), "on the ramp below");

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
            Check(a.AloneTicks == alone + 90, $"jump to y {top:F2} stays in the zone (alone +{a.AloneTicks - alone})");
        }

        /// <summary>To the winning points, the pause, and back to the spawn.</summary>
        void Win(in BodyState spawnA, in BodyState spawnB)
        {
            int limit = match.WinTicks + match.WarmupTicks + 10;
            int t = 0;
            while (!match.State.Over && t++ < limit)
                Ticks(1);
            Check(match.State.Over && match.State.Winner == a && a.ScoreTicks == match.WinTicks,
                  $"win at {HillMatch.Points(a.ScoreTicks)} points after {t} ticks, winner {Name(match.State.Winner)}");

            int score = a.ScoreTicks;
            Ticks(match.PauseTicks - 1);
            Check(match.State.Over && a.ScoreTicks == score && a.AloneTicks == 0, "pause without points");

            Ticks(1);
            Check(!match.State.Over && match.State.RoundTicks == 0, "new round");
            Check(SameAsSpawn(a.CaptureState(), spawnA) && SameAsSpawn(b.CaptureState(), spawnB),
                  "both bit-identical in the spawn state, points 0");
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
                  $"time up: A {a.ScoreTicks} ahead of B {b.ScoreTicks}, winner {Name(match.State.Winner)}");
            Ticks(match.PauseTicks);
            Check(!match.State.Over && a.ScoreTicks == 0 && b.ScoreTicks == 0, "then new round");

            match.LimitTicks = 50;
            Ticks(50);
            Check(match.State.Over && match.State.Winner == null, "0 : 0 at time up is a draw");
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
            IReadOnlyList<SimulationBody> bodies = world.Simulation.Bodies;
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
            Check(endA.ScoreTicks > 0 && end.Holder == null, $"run scores and leaves the zone ({endA.ScoreTicks} ticks)");

            world.Simulation.Resimulate(snap, inputs, SimulationSystem.TickSeconds);
            MatchState again = match.State;
            Check(Same(a.CaptureState(), endA) && Same(b.CaptureState(), endB) &&
                  again.RoundTicks == end.RoundTicks && again.Inside == end.Inside &&
                  again.Holder == end.Holder && again.Over == end.Over,
                  "rollback and resimulate bit-identical including the round");
        }

        /// <summary>Waiting for four players, the countdown, its stop when one leaves, the start
        /// from the spawns, the join window, and back to waiting after a round. Dummies (NetId 1 to
        /// 3) never count.</summary>
        /// <summary>Off the edge of the arena: back at the spawn below -5 m, points kept.</summary>
        void OutOfArena(in BodyState spawnA)
        {
            int carried = a.ScoreTicks;
            a.Motor.Teleport(new Vector3(0f, 2f, 40f));
            float lowest = float.MaxValue;
            for (int i = 0; i < 80; i++)
            {
                Ticks(1);
                lowest = Math.Min(lowest, a.Motor.Position.Y);
            }
            float off = Vector3.Distance(a.Motor.Position, spawnA.Motor.Position);
            Check(lowest < match.RespawnBelow + 0.5f && off < 0.5f && a.ScoreTicks == carried,
                  $"fell out of the arena: back at the spawn point ({off:F2} m off), points kept");
        }

        void Lobby()
        {
            match.MinPlayers = 4;
            a.NetId = NetWorld.FirstPlayerId;
            b.NetId = NetWorld.FirstPlayerId + 1;
            ScriptedBody dummy = Spawn("Dummy", new Vector3(-3f, 0.9f, -2f));
            dummy.NetId = 1;
            ScriptedBody c = Spawn("C", new Vector3(-6f, 0.9f, -2f));
            c.NetId = NetWorld.FirstPlayerId + 2;

            int carried = a.ScoreTicks;
            a.Motor.Teleport(HillA);
            Ticks(200);
            Check(match.State.Phase == MatchPhase.Waiting && match.State.Holder == null && a.ScoreTicks == carried &&
                  match.AcceptsJoin && HillMatch.CountPlayers(world.Simulation.Bodies) == 3,
                  $"3 players and a dummy: waiting, the zone does not count ({a.ScoreTicks - carried})");

            ScriptedBody d = Spawn("D", new Vector3(-9f, 0.9f, -2f));
            d.NetId = NetWorld.FirstPlayerId + 3;
            Ticks(1);
            Check(match.State.Phase == MatchPhase.Countdown && match.State.PhaseTicks == match.CountdownTicks,
                  $"4th player starts the countdown ({match.State.PhaseTicks} ticks)");

            Ticks(100);
            BodyState left = d.CaptureState();
            world.Simulation.Leave(d);
            Ticks(1);
            Check(match.State.Phase == MatchPhase.Waiting, "one leaves: back to waiting");

            world.Simulation.Rejoin(d, left);
            Ticks(1);
            Check(match.State.Phase == MatchPhase.Countdown && match.State.PhaseTicks == match.CountdownTicks,
                  "back again: countdown from the start");

            Ticks(match.CountdownTicks - 1);
            Check(match.State.Phase == MatchPhase.Countdown && match.State.PhaseTicks == 1 && a.ScoreTicks == carried,
                  "countdown runs to the last tick without points");
            Ticks(1);
            Check(match.State.Phase == MatchPhase.Running && match.State.RoundTicks == 0 &&
                  match.State.MinPlayers == 4 && SameAsSpawn(a.CaptureState(), a.SpawnState),
                  "at 0 the round runs, all at their spawn points");

            Ticks(match.JoinTicks - 1);
            bool open = match.AcceptsJoin;
            Ticks(1);
            Check(open && !match.AcceptsJoin, $"join until tick {match.JoinTicks} of the round, then closed");

            match.LimitTicks = match.State.RoundTicks + 1;
            Ticks(1);
            Check(match.State.Over && match.AcceptsJoin, "round over, join open again");
            Ticks(match.PauseTicks);
            Check(match.State.Phase == MatchPhase.Waiting, "after the pause waiting again");
            Ticks(1);
            Check(match.State.Phase == MatchPhase.Countdown, "enough players: countdown again at once");

            match.LimitTicks = Math.Max(match.LimitTicks, 48000);
            match.MinPlayers = 0;
            Check(match.State.Phase == MatchPhase.Running && match.State.RoundTicks == 0, "without a minimum at once as offline");
        }

        /// <summary>All twelve slots and the dummies at once: everyone stands where placed.</summary>
        void Slots()
        {
            var w = new HeadlessWorld();
            w.LoadArena(Maps.Test);
            NetWorld.SpawnDummies(w.Builder);
            var net = new HeadlessNetWorld(w);
            var players = new List<SimulationBody>();
            for (int slot = 0; slot < NetWorld.MaxPlayers; slot++)
                players.Add(net.SpawnPlayer(NetWorld.FirstPlayerId + slot, slot, "S" + slot));

            for (int i = 0; i < 80; i++)
                w.Simulation.Tick();

            int moved = 0;
            float worst = 0f;
            for (int slot = 0; slot < players.Count; slot++)
            {
                Vector3 want = NetWorld.SlotMarker(w.Builder, slot).Transform.Position;
                Vector3 got = players[slot].Motor.Position;
                float off = MathF.Max(MathF.Abs(got.X - want.X), MathF.Max(MathF.Abs(got.Z - want.Z), MathF.Abs(got.Y - want.Y)));
                worst = MathF.Max(worst, off);
                if (off > 0.05f)
                {
                    moved++;
                    Log($"[capture]        S{slot} at {want} stands at {got}");
                }
            }
            Check(players.Count == 12 && moved == 0, $"12 spawn points free and on the ground (largest deviation {worst * 100f:F1} cm)");
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
            Log($"[capture] {(ok ? "ok    " : "ERROR ")} {what}");
        }

        static string Name(SimulationBody body) => body == null ? "nobody" : body.Entity.Name;

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
