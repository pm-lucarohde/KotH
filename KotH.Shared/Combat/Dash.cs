using System.Collections.Generic;
using KotH.Shared.Match;
using KotH.Shared.Mathematics;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.Shared.Combat
{
    /// <summary>The dash down: crouch in the air drops the body straight to the ground,
    /// which knocks everyone around the landing away. One charge, which comes back with
    /// points in the zone like the blaster's shots. Driven like Blaster from SimulationBody.</summary>
    [DataContract("KotH.Dash")]
    [Display("Dash")]
    [ComponentCategory("KotH")]
    public sealed class Dash : EntityComponent
    {
        /// <userdoc>How fast the dash falls, in metres per second.</userdoc>
        [Display("Speed", "Dash")] public float SlamSpeed = 15f;

        /// <userdoc>The ground must be at least this far below the feet, in metres; lower, crouch does what it always does.</userdoc>
        [Display("Min height (m)", "Dash")] public float MinHeight = 1.5f;

        /// <userdoc>Bodies this close to the landing are hit, in metres to their nearest point.</userdoc>
        [Display("Radius (m)", "Hit")] public float Radius = 3.5f;

        /// <userdoc>How many percent a hit adds to the target.</userdoc>
        [Display("Damage percent", "Hit")] public float DamagePercent = 1.2f;

        /// <userdoc>How far a target at 100 % flies away from the landing, in metres (200 % of the cane's).</userdoc>
        [Display("Distance at 100 %", "Knockback")] public float KnockbackDistance = 24f;

        /// <userdoc>How high a target at 100 % rises, in metres.</userdoc>
        [Display("Height at 100 %", "Knockback")] public float KnockbackHeight = 2.4f;

        /// <userdoc>Below this a target rises as high as at this percent, even at 0 %.</userdoc>
        [Display("Min height as at %", "Knockback")] public float KnockbackHeightFloor = 1f;

        /// <userdoc>Most dashes held, also how many a round starts with.</userdoc>
        [Display("Dashes", "Charge")] public int MaxDashes = 1;

        /// <userdoc>Points scored in the zone that bring one dash back. While full nothing is saved up.</userdoc>
        [Display("Points per dash", "Charge")] public int PointsPerDash = 10;

        // The ground below, bodies included: a dash onto a head is a dash.
        const Stride.BepuPhysics.CollisionMask GroundMask = WorldQueries.WorldMask | WorldQueries.HittableMask;

        PlayerMotor motor;
        DashState state;

        /// <summary>The part a rollback restores.</summary>
        [DataMemberIgnore] public DashState State => state;

        /// <summary>Tick and place of the last landing, for the looks only, like Blaster.LastImpactTick.</summary>
        [DataMemberIgnore] public int LastSlamTick { get; private set; } = int.MinValue;
        [DataMemberIgnore] public Vector3 LastSlamPoint { get; private set; }

        /// <summary>Dashes started and bodies hit so far. Diagnostics for the probes.</summary>
        [DataMemberIgnore] public int SlamCount { get; private set; }
        [DataMemberIgnore] public int HitCount { get; private set; }

        public void Initialize(PlayerMotor owner)
        {
            motor = owner;
            state = new DashState { Charges = MaxDashes };
        }

        public void Restore(in DashState from) => state = from;

        /// <summary>One combat step, before the blaster's: earn, strike if the motor landed
        /// a dash in this tick, start one on a crouch press high enough in the air. The
        /// motor falls from the next tick on.</summary>
        public void Tick(in MoveInput input, int scoreTicks, IReadOnlyList<SimulationBody> bodies, int tick, float dt)
        {
            Earn(scoreTicks);

            if (motor.SlamLanded)
                Strike(bodies, tick, dt);

            if (!input.CrouchPressed || motor.IsGrounded || motor.IsTouchingGround || motor.IsSlamming ||
                state.Charges <= 0 || !HighEnough())
                return;

            state.Charges--;
            SlamCount++;
            motor.StartSlam(SlamSpeed);
        }

        // Grows only while not full, as the blaster's shots.
        void Earn(int scoreTicks)
        {
            int earned = scoreTicks - state.ScoreSeen;
            state.ScoreSeen = scoreTicks;
            if (earned <= 0 || state.Charges >= MaxDashes)
                return;

            state.Progress += earned;
            int per = PointsPerDash * HillMatch.TicksPerPoint;
            while (state.Progress >= per && state.Charges < MaxDashes)
            {
                state.Progress -= per;
                state.Charges++;
            }
            if (state.Charges >= MaxDashes)
                state.Progress = 0;
        }

        /// <summary>The feet are at least MinHeight above whatever lies below.</summary>
        bool HighEnough()
        {
            float half = motor.StandHeight * 0.5f;
            return !motor.World.Raycast(motor.Position, UMath.Down, half + MinHeight, GroundMask, out _, motor.Hull);
        }

        /// <summary>Feet of the body as the motor stands it, crouched or not.</summary>
        static Vector3 Feet(PlayerMotor m) => new Vector3(m.Position.X, m.Position.Y - m.StandHeight * 0.5f, m.Position.Z);

        // Every other body within the radius of the landing and in sight of it, in
        // the bodies' fixed order: the cane's hit, away from the landing, no carry.
        void Strike(IReadOnlyList<SimulationBody> bodies, int tick, float dt)
        {
            Vector3 at = Feet(motor);
            LastSlamTick = tick;
            LastSlamPoint = at;
            Vector3 eye = UMath.Add(at, UMath.Mul(UMath.Up, 0.5f));

            for (int i = 0; i < bodies.Count; i++)
            {
                SimulationBody body = bodies[i];
                PlayerMotor victim = body.Motor;
                DamagePercent target = body.Entity.Get<DamagePercent>();
                if (victim == motor || body.IsParked || target == null)
                    continue;

                // The nearest point of the body's axis, ends rounded by its radius.
                float half = UMath.Max(0f, victim.Height * 0.5f - victim.Radius);
                Vector3 centre = victim.BodyCenter;
                float y = UMath.Clamp(at.Y, centre.Y - half, centre.Y + half);
                Vector3 nearest = new Vector3(centre.X, y, centre.Z);
                Vector3 off = UMath.Sub(nearest, at);
                if (UMath.Magnitude(off) - victim.Radius > Radius)
                    continue;

                Vector3 line = UMath.Sub(centre, eye);
                float length = UMath.Magnitude(line);
                if (length > 1e-4f && motor.World.Raycast(eye, UMath.Div(line, length), length,
                                                          WorldQueries.WorldMask, out _, motor.Hull))
                    continue;

                Vector3 away = new Vector3(off.X, 0f, off.Z);
                if (UMath.SqrMagnitude(away) < 1e-6f)
                    away = UMath.Rotate(motor.Rotation, UMath.Forward);

                HitCount++;
                target.LastHitTick = tick;
                target.LastHitAway = away;
                target.LastHitAt = UMath.Sub(nearest, centre);

                float before = target.Percent;
                target.Add(DamagePercent);
                float lift = UMath.Max(before, KnockbackHeightFloor) / 100f;
                victim.Knock(away, KnockbackDistance * (before / 100f), KnockbackHeight * lift, 0f, 0f, dt);
            }
        }

        /// <summary>Clears the counters so a probe can measure one case.</summary>
        public void ResetCounters()
        {
            SlamCount = 0;
            HitCount = 0;
        }
    }
}
