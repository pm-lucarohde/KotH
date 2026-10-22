using System;
using System.Collections.Generic;
using System.Linq;
using KotH.Probes.Harness;
using KotH.Shared.Bots;
using KotH.Shared.Combat;
using KotH.Shared.Hosting;
using KotH.Shared.Mathematics;
using KotH.Shared.Net;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Probes
{
    public sealed partial class BotProbe
    {
        /// <summary>Links for "bots jumplab N": how jumps up the mountain of map Neu land.</summary>
        public int Lab = 0;

        struct Way
        {
            public string Name;
            public float Speed;      // m/s along the jump when it starts
            public float Back;       // metres behind the take-off it starts
            public int Hold;         // ticks after the jump before pressing forward
            public int Double;       // ticks after the jump of the second press, -1 none
            public bool Sprint;
            public int Wait;         // ticks it costs to stand still first
            public bool Always;      // a double jump on every step, single ones too
            public int Pilot;        // flown by the JumpPilot, seeing this many ticks late (0: none, else delay + 1)
        }

        /// <summary>
        /// A lab for the climb, no bot and no reaction time: random jumps and
        /// double jumps of the climbing grid on the mountain, each tried in
        /// several ways with the real motor. Scores a way by the seconds to
        /// the zone it gains per second spent, landing anywhere higher up.
        /// </summary>
        int Labs(int links)
        {
            using var world = new HeadlessWorld();
            world.LoadArena(Maps.Default);
            var states = new LocalFinalStates(world.Simulation);
            BotDirector director = BotDirector.Create(world.Builder, world.Simulation, states, 1);
            ClimbGrid grid = director.Navigation.Grid;
            var pilot = new JumpPilot(grid, world.Builder.Template<PlayerMotor>(WorldBuilder.PlayerBodyUrl));

            var body = new ScriptedBody { NetId = 4 };
            Entity marker = NetWorld.SlotMarker(world.Builder, 0);
            world.SpawnBody(HeadlessWorld.PlayerBodyUrl, marker.Transform.Position, marker.Transform.Rotation, body, new DamagePercent());
            for (int t = 0; t < 20; t++)
                world.Simulation.Tick();
            MotorState rest = body.Motor.CaptureState();

            // The ground around a spot: top surface and the ceiling above the feet, every 0,5 m.
            if (Environment.GetEnvironmentVariable("KOTH_LAB_LOOK") is string look)
            {
                float[] c = look.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                var q = body.Motor.World;
                for (float dz = 3f; dz >= -3f; dz -= 0.5f)
                {
                    var row = new List<string>();
                    for (float dx = -3f; dx <= 3f; dx += 0.5f)
                    {
                        var top = new Vector3(c[0] + dx, c[1] + 8f, c[2] + dz);
                        float y = q.Raycast(top, new Vector3(0f, -1f, 0f), 16f, KotH.Shared.Physics.WorldQueries.WorldMask, out var hit) ? top.Y - hit.Distance - c[1] : -99f;
                        var foot = new Vector3(c[0] + dx, c[1] + 0.2f, c[2] + dz);
                        string roof = q.Raycast(foot, new Vector3(0f, 1f, 0f), 6f, KotH.Shared.Physics.WorldQueries.WorldMask, out var ceil) ? FormattableString.Invariant($"^{ceil.Distance + 0.2f:F1}") : "";
                        row.Add(FormattableString.Invariant($"{y,5:F1}{roof,-5}"));
                    }
                    Log(FormattableString.Invariant($"[labor] z{dz,5:F1}: {string.Join(" ", row)}"));
                }
                return 0;
            }

            // A spot where a bot got stuck: can a body there get free with the keys?
            if (Environment.GetEnvironmentVariable("KOTH_LAB_STUCK") is string spot)
            {
                string[] xyz = spot.Split(',');
                var at = new Vector3(float.Parse(xyz[0], System.Globalization.CultureInfo.InvariantCulture),
                                     float.Parse(xyz[1], System.Globalization.CultureInfo.InvariantCulture),
                                     float.Parse(xyz[2], System.Globalization.CultureInfo.InvariantCulture));
                for (int d = 0; d < 8; d++)
                    foreach (bool jump in new[] { false, true })
                    {
                        MotorState s0 = rest;
                        s0.Position = at;
                        s0.Velocity = Vector3.Zero;
                        body.Motor.RestoreState(s0);
                        body.Motor.Teleport(at);
                        for (int t = 0; t < 40; t++)
                        {
                            body.Input = default;
                            world.Simulation.Tick();
                        }
                        Vector3 settled = body.Motor.Position;
                        for (int t = 0; t < 80; t++)
                        {
                            body.Input = new MoveInput { Yaw = d * 45f, Move = new Vector2(0f, 1f), JumpPresses = (byte)(jump && t % 10 == 0 ? 1 : 0), JumpHeld = jump };
                            world.Simulation.Tick();
                        }
                        MotorState m = body.Motor.CaptureState();
                        Log(FormattableString.Invariant($"[labor] klemmt bei {settled.X:F2} {settled.Y:F2} {settled.Z:F2}: Richtung {d * 45}°{(jump ? " mit Sprung" : "")} -> {m.Position.X:F2} {m.Position.Y:F2} {m.Position.Z:F2}, Boden {m.Grounded}, steil {m.OnSteepGround}"));
                    }
                return 0;
            }

            var ways = new List<Way>
            {
                new Way { Name = "Pilot, sieht sofort", Pilot = 1 },
                new Way { Name = "Pilot, 8 Ticks spät", Pilot = 9 },
                new Way { Name = "Stand, vorwärts 6", Hold = 6, Double = 6, Wait = 8 },
                new Way { Name = "Stand, vorwärts 6, Doppel 8", Hold = 6, Double = 8, Wait = 8 },
                new Way { Name = "Stand, vorwärts 0, Doppel 8", Hold = 0, Double = 8, Wait = 8 },
                new Way { Name = "Stand, vorwärts 6, immer Doppel 6", Hold = 6, Double = 6, Wait = 8, Always = true },
                new Way { Name = "Stand, vorwärts 6, immer Doppel 8", Hold = 6, Double = 8, Wait = 8, Always = true },
                new Way { Name = "Stand, vorwärts 3, immer Doppel 6", Hold = 3, Double = 6, Wait = 8, Always = true },
                new Way { Name = "Stand, vorwärts 0, immer Doppel 10", Hold = 0, Double = 10, Wait = 8, Always = true },
                new Way { Name = "Gehen 2,5 m/s, immer Doppel 6", Speed = 2.5f, Hold = 0, Double = 6, Always = true },
                new Way { Name = "Gehen 2,5 m/s", Speed = 2.5f, Back = 0f, Hold = 0, Double = 6 },
                new Way { Name = "Gehen 5 m/s", Speed = 5f, Back = 0f, Hold = 0, Double = 6 },
                new Way { Name = "Gehen 5 m/s, vorwärts 4", Speed = 5f, Back = 0f, Hold = 4, Double = 6 },
                new Way { Name = "Sprint 8,6 m/s", Speed = 8.6f, Back = 0f, Hold = 0, Double = 6, Sprint = true },
                new Way { Name = "Gehen 5 m/s, 0,5 m zurück", Speed = 5f, Back = 0.5f, Hold = 0, Double = 6 },
            };

            var gain = new double[ways.Count, 2];
            var binGain = new double[ways.Count, 2, 5];
            var binSpent = new double[ways.Count, 2, 5];
            var binN = new int[2, 5];
            var spent = new double[ways.Count, 2];
            var up = new int[ways.Count, 2];
            int[] total = new int[2];
            uint rng = 12345u;
            int picked = 0;
            bool dump = Environment.GetEnvironmentVariable("KOTH_LAB_DUMP") != null;
            for (int guard = 0; picked < links && guard < links * 200; guard++)
            {
                int i = (int)(BotRandom.Next(ref rng) % (uint)grid.LinkCount);
                LinkKind kind = grid.LinkAt(i, out Vector3 from, out Vector3 to);
                if (kind != LinkKind.Jump && kind != LinkKind.DoubleJump || to.Y - from.Y < 0.6f || from.Y < 8f || from.Y > 110f)
                    continue;
                picked++;
                int k = kind == LinkKind.DoubleJump ? 1 : 0;
                total[k]++;
                float before = grid.TimeOf(grid.FloorAt(from));
                float reach = UMath.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Z - from.Z) * (to.Z - from.Z));
                int bin = Math.Min(4, (int)(reach / 0.5f));
                binN[k, bin]++;
                if (dump)
                {
                    int dt = Fly(world, body, rest, pilot, from, to, 8, out Vector3 lf);
                    var prof = new List<string>();
                    for (float o = -0.5f; o <= reach + 1.01f; o += 0.25f)
                    {
                        float px = from.X + (to.X - from.X) / MathF.Max(reach, 1e-3f) * o, pz = from.Z + (to.Z - from.Z) / MathF.Max(reach, 1e-3f) * o;
                        prof.Add($"{grid.SurfaceAt(px, pz, from.Y + 3f) - from.Y:F1}");
                    }
                    float dx = lf.X - to.X, dz = lf.Z - to.Z;
                    string res = MathF.Sqrt(dx * dx + dz * dz) < 0.6f && MathF.Abs(lf.Y - to.Y) < 0.3f ? "ZIEL" : lf.Y > from.Y + 0.3f ? "höher" : lf.Y > from.Y - 0.3f ? "gleich" : "tiefer";
                    Log(FormattableString.Invariant($"[labor] {kind} weite {reach:F2} anstieg {to.Y - from.Y:F2} -> {res} (ab Ziel {dx:F2} {lf.Y - to.Y:F2} {dz:F2}, {dt} Ticks) profil {string.Join(' ', prof)}"));
                }
                for (int w = 0; w < ways.Count; w++)
                {
                    int ticks = ways[w].Pilot > 0 ? Fly(world, body, rest, pilot, from, to, ways[w].Pilot - 1, out Vector3 feet)
                                                  : Try(world, body, rest, from, to, kind, ways[w], out feet);
                    float after = grid.TimeOf(grid.FloorAt(feet));
                    if (after == float.MaxValue)
                        after = before + 5f;
                    gain[w, k] += before - after;
                    spent[w, k] += (ticks + ways[w].Wait) / 40.0;
                    up[w, k] += before - after > 0.3f ? 1 : 0;
                    binGain[w, k, bin] += before - after;
                    binSpent[w, k, bin] += (ticks + ways[w].Wait) / 40.0;
                }
            }

            for (int k = 0; k < 2; k++)
            {
                Log($"[labor] {(k == 0 ? "Sprung" : "Doppelsprung")}: {total[k]} Links");
                for (int w = 0; w < ways.Count; w++)
                    Log($"[labor]   {ways[w].Name}: {up[w, k] * 100 / Math.Max(1, total[k])} % höher, " +
                        $"gewinnt {gain[w, k] / Math.Max(1, total[k]):F2} s in {spent[w, k] / Math.Max(1, total[k]):F2} s, " +
                        $"also {gain[w, k] / Math.Max(1e-6, spent[w, k]):F2} s je s; nach Weite " +
                        string.Join(" ", System.Linq.Enumerable.Range(0, 5).Where(b => binN[k, b] > 0)
                            .Select(b => $"{b * 0.5f:F1}+:{binGain[w, k, b] / binSpent[w, k, b]:F2}")));
            }
            return 0;
        }

        // One hop by the pilot, which sees the motor delay ticks late and
        // carries that forward with its own keys, as the bots do.
        static int Fly(HeadlessWorld world, ScriptedBody body, MotorState rest, JumpPilot pilot, Vector3 from, Vector3 to,
                       int delay, out Vector3 feet)
        {
            feet = from;
            if (!pilot.Prepare(from, to, out JumpPilot.Hop hop))
                return 1;
            float yaw = UMath.Atan2(-hop.DX, -hop.DZ) * (180f / MathF.PI);
            MotorState s = rest;
            s.Position = new Vector3(from.X, from.Y + 0.9f + 0.05f, from.Z);
            s.Velocity = Vector3.Zero;
            s.Rotation = Quaternion.RotationY(yaw * (MathF.PI / 180f));
            body.Motor.RestoreState(s);
            body.Motor.Teleport(s.Position);

            var seen = new MotorState[128];
            var keys = new MoveInput[128];
            var attempt = new JumpPilot.Attempt();
            int t = 0, jumped = -1;
            for (; t < 120; t++)
            {
                seen[t] = body.Motor.CaptureState();
                int from_ = Math.Max(0, t - delay);
                JumpPilot.Body b = pilot.From(seen[from_], 1.8f);
                for (int k = from_; k < t; k++)
                    pilot.Step(ref b, keys[k]);
                if (attempt.Flying && jumped >= 0 && t - jumped > 3 && b.Grounded)
                    break;
                MoveInput input = pilot.Drive(hop, b, yaw, ref attempt);
                if (input.JumpPresses == 1 && jumped < 0)
                    jumped = t;
                keys[t] = input;
                body.Input = input;
                world.Simulation.Tick();
            }
            // Until the real body stands too.
            for (int k = 0; k < 40 && !body.Motor.CaptureState().Grounded; k++, t++)
            {
                body.Input = new MoveInput { Yaw = yaw };
                world.Simulation.Tick();
            }
            feet = new Vector3(body.Motor.Position.X, body.Motor.Position.Y - 0.9f, body.Motor.Position.Z);
            return t + 1;
        }

        // One try: the body on the take-off floor (or behind it), facing the
        // landing, at the way's speed; jumps once it reaches the take-off,
        // presses again for a double jump. Returns the ticks until it stood
        // again and where.
        static int Try(HeadlessWorld world, ScriptedBody body, MotorState rest, Vector3 from, Vector3 to, LinkKind kind,
                       Way way, out Vector3 feet)
        {
            Vector3 dir = new Vector3(to.X - from.X, 0f, to.Z - from.Z);
            dir = UMath.SqrMagnitude(dir) < 1e-6f ? new Vector3(0f, 0f, -1f) : UMath.Normalized(dir);
            float yaw = UMath.Atan2(-dir.X, -dir.Z) * (180f / MathF.PI);
            Vector3 start = UMath.Sub(from, UMath.Mul(dir, way.Back));
            MotorState s = rest;
            s.Position = new Vector3(start.X, from.Y + 0.9f + 0.05f, start.Z);
            s.Velocity = UMath.Mul(dir, way.Speed);
            s.Rotation = Quaternion.RotationY(yaw * (MathF.PI / 180f));
            body.Motor.RestoreState(s);
            body.Motor.Teleport(s.Position);

            int jumped = -1, t = 0;
            for (; t < 80; t++)
            {
                Vector3 p = body.Motor.Position;
                float along = (p.X - from.X) * dir.X + (p.Z - from.Z) * dir.Z;
                bool forward = jumped < 0 || t - jumped >= way.Hold;
                var input = new MoveInput { Yaw = yaw, Move = new Vector2(0f, forward ? 1f : 0f), Sprint = way.Sprint };
                if (jumped < 0 && along >= -0.05f)
                {
                    input.JumpPresses = 1;
                    jumped = t;
                }
                if (jumped >= 0 && t - jumped < 8)
                    input.JumpHeld = true;
                if ((kind == LinkKind.DoubleJump || way.Always) && jumped >= 0 && t - jumped == way.Double && t > jumped)
                    input.JumpPresses = 2;
                body.Input = input;
                world.Simulation.Tick();
                MotorState m = body.Motor.CaptureState();
                if (jumped >= 0 && t - jumped > 6 && m.Grounded)
                    break;
            }
            feet = new Vector3(body.Motor.Position.X, body.Motor.Position.Y - 0.9f, body.Motor.Position.Z);
            return t + 1;
        }
    }
}
