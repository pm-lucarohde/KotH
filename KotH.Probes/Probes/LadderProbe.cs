using System;
using System.Collections.Generic;
using KotH.Probes.Harness;
using KotH.Shared.Hosting;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Probes
{
    /// <summary>The Ladder's climbing and bounce pads on Test: strips built in code on the Plateau's
    /// face (z -10, towards +z, top 5 m) and a pad block. Catching, climbing speed, the top, jumping,
    /// stepping and being knocked off, bouncing, crouching on a pad and a rollback mid-climb.</summary>
    public sealed class LadderProbe : Probe
    {
        protected override string Tag => "ladder";

        // Stride metres. Yaw 0 looks along -z, at the wall, between the two ramps leaning on it. The
        // strip reaches the Plateau's top or, short, stops under it; the pad's top is 0.5 m high.
        const float Wall = -10f, Top = 5f, ShortTop = 3f;
        static readonly Vector3 Foot = new Vector3(0f, 0f, Wall);
        static readonly Vector3 PadTop = new Vector3(8f, 0.5f, -3f);
        const int Idle = 60;

        int failures;

        /// <summary>Climbs every ladder of The Ladder instead (ladder map).</summary>
        public bool OnMap { get; init; }

        /// <summary>Tries the change round a corner between neighbouring ladders instead (ladder corners).</summary>
        public bool Corners { get; init; }

        public override int Run(HeadlessWorld world, int maxTicks)
        {
            if (Corners)
                return CornerMap(world);
            if (OnMap)
                return ClimbMap(world);
            PlayerMotor motor = Prototype.Open(world).Motor;
            world.Queries.Ladders.Add(Strip(Top));
            Arena.AddBox(world, "LadderPad", Ref.ToUnity(new Vector3(PadTop.X, 0.25f, PadTop.Z)), new Vector3(1f, 0.5f, 1f));
            world.Queries.Pads.Add(new PadArea(PadTop, 0.5f, 0.5f, 0.9f));

            var up = new MoveInput { Move = new Vector2(0f, 1f) };
            float feet0 = 0f;

            // Catch: walking into the wall takes hold; climbing then rises at ClimbSpeed.
            Stand(motor, Foot.X, Wall + 1.5f);
            int caught = Ticks(motor, up, 60, m => m.IsClimbing);
            Check(caught >= 0, $"walking into the wall catches the ladder (tick {caught})");
            for (int t = 0; t < 4; t++)
                motor.Tick(up, FixedDeltaTime);
            feet0 = Feet(motor);
            for (int t = 0; t < 20; t++)
                motor.Tick(up, FixedDeltaTime);
            float speed = (Feet(motor) - feet0) / (20 * FixedDeltaTime);
            Check(MathF.Abs(speed - motor.ClimbSpeed) < 0.01f, $"climbs at {speed:F3} m/s (ClimbSpeed {motor.ClimbSpeed})");

            // Rollback mid-climb: the same inputs after a restore give the same bits.
            MotorState mid = motor.CaptureState();
            var turn = new MoveInput { Move = new Vector2(0.6f, 0.8f), Yaw = 10f };
            Vector3 first = Run(motor, up, turn);
            motor.RestoreState(mid);
            Vector3 again = Run(motor, up, turn);
            Check(first == again, $"rollback mid-climb repeats bit for bit ({first} / {again})");

            // Top: climbing on steps onto the Plateau and walks off the ladder.
            motor.RestoreState(mid);
            int over = Ticks(motor, up, 200, m => !m.IsClimbing && m.IsGrounded && Feet(m) > Top - 0.01f);
            Check(over >= 0 && motor.Position.Z < Wall, $"the top steps onto the Plateau (tick {over}, " +
                  $"feet {Feet(motor):F3}, z {motor.Position.Z:F3})");

            // Under the wall: the short strip holds the feet at its top, still on the ladder.
            world.Queries.Ladders[0] = Strip(ShortTop);
            Stand(motor, Foot.X, Wall + 1.5f);
            Ticks(motor, up, 200, m => false);
            Check(motor.IsClimbing && MathF.Abs(Feet(motor) - ShortTop - 0.05f) < 0.01f,
                  $"under the wall it holds at the top (feet {Feet(motor):F3}, climbing {motor.IsClimbing})");

            // No key slides down slowly, crouching holds.
            feet0 = Feet(motor);
            Ticks(motor, default, 20, m => false);
            float slide = (feet0 - Feet(motor)) / (20 * FixedDeltaTime);
            Check(motor.IsClimbing && MathF.Abs(slide - motor.ClimbSlideSpeed) < 0.01f, $"no key slides down at {slide:F3} m/s");
            feet0 = Feet(motor);
            Ticks(motor, new MoveInput { Crouch = true }, 20, m => false);
            Check(motor.IsClimbing && MathF.Abs(Feet(motor) - feet0) < 0.001f, $"crouching holds ({Feet(motor) - feet0:F4} m)");

            // A ledge 0.4 m over the top, as a half slab over the last block: stepped onto.
            world.Queries.Ladders[0] = Strip(Top - 0.4f);
            Stand(motor, Foot.X, Wall + 1.5f);
            over = Ticks(motor, up, 200, m => !m.IsClimbing && m.IsGrounded && Feet(m) > Top - 0.01f);
            Check(over >= 0, $"a ledge 0.4 m over the top is stepped onto (tick {over}, feet {Feet(motor):F3})");
            world.Queries.Ladders[0] = Strip(Top);

            // Jump off: away from the wall, as high as a ground jump, and no catch at once.
            ClimbTo(motor, Foot.X, 2f);
            motor.Tick(new MoveInput { JumpPresses = 1, JumpHeld = true }, FixedDeltaTime);
            Check(!motor.IsClimbing && motor.Velocity.Z > 2.9f && motor.Velocity.Y > 5f,
                  $"jump leaps off (v {motor.Velocity.Z:F2} out, {motor.Velocity.Y:F2} up)");

            // Side exit: strafing past the strip's edge lets go.
            ClimbTo(motor, Foot.X, 2f);
            int off = Ticks(motor, new MoveInput { Move = new Vector2(1f, 0.6f) }, 40, m => !m.IsClimbing);
            Check(off >= 0, $"strafing past the edge lets go (tick {off}, x {motor.Position.X - Foot.X:F3})");

            // Knocks: a graze keeps the climber on, a real hit throws it off.
            ClimbTo(motor, Foot.X, 2f);
            motor.Knock(new Vector3(0f, 0f, 1f), 0.3f, 0.2f, 0f, 0f, FixedDeltaTime);
            motor.Tick(up, FixedDeltaTime);
            Check(motor.IsClimbing, "a 0.3 m graze leaves the climber on");
            motor.Knock(new Vector3(0f, 0f, 1f), 3f, 1f, 0f, 0f, FixedDeltaTime);
            int stays = Ticks(motor, up, 12, m => m.IsClimbing);
            Check(stays < 0, $"a 3 m knock throws it off for good (caught again at tick {stays})");

            // Pad: a 4 m fall comes back up by the pad's share; crouched it lands.
            float rise = Drop(motor, false);
            Check(rise > 0.75f * 4f && rise < 0.85f * 4f, $"a 4 m fall bounces {rise:F3} m back up (0.9² of it, less Euler)");
            rise = Drop(motor, true);
            Check(rise < 0.1f, $"crouched it lands (feet at most {rise:F3} m over the top, resting)");
            return failures;
        }

        static LadderStrip Strip(float top)
            => new LadderStrip(Foot, new Vector3(0f, 0f, 1f), new Vector3(-1f, 0f, 0f), 0.5f, top);

        /// <summary>From each strip's foot, held towards its wall for 15 s: it ends standing on top, or
        /// hanging at the top under a wall (a gap of the source); anything else is a deviation.</summary>
        int ClimbMap(HeadlessWorld world)
        {
            world.LoadArena(Maps.TheLadder);
            Entity body = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, new Vector3(0f, 5f, 13f), Quaternion.Identity);
            PlayerMotor motor = SimulationBody.Wire(body, world.Queries);
            int hanging = 0;
            for (int i = 0; i < world.Queries.Ladders.Count; i++)
            {
                LadderStrip s = world.Queries.Ladders[i];
                Vector3 at = s.Foot + s.Out * (motor.BodyRadius + 0.05f);
                motor.Teleport(new Vector3(at.X, s.Foot.Y + 0.95f, at.Z));
                var input = new MoveInput { Move = new Vector2(0f, 1f), Yaw = MathF.Atan2(s.Out.X, s.Out.Z) * 180f / MathF.PI };
                float peak = float.MinValue;
                bool caught = false;
                int held = 0;
                // Until it lets go (then it stands still a moment) or hangs at the top for half a second.
                for (int t = 0; t < 600 && held < 20; t++)
                {
                    motor.Tick(input, FixedDeltaTime);
                    caught |= motor.IsClimbing;
                    peak = MathF.Max(peak, Feet(motor));
                    held = motor.IsClimbing && Feet(motor) >= s.Top ? held + 1 : 0;
                    if (caught && !motor.IsClimbing)
                    {
                        for (int k = 0; k < 10; k++)
                            motor.Tick(default, FixedDeltaTime);
                        break;
                    }
                }
                float feet = Feet(motor);
                bool onTop = !motor.IsClimbing && motor.IsGrounded && feet >= s.Top - 0.05f;
                bool hangs = held >= 20;
                hanging += hangs ? 1 : 0;
                string what = onTop ? "on top" : hangs ? "hangs at the top" : $"stopped (climbing {motor.IsClimbing}, peak {peak - s.Foot.Y:F2} m)";
                bool ok = caught && (onTop || hangs);
                failures += ok ? 0 : 1;
                Log($"[ladder] {i,2} at ({s.Foot.X:F1}, {s.Foot.Z:F1}) out ({s.Out.X:F0}, {s.Out.Z:F0}), {s.Foot.Y:F1}-{s.Top:F1} m: " +
                    $"{what}, feet {feet:F2}" + (ok ? "" : " -> DEVIATION"));
            }
            Log($"[ladder] {world.Queries.Ladders.Count} ladders, {hanging} hang under a wall, {failures} deviations");
            return failures;
        }

        /// <summary>Experiment: every pair of ladders meeting at a corner of a pillar, a climber on the first
        /// at their shared height strafes out past the corner, then pushes in round it towards the second's
        /// wall; which key mixes catch the second, and how much height it costs.</summary>
        int CornerMap(HeadlessWorld world)
        {
            world.LoadArena(Maps.TheLadder);
            Entity body = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, new Vector3(0f, 5f, 13f), Quaternion.Identity);
            PlayerMotor motor = SimulationBody.Wire(body, world.Queries);
            var ladders = world.Queries.Ladders;
            int pairs = 0, caught = 0;
            for (int a = 0; a < ladders.Count; a++)
                for (int b = 0; b < ladders.Count; b++)
                {
                    LadderStrip A = ladders[a], B = ladders[b];
                    if (a == b || MathF.Abs(Vector3.Dot(A.Out, B.Out)) > 0.1f)
                        continue;
                    float low = MathF.Max(A.Foot.Y, B.Foot.Y), high = MathF.Min(A.Top, B.Top);
                    if (high - low < 0.5f)
                        continue;
                    float s = 0f;
                    foreach (float side in new[] { -1f, 1f })
                        foreach (float other in new[] { -1f, 1f })
                        {
                            Vector3 ea = A.Foot + A.Along * (A.HalfWidth * side), eb = B.Foot + B.Along * (B.HalfWidth * other);
                            if (MathF.Abs(ea.X - eb.X) < 0.1f && MathF.Abs(ea.Z - eb.Z) < 0.1f)
                                s = side;
                        }
                    if (s == 0f)
                        continue;
                    pairs++;
                    float at = MathF.Max(low + 0.5f, high - 1.5f);
                    var results = new List<string>();
                    foreach (int outTicks in new[] { 12, 14, 16, 18, 20 })
                        foreach (float push in new[] { 0.7f })
                        {
                            Vector3 start = A.Foot + A.Out * (motor.BodyRadius + 0.05f);
                            motor.Teleport(new Vector3(start.X, at + 0.9f, start.Z));
                            float faceA = MathF.Atan2(A.Out.X, A.Out.Z) * 180f / MathF.PI;
                            Ticks(motor, new MoveInput { Move = new Vector2(0f, 1f), Yaw = faceA }, 40, m => m.IsClimbing);
                            Ticks(motor, new MoveInput { Crouch = true, Yaw = faceA }, 10, m => false);
                            float from = Feet(motor);
                            // Out past the corner along A, then in round it: towards B's wall and A's plane.
                            Vector3 outward = A.Along * s;
                            Ticks(motor, Keys(outward, faceA), outTicks, m => false);
                            Vector3 wish = Vector3.Normalize(-B.Out * push + -A.Out * MathF.Sqrt(1f - push * push));
                            float faceB = MathF.Atan2(B.Out.X, B.Out.Z) * 180f / MathF.PI;
                            int t = Ticks(motor, Keys(wish, faceB), 40, m => m.IsClimbing && m.LadderOut == B.Out);
                            bool ok = t >= 0;
                            results.Add(ok ? $"{outTicks}/{push:F2}: {from - Feet(motor):F2} m in {t} t" : $"{outTicks}/{push:F2}: -");
                        }
                    bool any = results.Exists(r => !r.EndsWith("-"));
                    caught += any ? 1 : 0;
                    Log($"[corner] {a,2} -> {b,2} at {at:F1}: {string.Join(", ", results)}");
                }
            Log($"[corner] {pairs} pairs, {caught} changed in some way");
            return pairs == caught ? 0 : 1;
        }

        // Keys that move along a world direction with the view at yaw (forward -Z turned by yaw).
        static MoveInput Keys(Vector3 wish, float yaw)
        {
            float r = yaw * MathF.PI / 180f;
            var forward = new Vector3(-MathF.Sin(r), 0f, -MathF.Cos(r));
            var right = new Vector3(MathF.Cos(r), 0f, -MathF.Sin(r));
            return new MoveInput { Move = new Vector2(Vector3.Dot(wish, right), Vector3.Dot(wish, forward)), Yaw = yaw };
        }

        void Check(bool ok, string what)
        {
            Log($"[ladder] {what} -> " + (ok ? "ok" : "DEVIATION"));
            failures += ok ? 0 : 1;
        }

        // The motor's own feet: half the standing height under the axis, crouched too.
        static float Feet(PlayerMotor motor) => motor.Position.Y - 0.9f;

        /// <summary>Ticks until done holds; returns the tick it did, or -1.</summary>
        static int Ticks(PlayerMotor motor, MoveInput input, int count, Func<PlayerMotor, bool> done)
        {
            for (int t = 0; t < count; t++)
            {
                motor.Tick(input, FixedDeltaTime);
                if (done(motor))
                    return t;
            }
            return -1;
        }

        /// <summary>Stands still at (x, z) on the ground, after the idle ticks every case starts with.</summary>
        static void Stand(PlayerMotor motor, float x, float z)
        {
            motor.Teleport(new Vector3(x, 0.9f, z));
            for (int t = 0; t < Idle; t++)
                motor.Tick(default, FixedDeltaTime);
        }

        /// <summary>From the ground in front of the strip at x, climbs until the feet are at height.</summary>
        static void ClimbTo(PlayerMotor motor, float x, float height)
        {
            Stand(motor, x, Wall + 1.5f);
            Ticks(motor, new MoveInput { Move = new Vector2(0f, 1f) }, 200, m => m.IsClimbing && Feet(m) >= height);
        }

        static Vector3 Run(PlayerMotor motor, MoveInput a, MoveInput b)
        {
            for (int t = 0; t < 30; t++)
                motor.Tick(t % 3 == 0 ? b : a, FixedDeltaTime);
            return motor.Position;
        }

        /// <summary>Drops the body from 4 m over the pad's top; returns how high its feet rise again.</summary>
        float Drop(PlayerMotor motor, bool crouch)
        {
            Stand(motor, PadTop.X + 2f, PadTop.Z);
            motor.Teleport(new Vector3(PadTop.X, PadTop.Y + 4f + 0.9f, PadTop.Z));
            var input = new MoveInput { Crouch = crouch };
            bool landed = false;
            float peak = float.MinValue;
            for (int t = 0; t < 160; t++)
            {
                motor.Tick(input, FixedDeltaTime);
                if (!landed && Feet(motor) < PadTop.Y + 0.2f && (motor.IsGrounded || motor.Velocity.Y >= 0f))
                    landed = true;
                if (landed)
                    peak = MathF.Max(peak, Feet(motor));
            }
            return landed ? peak - PadTop.Y : float.NaN;
        }
    }
}
