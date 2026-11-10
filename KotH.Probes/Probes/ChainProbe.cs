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
    /// <summary>The duo chain (4 m, Luca) on the Test arena: partners running apart stay within it,
    /// one walking away drags the other, a partner over the plateau's edge hangs and is pulled up, a
    /// knock drags the partner along, a rollback through it is bit-identical, solo has no chain.</summary>
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

            // A knocked away from B drags B along.
            a.Motor.Teleport(new Vector3(0f, 0.9f, -2f));
            b.Motor.Teleport(new Vector3(3f, 0.9f, -2f));
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

            Log(failures == 0 ? "[chain] PASSED" : $"[chain] {failures} errors");
            return failures == 0 ? 0 : 1;
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
                if (match.Duo)
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

        void Check(bool ok, string what)
        {
            if (!ok)
                failures++;
            Log($"[chain] {(ok ? "ok    " : "ERROR ")} {what}");
        }

        static bool Same(in BodyState x, in BodyState y)
            => x.Motor.Equals(y.Motor) && x.ScoreTicks == y.ScoreTicks && x.Partner == y.Partner;
    }
}
