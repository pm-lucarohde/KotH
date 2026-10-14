using System.Collections.Generic;
using KotH.Shared.Player;
using UnityEngine;

namespace KotH.Shared.Simulation
{
    /// <summary>
    /// The one place a simulation step is driven from. One FixedUpdate per
    /// body left the order to Unity, and a collision between two of them
    /// needs a moment where both states are valid side by side. A dedicated
    /// server later runs this same loop.
    /// </summary>
    public sealed class SimulationSystem : MonoBehaviour
    {
        // Static, so a body can register before this object's Awake runs and
        // long after scene load once netcode spawns players.
        static readonly List<SimulationBody> bodies = new List<SimulationBody>();
        static SimulationSystem active;

        public static bool HasActive => active != null;

        /// <summary>
        /// Contacts the last tick resolved. Diagnostics: without it a probe
        /// cannot tell a push apart from ordinary acceleration.
        /// </summary>
        public static int LastContactCount { get; private set; }

        /// <summary>
        /// Normal and closing speed of the last contact resolved. A probe
        /// needs these to check a push against the model: reading velocities
        /// before and after is a tick too late once gravity is involved.
        /// </summary>
        public static Vector3 LastNormal { get; private set; }
        public static float LastApproach { get; private set; }

        [Header("Körperstoß")]
        [Tooltip("Anteil der Annäherung, mit dem sich beide vom gemeinsamen " +
                 "Tempo entfernen. 0,5 ist ein vollelastischer Stoß, mehr " +
                 "würde bei jeder Berührung Energie erzeugen.")]
        [Range(0f, 0.5f)]
        [SerializeField] float bounce = 1f / 3f;

        [Tooltip("Langsamere Annäherung löst keinen Stoß aus.")]
        [SerializeField] float minApproach = 0.5f;

        [Tooltip("Wie nah sich zwei Körper kommen müssen, damit es ein Kontakt ist.")]
        [SerializeField] float contactRange = 0.1f;

        [Tooltip("Wie lange nach einem Stoß die Bodenbeschleunigung gedämpft bleibt.")]
        [SerializeField] float knockbackLock = 0.2f;

        // Reused across ticks so a step allocates nothing.
        readonly List<BodyContact.Body> states = new List<BodyContact.Body>();
        readonly List<Vector3> impulses = new List<Vector3>();

        public static void Register(SimulationBody body)
        {
            if (!bodies.Contains(body))
                bodies.Add(body);
        }

        public static void Unregister(SimulationBody body)
        {
            bodies.Remove(body);
        }

        void Awake()
        {
            // Explicit == null, Unity's fake null breaks the other checks.
            if (active != null && active != this)
            {
                Debug.LogWarning(
                    "Zweites SimulationSystem in der Szene, es bleibt aus.", this);
                enabled = false;
                return;
            }

            active = this;
        }

        void OnDestroy()
        {
            if (active == this)
                active = null;
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;

            // Contacts first, so a push still takes effect in this tick
            // instead of 25 ms later. It reads what the last tick left
            // behind, the only state both bodies agree on.
            ResolveContacts();

            for (int i = 0; i < bodies.Count; i++)
            {
                SimulationBody body = bodies[i];
                body.Motor.Tick(body.ReadInput(), dt);
            }
        }

        /// <summary>
        /// Pushes touching bodies apart. Impulses are collected first and
        /// applied afterwards, so no body sees a half resolved world and the
        /// outcome does not depend on the order of the list.
        /// </summary>
        void ResolveContacts()
        {
            LastContactCount = 0;

            int count = bodies.Count;
            if (count < 2)
                return;

            states.Clear();
            impulses.Clear();

            for (int i = 0; i < count; i++)
            {
                PlayerMotor motor = bodies[i].Motor;
                states.Add(new BodyContact.Body
                {
                    center = motor.BodyCenter,
                    velocity = motor.Velocity,
                    height = motor.Height,
                    radius = motor.Radius,
                });

                impulses.Add(Vector3.zero);
            }

            bool any = false;

            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    BodyContact.Body a = states[i];
                    BodyContact.Body b = states[j];

                    if (!BodyContact.TryGetNormal(a, b, contactRange, out Vector3 n))
                        continue;

                    // Positive means they are closing in. A pair already
                    // drifting apart must never be hit again, or pressing
                    // against someone would turn into a buzz.
                    float approach = Vector3.Dot(a.velocity - b.velocity, n);
                    if (approach <= minApproach)
                        continue;

                    // Both end up moving around the speed they share along
                    // the normal, not around zero. That conserves momentum:
                    // running into someone hands the motion over instead of
                    // stopping the runner dead and barely nudging the other.
                    float alongA = Vector3.Dot(a.velocity, n);
                    float alongB = Vector3.Dot(b.velocity, n);
                    float shared = (alongA + alongB) * 0.5f;
                    float separate = bounce * approach;

                    impulses[i] += n * (shared - separate - alongA);
                    impulses[j] += n * (shared + separate - alongB);
                    LastContactCount++;
                    LastNormal = n;
                    LastApproach = approach;
                    any = true;
                }
            }

            if (!any)
                return;

            for (int i = 0; i < count; i++)
            {
                if (impulses[i].sqrMagnitude > 0f)
                    bodies[i].Motor.AddVelocity(impulses[i], knockbackLock);
            }
        }
    }
}
