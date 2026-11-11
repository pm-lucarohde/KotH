using System;
using KotH.Probes.Harness;
using KotH.Shared.Hosting;
using KotH.Shared.Player;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    /// <summary>Stands the body still with its axis a set distance past a block's edge: up to
    /// EdgeReach it stays on top, further out it slips off (Luca, 11.11.2026: one stood with most of
    /// the body over the void). Also walks along the edge, jumps off it and creeps crouched into it.</summary>
    public sealed class EdgeSlipProbe : Probe
    {
        protected override string Tag => "edgeslip";

        // Unity coordinates: a 4 m block high above the arena, top at y 50, edge at x 102.
        static readonly Vector3 BlockAt = new Vector3(100f, 49.5f, 100f);
        const float Top = 50f, Edge = 102f;

        public bool Measure { get; init; }

        public override int Run(HeadlessWorld world, int maxTicks)
        {
            PlayerMotor motor = Prototype.Open(world).Motor;
            Arena.AddBox(world, "EdgeSlipBlock", BlockAt, new Vector3(4f, 1f, 4f));
            int failures = 0;

            if (Measure)
            {
                Settle(motor);
                float bottom = Ref.ToUnity(motor.Position).Y - 0.5f * motor.Height;
                Log($"[edgeslip] standing in the middle: capsule bottom {bottom - Top:F4} m above the top");
                foreach (float yaw in new[] { 90f, 60f, 30f })
                {
                    Settle(motor);
                    motor.Teleport(Ref.Pos(new Vector3(Edge - 1f, Top + 1f, 100f)));
                    for (int t = 0; t < 160; t++)
                        motor.Tick(new MoveInput { Move = new Vector2(0f, 1f), Yaw = Ref.Angle(yaw), Crouch = true }, FixedDeltaTime);
                    Vector3 at = Ref.ToUnity(motor.Position);
                    Log($"[edgeslip] crouched walk at {yaw}°: axis {at.X - Edge:F3} m past, y {at.Y - Top:F3}");
                }
                for (float d = 0f; d <= 0.401f; d += 0.02f)
                {
                    (bool kept, float angle, float x) = Stand(motor, d, 0f);
                    Log($"[edgeslip] axis {d:F2} m past: contact {angle:F1}°, after 3 s x {x - Edge:F3} past, " +
                        (kept ? "on top" : "fell"));
                }
                return 0;
            }

            // Stands: inside the reach stays, beyond it slips.
            foreach ((float d, bool stays) in new[] { (0.05f, true), (0.15f, true), (0.19f, true), (0.23f, false), (0.3f, false), (0.36f, false) })
            {
                (bool kept, float angle, float x) = Stand(motor, d, 0f);
                bool ok = kept == stays;
                failures += ok ? 0 : 1;
                Log($"[edgeslip] stand {d:F2} m past (contact {angle:F1}°): " + (kept ? "stays" : "slips off") +
                    $", x {x - Edge:F3} -> " + (ok ? "ok" : "DEVIATION"));
            }

            // Along the edge at 0.15 m: stays on top and does not drift out while on the block.
            {
                Settle(motor);
                motor.Teleport(Ref.Pos(new Vector3(Edge + 0.15f, Top + 1f, 98.5f)));
                Idle(motor, 40);
                bool kept = true;
                for (int t = 0; t < 40; t++)
                {
                    motor.Tick(new MoveInput { Move = new Vector2(0f, 1f), Yaw = Ref.Angle(0f) }, FixedDeltaTime);
                    Vector3 at = Ref.ToUnity(motor.Position);
                    if (at.Z < BlockAt.Z + 1.5f)  // the block ends at z 102
                        kept &= at.Y > Top && at.X < Edge + 0.2f;
                }
                failures += kept ? 0 : 1;
                Log($"[edgeslip] walk along the edge: " + (kept ? "on top" : "FELL") + $", end {Ref.V(motor.Position)}");
            }

            // A jump at 0.19 m past (a running jump off the edge stays possible).
            {
                Settle(motor);
                motor.Teleport(Ref.Pos(new Vector3(Edge + 0.19f, Top + 1f, 100f)));
                Idle(motor, 20);
                float before = Ref.ToUnity(motor.Position).Y;
                motor.Tick(new MoveInput { JumpPresses = 1, JumpHeld = true, Yaw = Ref.Angle(90f) }, FixedDeltaTime);
                for (int t = 0; t < 8; t++)
                    motor.Tick(new MoveInput { JumpHeld = true, Yaw = Ref.Angle(90f) }, FixedDeltaTime);
                bool rose = Ref.ToUnity(motor.Position).Y > before + 0.3f;
                failures += rose ? 0 : 1;
                Log($"[edgeslip] jump at the edge: " + (rose ? "jumped" : "NO JUMP"));
            }

            // Crouched towards the edge: the guard stops the axis just past it.
            {
                Settle(motor);
                motor.Teleport(Ref.Pos(new Vector3(Edge - 1f, Top + 1f, 100f)));
                for (int t = 0; t < 160; t++)
                    motor.Tick(new MoveInput { Move = new Vector2(0f, 1f), Yaw = Ref.Angle(70f), Crouch = true }, FixedDeltaTime);
                Vector3 at = Ref.ToUnity(motor.Position);
                bool held = at.Y > Top && at.X - Edge < 0.1f;
                failures += held ? 0 : 1;
                Log($"[edgeslip] crouched into the edge: axis {at.X - Edge:F3} m past -> " + (held ? "ok" : "DEVIATION"));
            }

            Log(failures == 0 ? "[edgeslip] EDGES AS EXPECTED" : $"[edgeslip] {failures} DEVIATIONS");
            return failures == 0 ? 0 : 1;
        }

        // Stands still d past the edge for 3 s; the contact angle after the landing.
        (bool kept, float angle, float x) Stand(PlayerMotor motor, float d, float yaw)
        {
            Settle(motor);
            motor.Teleport(Ref.Pos(new Vector3(Edge + d, Top + 1f, 100f)));
            float angle = 0f;
            for (int t = 0; t < 120; t++)
            {
                motor.Tick(new MoveInput { Yaw = Ref.Angle(yaw) }, FixedDeltaTime);
                if (t == 20)
                    angle = motor.GroundAngle;
            }
            Vector3 at = Ref.ToUnity(motor.Position);
            return (at.Y > Top, angle, at.X);
        }

        // 60 idle ticks in the block's middle: no case inherits the last one's speed.
        static void Settle(PlayerMotor motor)
        {
            motor.Teleport(Ref.Pos(new Vector3(BlockAt.X, Top + 1f, BlockAt.Z)));
            Idle(motor, 60);
        }

        static void Idle(PlayerMotor motor, int ticks)
        {
            for (int i = 0; i < ticks; i++)
                motor.Tick(new MoveInput(), FixedDeltaTime);
        }
    }
}
