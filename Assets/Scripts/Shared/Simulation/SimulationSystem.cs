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

        /// <summary>The driver in the scene, null without one.</summary>
        public static SimulationSystem Active => active;

        /// <summary>
        /// Bodies in tick order. The order carries no meaning - the
        /// determinism probe proves that by reversing it - but the inputs
        /// handed to Step line up with it.
        /// </summary>
        public static IReadOnlyList<SimulationBody> Bodies => bodies;

        /// <summary>Ticks stepped so far. Part of what a snapshot records.</summary>
        public static int CurrentTick { get; private set; }

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

        /// <summary>
        /// Raised after the contacts are resolved and before any body moves.
        /// The only moment a push can be read apart from the movement that
        /// follows it; null in the game, used by probes.
        /// </summary>
        public static System.Action AfterContacts;

        /// <summary>
        /// Raised once every body has simulated and none has published.
        /// Lets a probe check that the buffer holds; null in the game.
        /// </summary>
        public static System.Action BeforePublish;

        [Header("Körperstoß")]
        [Tooltip("Anteil der Annäherung, mit dem sich beide vom gemeinsamen " +
                 "Tempo entfernen. 0,5 ist ein vollelastischer Stoß, mehr " +
                 "würde bei jeder Berührung Energie erzeugen.")]
        [Range(0f, 0.5f)]
        [SerializeField] float bounce = 1f / 6f;

        [Tooltip("Langsamere Annäherung löst keinen Stoß aus.")]
        [SerializeField] float minApproach = 0.5f;

        [Tooltip("Wie nah sich zwei Körper kommen müssen, damit es ein Kontakt ist.")]
        [SerializeField] float contactRange = 0.1f;

        [Tooltip("Wie lange nach einem Stoß die Bodenbeschleunigung gedämpft bleibt.")]
        [SerializeField] float knockbackLock = 0.2f;

        // Reused across ticks so a step allocates nothing.
        readonly List<BodyContact.Body> states = new List<BodyContact.Body>();
        readonly List<Vector3> impulses = new List<Vector3>();
        readonly List<Vector3> shifts = new List<Vector3>();
        readonly List<MoveInput> inputs = new List<MoveInput>();

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
            int count = bodies.Count;

            // Gathered up front, because reading consumes: ReadInput clears
            // the key presses it hands over, so asking a second time for the
            // combat pass would get an empty input. It also separates taking
            // orders from carrying them out, which is what a server needs.
            inputs.Clear();
            for (int i = 0; i < count; i++)
                inputs.Add(bodies[i].ReadInput());

            Step(inputs, Time.fixedDeltaTime);
        }

        /// <summary>
        /// One tick for every body, with inputs given in Bodies order. Reads
        /// nothing from devices and nothing from the clock, so the same
        /// inputs on the same snapshot always produce the same tick.
        /// </summary>
        public void Step(IReadOnlyList<MoveInput> tickInputs, float dt)
        {
            int count = bodies.Count;
            if (tickInputs.Count != count)
            {
                Debug.LogError(
                    $"Step mit {tickInputs.Count} Eingaben für {count} Körper.", this);
                return;
            }

            // Contacts first, so a push still takes effect in this tick
            // instead of 25 ms later. It reads what the last tick left
            // behind, the only state both bodies agree on.
            ResolveContacts();
            AfterContacts?.Invoke();

            // Two phases: every body computes its tick on its own pose, and
            // none becomes visible before all are done. Bodies are also left
            // out of every motor query; the buffer still holds if that ever
            // changes, so the list order stays out of the result either way.
            for (int i = 0; i < count; i++)
                bodies[i].Motor.Simulate(tickInputs[i], dt);

            // After the moves, because that is when bodies can overlap, and
            // before combat, so a swing sees where everyone really stands.
            SeparateBodies();

            BeforePublish?.Invoke();

            for (int i = 0; i < count; i++)
                bodies[i].Motor.Publish();

            // The swing asks PhysX, which otherwise knows the hulls only as
            // the last physics step left them. A resimulation runs many ticks
            // between two physics steps and would hit stale hulls.
            Physics.SyncTransforms();

            // After every body has moved, so a swing sees where they all
            // ended up rather than where the ones ahead of it stood.
            for (int i = 0; i < count; i++)
                bodies[i].TickCombat(tickInputs[i], dt);

            CurrentTick++;
        }

        /// <summary>Records every body as this tick left it.</summary>
        public static void Capture(WorldSnapshot into)
        {
            into.Clear();
            into.Tick = CurrentTick;

            for (int i = 0; i < bodies.Count; i++)
            {
                into.bodies.Add(bodies[i]);
                into.states.Add(bodies[i].CaptureState());
            }
        }

        /// <summary>
        /// Puts every body in the snapshot back. Bodies that joined since
        /// are left alone; deciding what they should be is netcode's job.
        /// </summary>
        public static void Restore(WorldSnapshot from)
        {
            for (int i = 0; i < from.Count; i++)
            {
                // Explicit == null: a body destroyed since is fake null.
                SimulationBody body = from.bodies[i];
                if (body != null)
                    body.RestoreState(from.states[i]);
            }

            CurrentTick = from.Tick;
        }

        /// <summary>
        /// Rolls back to a snapshot and steps forward again, one entry of
        /// ticks per tick, each in Bodies order. All the server will need
        /// for a late input: correct the input, resimulate from before it.
        /// </summary>
        public void Resimulate(WorldSnapshot from,
                               IReadOnlyList<MoveInput[]> ticks, float dt)
        {
            Restore(from);

            for (int t = 0; t < ticks.Count; t++)
                Step(ticks[t], dt);
        }

        /// <summary>
        /// Pushes overlapping bodies out of each other, sideways only. Until
        /// now PhysX did this through the character controllers; they are
        /// gone, so somebody has to. Shifts are collected before they are
        /// applied, so the result does not depend on the list order.
        /// </summary>
        void SeparateBodies()
        {
            int count = bodies.Count;
            if (count < 2)
                return;

            states.Clear();
            shifts.Clear();

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

                shifts.Add(Vector3.zero);
            }

            bool any = false;

            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    if (!BodyContact.TryGetSideways(
                            states[i], states[j],
                            out Vector3 n, out float overlap))
                        continue;

                    // Half each: neither body owns the ground they share.
                    Vector3 push = n * (overlap * 0.5f);
                    shifts[i] -= push;
                    shifts[j] += push;
                    any = true;
                }
            }

            if (!any)
                return;

            for (int i = 0; i < count; i++)
            {
                if (shifts[i].sqrMagnitude > 0f)
                    bodies[i].Motor.Nudge(shifts[i]);
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
