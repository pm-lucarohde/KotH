using System;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;

namespace KotH.Server
{
    /// <summary>
    /// A load-test body: roams the arena between waypoints and sprints,
    /// jumps, double jumps, slides and swings at random, so a benchmark
    /// runs every motor path instead of standing bodies. The randomness is
    /// a seeded xorshift and reads no clock, so every run ticks the same.
    /// </summary>
    [DataContract]
    public sealed class BenchBot : SimulationBody
    {
        // Stride coordinates: the plateau, the ramp feet, the stair flights
        // (they climb towards -z) and open ground on both sides.
        static readonly Vector2[] Waypoints =
        {
            new Vector2(0f, -16f), new Vector2(8f, -12f), new Vector2(-8f, -12f),
            new Vector2(3.5f, -2f), new Vector2(-3.5f, 6f),
            new Vector2(-6f, 6f), new Vector2(-10f, 6f), new Vector2(-14f, 6f),
            new Vector2(-6f, 13f), new Vector2(-10f, 13f), new Vector2(-14f, 13f),
            new Vector2(15f, 15f), new Vector2(-18f, -5f), new Vector2(18f, -18f),
            new Vector2(12f, 4f), new Vector2(0f, 20f),
        };

        readonly Vector3 spawn;
        uint rng;
        float yaw;
        int target;
        int ticksToRetarget;
        int jumpHold;
        int crouchHold;

        public BenchBot(int seed, Vector3 spawn, float startYaw)
        {
            this.spawn = spawn;
            rng = (uint)(seed * 2654435761u) | 1u;
            yaw = startYaw;
            target = (int)(Next() % (uint)Waypoints.Length);
        }

        /// <summary>Where the bot is heading, for the end-of-run summary.</summary>
        public Vector2 Target => Waypoints[target];

        /// <summary>How often the bot fell out of the arena and was put back.</summary>
        public int Respawns { get; private set; }

        /// <summary>Where each fall crossed ground level, for diagnosing holes.</summary>
        public readonly System.Collections.Generic.List<Vector3> Falls = new System.Collections.Generic.List<Vector3>();
        bool below;

        public override MoveInput ReadInput()
        {
            if (Motor.Position.Y < -0.5f && !below)
                Falls.Add(Motor.Position);
            below = Motor.Position.Y < -0.5f;

            // A body that left the arena would fall forever at almost no
            // cost and quietly lighten the load; put it back like a game would.
            if (Motor.Position.Y < -5f)
            {
                Motor.Teleport(spawn);
                Respawns++;
            }

            Vector3 at = Motor.Position;
            Vector2 goal = Waypoints[target];
            float dx = goal.X - at.X, dz = goal.Y - at.Z;

            if (--ticksToRetarget <= 0 || dx * dx + dz * dz < 4f)
            {
                target = (int)(Next() % (uint)Waypoints.Length);
                ticksToRetarget = 200 + (int)(Next() % 400u);
            }

            // Stride yaw turns left; forward at yaw y is (-sin y, 0, -cos y).
            float wanted = MathF.Atan2(-dx, -dz) * (180f / MathF.PI);
            float turn = MathF.IEEERemainder(wanted - yaw, 360f);
            yaw += Math.Clamp(turn, -12f, 12f) + (Chance(20) ? Signed(6f) : 0f);

            var input = new MoveInput
            {
                Move = new Vector2(Chance(10) ? Signed(1f) : 0f, 1f),
                Yaw = yaw,
                Sprint = !Chance(30),
            };

            if (jumpHold > 0)
            {
                jumpHold--;
                input.JumpHeld = true;
            }
            else if (Chance(3))
            {
                input.JumpPresses = 1;
                input.JumpHeld = true;
                jumpHold = (int)(Next() % 8u);
            }

            if (crouchHold > 0)
            {
                crouchHold--;
                input.Crouch = true;
            }
            else if (Chance(2))
            {
                input.CrouchPressed = true;
                input.Crouch = true;
                crouchHold = 4 + (int)(Next() % 20u);
            }

            if (Chance(5))
                input.AttackPresses = 1;

            return input;
        }

        uint Next()
        {
            rng ^= rng << 13;
            rng ^= rng >> 17;
            rng ^= rng << 5;
            return rng;
        }

        bool Chance(int percent) => Next() % 100u < (uint)percent;

        float Signed(float range) => ((Next() & 0xFFFF) / 32767.5f - 1f) * range;
    }
}
