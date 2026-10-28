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
    /// <summary>A swing straight ahead. Driven from outside like PlayerMotor, reads no
    /// input and knows no camera: the aim comes out of the tick's MoveInput, so a
    /// dedicated server computes the same ray.</summary>
    [DataContract("KotH.MeleeAttack")]
    [Display("Melee")]
    [ComponentCategory("KotH")]
    public sealed class MeleeAttack : EntityComponent
    {
        /// <userdoc>Reach from eye height, in metres.</userdoc>
        [Display("Range", "Swing")] public float Range = 3.5f;

        /// <userdoc>How many percent a hit adds to the target.</userdoc>
        [Display("Damage percent", "Swing")] public float DamagePercent = 1.2f;

        /// <userdoc>How far a target at 100 % flies horizontally, in metres, without
        /// steering against it on flat ground. Grows linearly with the percent before the
        /// hit: 0 % does not push horizontally at all, 1000 % ten times as far.</userdoc>
        [Display("Distance at 100 %", "Knockback")] public float KnockbackDistance = 12f;

        /// <userdoc>
        /// How high a target at 100 % rises, in metres. Linear like the distance.
        /// Rising or falling in the air when hit, the push adds to that speed.
        /// </userdoc>
        [Display("Height at 100 %", "Knockback")] public float KnockbackHeight = 1.2f;

        /// <userdoc>
        /// Below this a target rises as high as at this percent, even at 0 %.
        /// Height only: horizontally 0 % still does not push at all.
        /// </userdoc>
        [Display("Min height as at %", "Knockback")] public float KnockbackHeightFloor = 1f;

        /// <userdoc>From this percent before the hit, both speeds along the push line
        /// count, first with "Carry share at start": an attacker running at the target
        /// pushes harder, a target running at him less. Below it they do not
        /// count.</userdoc>
        [Display("Speed counts from %", "Knockback")] public float CarryFromPercent = 10f;

        /// <userdoc>Share of both speeds from "Speed counts from %", 0 to 1.</userdoc>
        [Display("Carry share at start", "Knockback")] public float CarryStartShare = 0.5f;

        /// <userdoc>From this percent both speeds count fully; in between the share rises
        /// linearly. If they cancel, the target flies as if standing; the push never
        /// points towards the attacker.</userdoc>
        [Display("Speed full from %", "Knockback")] public float CarryFullPercent = 20f;

        // Ticks, not seconds: a third of a second is 13.33 ticks at 40 Hz and
        // would silently round up to 14. Counting ticks is also what a server
        // can replay without an argument about rounding.
        /// <userdoc>Ticks between two swings. 13 ticks are 3.08 per second.</userdoc>
        [Display("Cooldown (ticks)", "Swing")] public int CooldownTicks = 13;

        PlayerMotor motor;
        int ticksUntilReady;

        /// <summary>Hits landed so far. Diagnostics for the probe.</summary>
        [DataMemberIgnore] public int HitCount { get; private set; }

        /// <summary>Swings started so far, whether they connected or not.</summary>
        [DataMemberIgnore] public int SwingCount { get; private set; }

        /// <summary>Tick of the last swing, hit or miss: starts the figure's swing. Not
        /// state, like DamagePercent.LastHitTick: a rollback sets it again, and predicted
        /// input never presses.</summary>
        [DataMemberIgnore] public int LastSwingTick { get; set; } = int.MinValue;

        /// <summary>How far the last hit landed from the eye. Diagnostics: the range
        /// limits the distance to the capsule SURFACE, and where that sits depends on how
        /// high the ray crosses the body.</summary>
        [DataMemberIgnore] public float LastHitDistance { get; private set; }

        /// <summary>
        /// Ticks until the next swing may start. The only part of the
        /// attack a rollback has to restore; the counters are diagnostics.
        /// </summary>
        [DataMemberIgnore]
        public int TicksUntilReady
        {
            get => ticksUntilReady;
            set => ticksUntilReady = value;
        }

        public void Initialize(PlayerMotor owner)
        {
            motor = owner;
        }

        /// <summary>
        /// One combat step, called after every body has moved so the swing
        /// sees where they all ended up.
        /// </summary>
        public void Tick(in MoveInput input, float dt)
        {
            if (ticksUntilReady > 0)
                ticksUntilReady--;

            if (input.AttackPresses == 0 || ticksUntilReady > 0)
                return;

            ticksUntilReady = CooldownTicks;
            SwingCount++;
            LastSwingTick = Entity.Get<SimulationBody>()?.Simulation?.CurrentTick ?? 0;

            // Same rotation the camera builds, so the ray goes exactly where
            // the crosshair points - but out of the input, not out of a
            // transform the server would not have.
            Vector3 aim = UMath.Rotate(UMath.Euler(input.Pitch, input.Yaw), UMath.Forward);

            // Hit hulls sit on their own layer, which movement never asks.
            // The ray starts inside this body's own hull, and unlike PhysX
            // Bepu reports that one, so it is skipped by name.
            if (!motor.World.Raycast(motor.EyePosition, aim, Range,
                                     WorldQueries.HittableMask, out QueryHit hit,
                                     motor.Hull))
                return;

            DamagePercent target = FindInParents(hit.Collider.Entity);
            if (target == null)
                return;

            // Counted even at the cap: the ray did connect. Otherwise a
            // probe could not tell a miss apart from a full target.
            HitCount++;
            LastHitDistance = hit.Distance;
            target.LastHitTick = Entity.Get<SimulationBody>()?.Simulation?.CurrentTick ?? 0;
            target.LastHitAway = default;
            target.LastHitAt = default;

            // The percent before this hit decides: a fresh body is not moved.
            float before = target.Percent;
            float scale = before / 100f;
            target.Add(DamagePercent);

            PlayerMotor victim = target.Entity.Get<PlayerMotor>();
            if (victim == null)
                return;
            target.LastHitAt = UMath.Sub(hit.Point, victim.BodyCenter);

            // Away from the attacker; straight along the aim if they overlap.
            Vector3 away = UMath.Sub(victim.Position, motor.Position);
            away.Y = 0f;
            if (UMath.SqrMagnitude(away) < 1e-8f)
                away = new Vector3(aim.X, 0f, aim.Z);
            target.LastHitAway = away;

            // Both speeds along the line count, the same share of each from
            // CarryFromPercent on: the attacker's towards the victim adds, the
            // victim's towards the attacker takes off. Head-on at equal speed
            // they cancel and the knock is the one from standing.
            float carry = 0f;
            float share = CarryShare(before);
            if (share > 0f)
            {
                Vector3 run = new Vector3(motor.Velocity.X, 0f, motor.Velocity.Z);
                carry = UMath.Dot(run, UMath.Normalized(away)) * share;
            }

            float lift = UMath.Max(before, KnockbackHeightFloor) / 100f;
            victim.Knock(away, KnockbackDistance * scale, KnockbackHeight * lift, carry, share, dt);
        }

        /// <summary>Share of both speeds along the line taken into the knock at this
        /// percent: none below CarryFromPercent, CarryStartShare there, rising linearly
        /// to all of it at CarryFullPercent.</summary>
        public float CarryShare(float percent)
        {
            if (percent < CarryFromPercent)
                return 0f;
            if (percent >= CarryFullPercent)
                return 1f;
            float t = (percent - CarryFromPercent) / (CarryFullPercent - CarryFromPercent);
            return CarryStartShare + (1f - CarryStartShare) * t;
        }

        /// <summary>Clears the counters so a probe can measure one case.</summary>
        public void ResetCounters()
        {
            HitCount = 0;
            SwingCount = 0;
            ticksUntilReady = 0;
        }

        // Unity's GetComponentInParent: the entity itself, then upwards.
        internal static DamagePercent FindInParents(Entity entity)
        {
            for (Entity e = entity; e != null; e = e.GetParent())
            {
                DamagePercent d = e.Get<DamagePercent>();
                if (d != null)
                    return d;
            }

            return null;
        }
    }
}
