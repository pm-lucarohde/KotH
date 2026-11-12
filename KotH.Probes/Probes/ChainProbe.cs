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
    /// <summary>The team chain (5 m, Luca) on the Test arena: partners running apart stay within it,
    /// one walking away drags the other, a partner over the plateau's edge hangs and is pulled up, a
    /// hanging one spore-jumps again after a cooldown, a knock drags the partner along, a rollback
    /// through it is bit-identical, solo has no chain.</summary>
    public sealed class ChainProbe : Probe
    {
        protected override string Tag => "chain";

        HeadlessWorld world;
        HillMatch match;
        ScriptedBody a, b;
        int failures;
        float longest;

        public override int Run(HeadlessWorld host, int maxTicks)
        {
            world = host;
            world.LoadArena(Maps.Test);
            match = world.StartMatch();
            match.Mode = MatchMode.Duo;
            a = Spawn("A", new Vector3(0f, 0.9f, -2f), NetWorld.FirstPlayerId);
            b = Spawn("B", new Vector3(2f, 0.9f, -2f), NetWorld.FirstPlayerId + 1);
            Ticks(1);
            Check(HillMatch.SameTeam(a, b), "A and B paired");
            float length = world.Simulation.ChainLength;

            // Apart, each at a run: the chain holds them.
            a.Input.Move = new Vector2(-1f, 0f);
            b.Input.Move = new Vector2(1f, 0f);
            Ticks(120);
            Check(longest <= length + 0.01f, $"running apart: at most {longest:F3} m apart");
            Stop();

            // B walks off, A stands: A comes along.
            Vector3 startA = a.Motor.Position;
            b.Input.Move = new Vector2(1f, 0f);
            Ticks(160);
            float dragged = a.Motor.Position.X - startA.X;
            Check(dragged > 2f && longest <= length + 0.01f, $"B walks away: A dragged {dragged:F2} m, at most {longest:F3} m apart");
            Stop();
            Ticks(40);

            // Over the plateau's side (x 6): B on top holds A hanging.
            b.Motor.Teleport(new Vector3(4.5f, 5.95f, -16f));
            a.Motor.Teleport(new Vector3(7.5f, 5.95f, -16f));
            var snap = new WorldSnapshot();
            world.Simulation.Capture(snap);
            var inputs = new List<MoveInput[]>();
            Record(inputs, 80);
            Check(a.Motor.Position.Y > 2f && a.Motor.Position.Y < 5.9f && b.Motor.Position.X < 6f && b.Motor.Position.Y > 5.5f,
                  $"hanging: A at y {a.Motor.Position.Y:F2}, B on top at x {b.Motor.Position.X:F2} y {b.Motor.Position.Y:F2}");

            // B walks back from the edge and pulls A up onto the plateau.
            b.Input.Move = new Vector2(-1f, 0f);
            Record(inputs, 50);
            b.Input.Move = Vector2.Zero;
            Record(inputs, 40);
            Check(a.Motor.Position.Y > 5.5f && a.Motor.Position.X < 6.2f,
                  $"pulled up: A at x {a.Motor.Position.X:F2} y {a.Motor.Position.Y:F2}");

            BodyState endA = a.CaptureState(), endB = b.CaptureState();
            world.Simulation.Resimulate(snap, inputs, SimulationSystem.TickSeconds);
            Check(Same(a.CaptureState(), endA) && Same(b.CaptureState(), endB), "rollback through hanging and pulling bit-identical");

            HangJumps(length);

            // A knocked away from B drags B along.
            a.Motor.Teleport(new Vector3(0f, 0.9f, -2f));
            b.Motor.Teleport(new Vector3(length - 1f, 0.9f, -2f));
            Ticks(20);
            Vector3 startB = b.Motor.Position;
            a.Motor.Knock(new Vector3(-1f, 0f, 0f), 12f, 1.2f, 0f, 0f, SimulationSystem.TickSeconds);
            longest = 0f;
            Ticks(80);
            float pulled = startB.X - b.Motor.Position.X;
            Check(pulled > 1f && longest <= length + 0.01f, $"knock: B dragged {pulled:F2} m, at most {longest:F3} m apart");

            // Solo: no chain.
            match.Mode = MatchMode.Solo;
            a.Input.Move = new Vector2(-1f, 0f);
            b.Input.Move = new Vector2(1f, 0f);
            Ticks(80);
            Stop();
            float apart = Vector3.Distance(a.Motor.BodyCenter, b.Motor.BodyCenter);
            Check(apart > length + 2f, $"solo: no chain ({apart:F2} m apart)");
            a.Motor.Teleport(new Vector3(0f, 4f, -2f));
            Ticks(4);
            DoubleTap(a, null);
            Check(a.Motor.Velocity.Y < 0f, $"solo: no jump falling without a ground jump (v.Y {a.Motor.Velocity.Y:F2})");
            Ticks(40);

            Row(length);

            Log(failures == 0 ? "[chain] PASSED" : $"[chain] {failures} errors");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>A hangs free off the plateau's sheer side (chain 3 m, clear of the floor): the double tap
        /// spore-jumps, again at once nothing, after the cooldown again (Luca); a rollback replays it.</summary>
        void HangJumps(float length)
        {
            world.Simulation.ChainLength = 3f;
            b.Motor.Teleport(new Vector3(5f, 5.95f, -20f));
            a.Motor.Teleport(new Vector3(6.8f, 5.95f, -20f));
            var snap = new WorldSnapshot();
            world.Simulation.Capture(snap);
            var inputs = new List<MoveInput[]>();
            Record(inputs, 80);
            Check(!a.Motor.IsGrounded && a.Motor.Position.Y > 2f && a.Motor.Position.Y < 4.5f && b.Motor.Position.Y > 5.5f,
                  $"free hang: A at y {a.Motor.Position.Y:F2} in the air, B on top at y {b.Motor.Position.Y:F2}");
            float before = a.Motor.Velocity.Y;
            DoubleTap(a, inputs);
            float first = a.Motor.Velocity.Y;
            Record(inputs, 4);
            float rising = a.Motor.Velocity.Y;
            DoubleTap(a, inputs);
            float denied = a.Motor.Velocity.Y;
            Record(inputs, 50);
            float fallen = a.Motor.Velocity.Y;
            DoubleTap(a, inputs);
            float again = a.Motor.Velocity.Y;
            Check(before < 0f && first > 5f && denied < rising && fallen < 0f && again > 5f,
                  $"hang jumps: v.Y {before:F2} -> {first:F2}, at once {rising:F2} -> {denied:F2}, after 1.4 s {fallen:F2} -> {again:F2}");
            Record(inputs, 40);
            BodyState endA = a.CaptureState(), endB = b.CaptureState();
            world.Simulation.Resimulate(snap, inputs, SimulationSystem.TickSeconds);
            Check(Same(a.CaptureState(), endA) && Same(b.CaptureState(), endB), "rollback through the hang jumps bit-identical");
            world.Simulation.ChainLength = length;
        }

        /// <summary>Squad: four in a row A-B-C-D, the ends run apart (and one is knocked): every link holds,
        /// the middle ones are dragged along, and a rollback replays it bit-exactly.</summary>
        void Row(float length)
        {
            world = new HeadlessWorld();
            world.LoadArena(Maps.Test);
            match = world.StartMatch();
            match.Mode = MatchMode.Squad;
            var row = new ScriptedBody[4];
            for (int i = 0; i < row.Length; i++)
                row[i] = Spawn(((char)('A' + i)).ToString(), new Vector3(-2f + 2f * i, 0.9f, 5f), NetWorld.FirstPlayerId + i);   // open ground
            a = row[0];
            b = row[3];
            Ticks(1);
            bool chained = true;
            for (int i = 1; i < row.Length; i++)
                chained &= HillMatch.AheadOf(row[i], world.Simulation.Bodies) == row[i - 1];
            Check(chained, "squad: one team, chained A-B-C-D");
            var snap = new WorldSnapshot();
            world.Simulation.Capture(snap);
            var inputs = new List<MoveInput[]>();
            a.Input.Move = new Vector2(-1f, 0f);
            b.Input.Move = new Vector2(1f, 0f);
            float link = 0f;
            for (int t = 0; t < 120; t++)
            {
                Record(inputs, 1);
                link = Math.Max(link, Longest(row));
            }
            float ends = Vector3.Distance(row[0].Motor.BodyCenter, row[3].Motor.BodyCenter);
            Check(link <= length + 0.05f, $"squad: the ends apart, every link holds (longest {link:F3} m, ends {ends:F2} m apart)");
            BodyState endA = row[0].CaptureState(), endD = row[3].CaptureState();
            world.Simulation.Resimulate(snap, inputs, SimulationSystem.TickSeconds);
            Check(Same(row[0].CaptureState(), endA) && Same(row[3].CaptureState(), endD), "squad: rollback bit-identical");

            // The end knocked away drags the whole row.
            Stop();
            Ticks(40);
            Vector3 startD = row[3].Motor.Position;
            a.Motor.Knock(new Vector3(-1f, 0f, 0f), 12f, 1.2f, 0f, 0f, SimulationSystem.TickSeconds);
            link = 0f;
            for (int t = 0; t < 80; t++)
            {
                Ticks(1);
                link = Math.Max(link, Longest(row));
            }
            float pulled = startD.X - row[3].Motor.Position.X;
            Check(pulled > 0.5f && link <= length + 0.05f, $"squad: A knocked drags D {pulled:F2} m, longest link {link:F3} m");
            Stop();
        }

        static float Longest(ScriptedBody[] row)
        {
            float link = 0f;
            for (int i = 1; i < row.Length; i++)
                link = Math.Max(link, Vector3.Distance(row[i - 1].Motor.BodyCenter, row[i].Motor.BodyCenter));
            return link;
        }

        ScriptedBody Spawn(string name, Vector3 at, int netId)
        {
            var body = new ScriptedBody();
            Entity e = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, at, Quaternion.Identity, body);
            e.Name = name;
            body.NetId = netId;
            return body;
        }

        void Stop()
        {
            a.Input.Move = b.Input.Move = Vector2.Zero;
            longest = 0f;
        }

        void Ticks(int n)
        {
            for (int i = 0; i < n; i++)
            {
                world.Simulation.Tick();
                if (match.Teamed && b.Team != 0)
                    longest = Math.Max(longest, Vector3.Distance(a.Motor.BodyCenter, b.Motor.BodyCenter));
            }
        }

        void Record(List<MoveInput[]> into, int n)
        {
            IReadOnlyList<SimulationBody> bodies = world.Simulation.Bodies;
            for (int t = 0; t < n; t++)
            {
                var tick = new MoveInput[bodies.Count];
                for (int i = 0; i < bodies.Count; i++)
                    tick[i] = ((ScriptedBody)bodies[i]).Input;
                into.Add(tick);
                Ticks(1);
            }
        }

        // Two taps two ticks apart, the second in the last tick run.
        void DoubleTap(ScriptedBody body, List<MoveInput[]> into)
        {
            for (int t = 0; t < 3; t++)
            {
                body.Input.JumpPresses = (byte)(t == 1 ? 0 : 1);
                if (into != null)
                    Record(into, 1);
                else
                    Ticks(1);
            }
            body.Input.JumpPresses = 0;
        }

        void Check(bool ok, string what)
        {
            if (!ok)
                failures++;
            Log($"[chain] {(ok ? "ok    " : "ERROR ")} {what}");
        }

        static bool Same(in BodyState x, in BodyState y)
            => x.Motor.Equals(y.Motor) && x.ScoreTicks == y.ScoreTicks && x.Team == y.Team;
    }
}
