using KotH.Shared.Match;
using KotH.Shared.Mathematics;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.Shared.Combat
{
    /// <summary>The mushroom blaster: spores that fly and fall, a few shots that come back
    /// with points in the zone. Driven from outside like MeleeAttack and fed the same
    /// MoveInput, so every peer flies the same spores.</summary>
    [DataContract("KotH.Blaster")]
    [Display("Blaster")]
    [ComponentCategory("KotH")]
    public sealed class Blaster : EntityComponent
    {
        /// <userdoc>Speed of a spore leaving the muzzle, in metres per second.</userdoc>
        [Display("Speed", "Shot")] public float Speed = 130f;

        /// <userdoc>How fast a spore falls, in metres per second squared (earth: 9.81).</userdoc>
        [Display("Gravity", "Shot")] public float Gravity = 9.81f;

        /// <userdoc>Ticks a spore flies before it is gone; 80 ticks are 2 seconds.</userdoc>
        [Display("Lifetime (ticks)", "Shot")] public int LifetimeTicks = 80;

        /// <userdoc>Ticks between two shots.</userdoc>
        [Display("Cooldown (ticks)", "Shot")] public int CooldownTicks = 10;

        /// <userdoc>Ticks after switching weapons before either can be used.</userdoc>
        [Display("Draw (ticks)", "Shot")] public int DrawTicks = 8;

        /// <userdoc>Most shots held, also how many a round starts with.</userdoc>
        [Display("Shots", "Ammo")] public int MaxShots = 3;

        /// <userdoc>Points scored in the zone that bring one shot back. While full nothing is saved up.</userdoc>
        [Display("Points per shot", "Ammo")] public int PointsPerShot = 30;

        /// <userdoc>How many percent a hit adds to the target.</userdoc>
        [Display("Damage percent", "Hit")] public float DamagePercent = 1.2f;

        /// <userdoc>How far a target at 100 % flies along the spore's path, in metres; linear in the percent like the cane's.</userdoc>
        [Display("Distance at 100 %", "Knockback")] public float KnockbackDistance = 18f;

        /// <userdoc>How high a target at 100 % rises, in metres.</userdoc>
        [Display("Height at 100 %", "Knockback")] public float KnockbackHeight = 1.8f;

        /// <userdoc>Below this a target rises as high as at this percent, even at 0 %.</userdoc>
        [Display("Min height as at %", "Knockback")] public float KnockbackHeightFloor = 1f;

        // Ground, walls and every body's hit hull.
        const Stride.BepuPhysics.CollisionMask ShotMask = WorldQueries.WorldMask | WorldQueries.HittableMask;

        PlayerMotor motor;
        BlasterState state;

        /// <summary>The part a rollback restores.</summary>
        [DataMemberIgnore] public BlasterState State => state;

        /// <summary>Tick of the last shot fired and of the last spore that struck, and where;
        /// for the looks only, not state, like MeleeAttack.LastSwingTick.</summary>
        [DataMemberIgnore] public int LastShotTick { get; private set; } = int.MinValue;
        [DataMemberIgnore] public int LastImpactTick { get; private set; } = int.MinValue;
        [DataMemberIgnore] public Vector3 LastImpactPoint { get; private set; }

        /// <summary>Shots fired and bodies hit so far. Diagnostics for the probe.</summary>
        [DataMemberIgnore] public int ShotCount { get; private set; }
        [DataMemberIgnore] public int HitCount { get; private set; }

        /// <summary>Whether the cane may swing this tick: in hand and drawn.</summary>
        [DataMemberIgnore] public bool CaneReady => !state.Selected && state.Draw == 0;

        public void Initialize(PlayerMotor owner)
        {
            motor = owner;
            state = new BlasterState { Shots = MaxShots };
        }

        public void Restore(in BlasterState from) => state = from;

        /// <summary>One combat step, before the cane's: switch, earn shots from the points
        /// as last seen, fly the spores, fire. scoreTicks is the body's ScoreTicks.</summary>
        public void Tick(in MoveInput input, int scoreTicks, int tick, float dt)
        {
            if (input.Blaster != state.Selected)
            {
                state.Selected = input.Blaster;
                state.Draw = DrawTicks;
            }
            else if (state.Draw > 0)
                state.Draw--;

            if (state.Cooldown > 0)
                state.Cooldown--;

            Earn(scoreTicks);

            for (int i = 0; i < BlasterState.Slots; i++)
                if (Slot(ref state, i).Age > 0)
                    Fly(ref Slot(ref state, i), tick, dt);

            if (input.AttackPresses == 0 || !state.Selected || state.Draw > 0 ||
                state.Cooldown > 0 || state.Shots <= 0)
                return;

            state.Shots--;
            state.Cooldown = CooldownTicks;
            ShotCount++;
            LastShotTick = tick;

            // Out of the eye along the crosshair, as the cane's ray; it flies its first
            // stretch in this same tick.
            Vector3 aim = UMath.Rotate(UMath.Euler(input.Pitch, input.Yaw), UMath.Forward);
            ref BlasterShot shot = ref Slot(ref state, FreeSlot());
            shot = new BlasterShot { Position = motor.EyePosition, Velocity = UMath.Mul(aim, Speed) };
            Fly(ref shot, tick, dt);
        }

        // Grows only while not full, so standing in the zone with three shots saves none up.
        void Earn(int scoreTicks)
        {
            int earned = scoreTicks - state.ScoreSeen;
            state.ScoreSeen = scoreTicks;
            if (earned <= 0 || state.Shots >= MaxShots)
                return;

            state.Progress += earned;
            int per = PointsPerShot * HillMatch.TicksPerPoint;
            while (state.Progress >= per && state.Shots < MaxShots)
            {
                state.Progress -= per;
                state.Shots++;
            }
            if (state.Shots >= MaxShots)
                state.Progress = 0;
        }

        // A free slot, else the oldest spore makes room.
        int FreeSlot()
        {
            int oldest = 0;
            for (int i = 0; i < BlasterState.Slots; i++)
            {
                int age = Slot(ref state, i).Age;
                if (age == 0)
                    return i;
                if (age > Slot(ref state, oldest).Age)
                    oldest = i;
            }
            return oldest;
        }

        // Semi-implicit like the motor's fall: speed first, then the stretch, which a ray
        // checks against everything but the shooter's own hull.
        void Fly(ref BlasterShot shot, int tick, float dt)
        {
            shot.Age++;
            if (shot.Age > LifetimeTicks)
            {
                shot = default;
                return;
            }

            shot.Velocity.Y -= Gravity * dt;
            Vector3 step = UMath.Mul(shot.Velocity, dt);
            float length = UMath.Magnitude(step);
            if (length > 0f && motor.World.Raycast(shot.Position, UMath.Div(step, length), length,
                                                   ShotMask, out QueryHit hit, motor.Hull))
            {
                Strike(hit, step, tick, dt);
                shot = default;
                return;
            }

            shot.Position = UMath.Add(shot.Position, step);
        }

        // The cane's hit with the spore's path as the push line and no carry: a shot from
        // afar does not bring the shooter's run along.
        void Strike(in QueryHit hit, Vector3 step, int tick, float dt)
        {
            LastImpactTick = tick;
            LastImpactPoint = hit.Point;

            DamagePercent target = MeleeAttack.FindInParents(hit.Collider.Entity);
            if (target == null)
                return;

            HitCount++;
            Vector3 away = new Vector3(step.X, 0f, step.Z);
            target.LastHitTick = tick;
            target.LastHitAway = away;
            target.LastHitAt = default;

            float before = target.Percent;
            target.Add(DamagePercent);

            PlayerMotor victim = target.Entity.Get<PlayerMotor>();
            if (victim == null)
                return;
            target.LastHitAt = UMath.Sub(hit.Point, victim.BodyCenter);

            float lift = UMath.Max(before, KnockbackHeightFloor) / 100f;
            victim.Knock(away, KnockbackDistance * (before / 100f), KnockbackHeight * lift, 0f, 0f, dt);
        }

        /// <summary>Slot i of the three, by reference.</summary>
        public static ref BlasterShot Slot(ref BlasterState s, int i)
        {
            if (i == 0)
                return ref s.Shot0;
            if (i == 1)
                return ref s.Shot1;
            return ref s.Shot2;
        }

        /// <summary>Clears the counters so a probe can measure one case.</summary>
        public void ResetCounters()
        {
            ShotCount = 0;
            HitCount = 0;
        }
    }
}
