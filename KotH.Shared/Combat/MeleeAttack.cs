using KotH.Shared.Mathematics;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.Shared.Combat
{
    /// <summary>
    /// A swing straight ahead. Driven from outside like PlayerMotor, reads no
    /// input and knows no camera: the aim comes out of the tick's MoveInput,
    /// so a dedicated server computes the same ray.
    /// </summary>
    [DataContract("KotH.MeleeAttack")]
    [Display("Nahkampf")]
    [ComponentCategory("KotH")]
    public sealed class MeleeAttack : EntityComponent
    {
        /// <userdoc>Reichweite ab Augenhöhe, in Metern.</userdoc>
        [Display("Reichweite", "Schlag")] public float Range = 3.5f;

        /// <userdoc>Wie viel Prozent ein Treffer dem Getroffenen auflädt.</userdoc>
        [Display("Schadensprozent", "Schlag")] public float DamagePercent = 1.2f;

        /// <userdoc>
        /// Wie weit ein Getroffener bei 100 % waagerecht fliegt, in Metern, ohne
        /// Gegensteuern auf ebenem Boden. Wächst linear mit dem Prozent vor dem
        /// Treffer: 0 % stößt waagerecht gar nicht, 1000 % zehnmal so weit.
        /// </userdoc>
        [Display("Weite bei 100 %", "Rückstoß")] public float KnockbackDistance = 12f;

        /// <userdoc>
        /// Wie hoch ein Getroffener bei 100 % steigt, in Metern. Linear wie die Weite.
        /// Steigt oder fällt er beim Treffer in der Luft, kommt der Stoß auf dieses
        /// Tempo obendrauf.
        /// </userdoc>
        [Display("Höhe bei 100 %", "Rückstoß")] public float KnockbackHeight = 1.2f;

        /// <userdoc>
        /// Darunter steigt ein Getroffener so hoch wie bei diesem Prozent, auch
        /// bei 0 %. Nur die Höhe: waagerecht stößt 0 % weiter gar nicht.
        /// </userdoc>
        [Display("Höhe mindestens wie bei %", "Rückstoß")] public float KnockbackHeightFloor = 1f;

        /// <userdoc>
        /// Ab diesem Prozent vor dem Treffer zählt beim Treffer auch das Tempo
        /// beider auf der Stoßlinie, zunächst mit dem Anteil „Tempoanteil am
        /// Anfang“: Läuft der Schläger auf den Getroffenen zu, stößt er
        /// stärker; läuft der Getroffene auf ihn zu, schwächer. Darunter zählt
        /// es nicht.
        /// </userdoc>
        [Display("Tempo zählt ab %", "Rückstoß")] public float CarryFromPercent = 10f;

        /// <userdoc>Anteil beider Tempi ab „Tempo zählt ab %“, 0 bis 1.</userdoc>
        [Display("Tempoanteil am Anfang", "Rückstoß")] public float CarryStartShare = 0.5f;

        /// <userdoc>
        /// Ab diesem Prozent zählen beide Tempi voll; dazwischen steigt der
        /// Anteil linear. Gehen sie gegeneinander auf, fliegt der Getroffene
        /// wie im Stand; der Stoß zeigt nie zum Schläger hin.
        /// </userdoc>
        [Display("Tempo voll ab %", "Rückstoß")] public float CarryFullPercent = 20f;

        // Ticks, not seconds: a third of a second is 13.33 ticks at 40 Hz and
        // would silently round up to 14. Counting ticks is also what a server
        // can replay without an argument about rounding.
        /// <userdoc>Ticks zwischen zwei Schlägen. 13 Ticks sind 3,08 pro Sekunde.</userdoc>
        [Display("Abklingzeit (Ticks)", "Schlag")] public int CooldownTicks = 13;

        PlayerMotor motor;
        int ticksUntilReady;

        /// <summary>Hits landed so far. Diagnostics for the probe.</summary>
        [DataMemberIgnore] public int HitCount { get; private set; }

        /// <summary>Swings started so far, whether they connected or not.</summary>
        [DataMemberIgnore] public int SwingCount { get; private set; }

        /// <summary>
        /// How far the last hit landed from the eye. Diagnostics: the range
        /// limits the distance to the capsule SURFACE, and where that sits
        /// depends on how high the ray crosses the body.
        /// </summary>
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

            // The percent before this hit decides: a fresh body is not moved.
            float before = target.Percent;
            float scale = before / 100f;
            target.Add(DamagePercent);

            PlayerMotor victim = target.Entity.Get<PlayerMotor>();
            if (victim == null)
                return;

            // Away from the attacker; straight along the aim if they overlap.
            Vector3 away = UMath.Sub(victim.Position, motor.Position);
            away.Y = 0f;
            if (UMath.SqrMagnitude(away) < 1e-8f)
                away = new Vector3(aim.X, 0f, aim.Z);

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

        /// <summary>
        /// Share of both speeds along the line taken into the knock at this percent:
        /// none below CarryFromPercent, CarryStartShare there, rising linearly
        /// to all of it at CarryFullPercent.
        /// </summary>
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
        static DamagePercent FindInParents(Entity entity)
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
