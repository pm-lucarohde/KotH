using KotH.Shared.Player;
using UnityEngine;

namespace KotH.Shared.Combat
{
    /// <summary>
    /// A swing straight ahead. Driven from outside like PlayerMotor, reads no
    /// input and knows no camera: the aim comes out of the tick's MoveInput,
    /// so a dedicated server computes the same ray.
    /// </summary>
    [RequireComponent(typeof(PlayerMotor))]
    public sealed class MeleeAttack : MonoBehaviour
    {
        [Header("Schlag")]
        [Tooltip("Reichweite ab Augenhöhe, in Metern.")]
        [SerializeField] float range = 3.5f;

        [Tooltip("Wie viel Prozent ein Treffer dem Getroffenen auflädt.")]
        [SerializeField] float damagePercent = 1.2f;

        // Ticks, not seconds: a third of a second is 13.33 ticks at 40 Hz and
        // would silently round up to 14. Counting ticks is also what a server
        // can replay without an argument about rounding.
        [Tooltip("Ticks zwischen zwei Schlägen. 13 Ticks sind 3,08 pro Sekunde.")]
        [SerializeField] int cooldownTicks = 13;

        PlayerMotor motor;
        int ticksUntilReady;

        /// <summary>Hits landed so far. Diagnostics for the probe.</summary>
        public int HitCount { get; private set; }

        /// <summary>Swings started so far, whether they connected or not.</summary>
        public int SwingCount { get; private set; }

        /// <summary>
        /// How far the last hit landed from the eye. Diagnostics: the range
        /// limits the distance to the capsule SURFACE, and where that sits
        /// depends on how high the ray crosses the body.
        /// </summary>
        public float LastHitDistance { get; private set; }

        public float Range => range;

        void Awake()
        {
            motor = GetComponent<PlayerMotor>();
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

            ticksUntilReady = cooldownTicks;
            SwingCount++;

            // Same rotation the camera builds, so the ray goes exactly where
            // the crosshair points - but out of the input, not out of a
            // Transform the server would not have.
            Vector3 aim = Quaternion.Euler(input.Pitch, input.Yaw, 0f)
                        * Vector3.forward;

            // Mask is 0 while the layer is missing, so nothing is ever hit.
            // Hittable.Awake reports that case on its own.
            //
            // Collide, not Ignore: a hit hull is a trigger on purpose. It
            // must not block anything - least of all the body's own
            // controller sitting on the same object - but a swing has to
            // find it.
            if (!Physics.Raycast(motor.EyePosition, aim, out RaycastHit hit,
                                 range, Hittable.Mask,
                                 QueryTriggerInteraction.Collide))
                return;

            DamagePercent target = hit.collider.GetComponentInParent<DamagePercent>();
            if (target == null)
                return;

            // Counted even at the cap: the ray did connect. Otherwise a
            // probe could not tell a miss apart from a full target.
            HitCount++;
            LastHitDistance = hit.distance;
            target.Add(damagePercent);
        }

        /// <summary>Clears the counters so a probe can measure one case.</summary>
        public void ResetCounters()
        {
            HitCount = 0;
            SwingCount = 0;
            ticksUntilReady = 0;
        }
    }
}
