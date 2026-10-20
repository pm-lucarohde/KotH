using System;
using System.Collections.Generic;
using KotH.Shared.Match;
using KotH.Shared.Mathematics;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;

namespace KotH.Shared.Simulation
{
    /// <summary>
    /// The one place a simulation step is driven from. A collision between
    /// two bodies needs a moment where both states are valid side by side,
    /// so no body ticks itself. The game, the server and the probes all run
    /// this same loop; whoever owns it calls Tick at 40 Hz.
    /// </summary>
    public sealed class SimulationSystem
    {
        /// <summary>The fixed step, Unity's 3528000 / 141120000 s.</summary>
        public const float TickSeconds = 0.025f;

        static readonly Logger Log = GlobalLogger.GetLogger("KotH");

        // Per world, not static: a probe runs a host and its peers side by
        // side in one process, each with its own bodies and its own clock.
        readonly List<SimulationBody> bodies = new List<SimulationBody>();
        bool active;

        /// <summary>
        /// Bodies in tick order. The order carries no meaning - the
        /// determinism probe proves that by reversing it - but the inputs
        /// handed to Step line up with it.
        /// </summary>
        public IReadOnlyList<SimulationBody> Bodies => bodies;

        /// <summary>Ticks stepped so far. Part of what a snapshot records.</summary>
        public int CurrentTick { get; private set; }

        /// <summary>
        /// Contacts the last tick resolved. Diagnostics: without it a probe
        /// cannot tell a push apart from ordinary acceleration.
        /// </summary>
        public int LastContactCount { get; private set; }

        /// <summary>
        /// Normal and closing speed of the last contact resolved. A probe
        /// needs these to check a push against the model: reading velocities
        /// before and after is a tick too late once gravity is involved.
        /// </summary>
        public Vector3 LastNormal { get; private set; }
        public float LastApproach { get; private set; }

        /// <summary>
        /// Raised after the contacts are resolved and before any body moves.
        /// The only moment a push can be read apart from the movement that
        /// follows it; null in the game, used by probes.
        /// </summary>
        public Action AfterContacts;

        /// <summary>
        /// Raised once every body has simulated and none has published.
        /// Lets a probe check that the buffer holds; null in the game.
        /// </summary>
        public Action BeforePublish;

        /// <summary>
        /// Share of the approach both bodies leave the shared speed with.
        /// 0.5 is a fully elastic push, more would create energy on contact.
        /// </summary>
        public float Bounce = 1f / 6f;

        /// <summary>Slower approaches do not push.</summary>
        public float MinApproach = 0.5f;

        /// <summary>How close two bodies have to be to count as touching.</summary>
        public float ContactRange = 0.1f;

        /// <summary>How long ground control stays soft after a push.</summary>
        public float KnockbackLock = 0.2f;

        // Reused across ticks so a step allocates nothing.
        readonly List<BodyContact.Body> states = new List<BodyContact.Body>();
        readonly List<Vector3> impulses = new List<Vector3>();
        readonly List<Vector3> shifts = new List<Vector3>();
        readonly List<MoveInput> inputs = new List<MoveInput>();

        public SimulationSystem(WorldQueries world)
        {
            World = world;
        }

        /// <summary>The physics questions every body asks.</summary>
        public WorldQueries World { get; }

        /// <summary>
        /// The round played for the zone, null for none. Set by whoever hosts
        /// a game; probes that only measure movement leave it off, so no
        /// round end can ever put their bodies back.
        /// </summary>
        public HillMatch Match { get; set; }

        /// <summary>Whether Tick steps. A probe that calls Step itself switches it off.</summary>
        public bool IsActive => active;

        public void Activate() => active = true;

        public void Deactivate() => active = false;

        public void Register(SimulationBody body)
        {
            if (!bodies.Contains(body))
                bodies.Add(body);
        }

        public void Unregister(SimulationBody body)
        {
            bodies.Remove(body);
        }

        /// <summary>
        /// Takes a body out of the tick but keeps its entity and its place in
        /// the query order, so a rollback to before it left can bring it
        /// back exactly. Netcode destroys it once no rollback can reach back.
        /// </summary>
        public void Leave(SimulationBody body)
        {
            Unregister(body);
            Park(body);
        }

        /// <summary>A parked body joins the tick again at the end of the list.</summary>
        public void Rejoin(SimulationBody body, in BodyState state)
        {
            body.IsParked = false;
            body.RestoreState(state);
            Register(body);
        }

        static void Park(SimulationBody body)
        {
            body.IsParked = true;
            body.Motor.ParkHull();
        }

        /// <summary>
        /// One fixed tick: gathers every body's input, then steps. Unity's
        /// FixedUpdate, called by whoever owns the clock.
        /// </summary>
        public void Tick()
        {
            if (!active)
                return;

            int count = bodies.Count;

            // Gathered up front, because reading consumes: ReadInput clears
            // the key presses it hands over, so asking a second time for the
            // combat pass would get an empty input. It also separates taking
            // orders from carrying them out, which is what a server needs.
            inputs.Clear();
            for (int i = 0; i < count; i++)
                inputs.Add(bodies[i].ReadInput());

            Step(inputs, TickSeconds);
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
                Log.Error($"Step mit {tickInputs.Count} Eingaben für {count} Körper.");
                return;
            }

            // Before anything moves, so a new round puts a body back exactly
            // where it joined.
            Match?.MarkNewcomers(bodies);

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

            // The swing asks the physics world, which otherwise knows the
            // hulls only where they were last put. A resimulation runs many
            // ticks in a row and would hit stale hulls.
            for (int i = 0; i < count; i++)
                bodies[i].Motor.SyncHull();

            // After every body has moved, so a swing sees where they all
            // ended up rather than where the ones ahead of it stood.
            for (int i = 0; i < count; i++)
                bodies[i].TickCombat(tickInputs[i], dt);

            // Last, on the poses everyone ended up in, after any knock.
            Match?.Tick(bodies);

            CurrentTick++;
        }

        /// <summary>Records every body and the round as this tick left them.</summary>
        public void Capture(WorldSnapshot into)
        {
            into.Clear();
            into.Tick = CurrentTick;
            into.Match = Match?.State ?? default;

            for (int i = 0; i < bodies.Count; i++)
            {
                into.bodies.Add(bodies[i]);
                into.states.Add(bodies[i].CaptureState());
            }
        }

        /// <summary>
        /// Puts the world back to the snapshot: who is in the tick, in which
        /// order, and in what state. Bodies the snapshot does not know, such
        /// as one that joined since, are parked; a rollback across a join or
        /// a leave thus lands where every peer that never rolled back is.
        /// </summary>
        public void Restore(WorldSnapshot from)
        {
            for (int i = 0; i < bodies.Count; i++)
                if (!from.Contains(bodies[i]))
                    Park(bodies[i]);

            // The list order goes back too: float sums over three or more
            // touching bodies depend on it, and peers must agree.
            bodies.Clear();
            for (int i = 0; i < from.Count; i++)
            {
                SimulationBody body = from.bodies[i];
                body.IsParked = false;
                body.RestoreState(from.states[i]);
                bodies.Add(body);
            }

            Match?.Restore(from.Match);
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

        /// <summary>Back to tick zero, for a probe that starts a fresh world.</summary>
        public void ResetTickCounter() => CurrentTick = 0;

        /// <summary>
        /// Pushes overlapping bodies out of each other, sideways only. No
        /// physics engine does it for kinematic capsules, so somebody has to.
        /// Shifts are collected before they are applied, so the result does
        /// not depend on the list order.
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

                shifts.Add(Vector3.Zero);
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
                    Vector3 push = UMath.Mul(n, overlap * 0.5f);
                    shifts[i] = UMath.Sub(shifts[i], push);
                    shifts[j] = UMath.Add(shifts[j], push);
                    any = true;
                }
            }

            if (!any)
                return;

            for (int i = 0; i < count; i++)
            {
                if (UMath.SqrMagnitude(shifts[i]) > 0f)
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

                impulses.Add(Vector3.Zero);
            }

            bool any = false;

            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    BodyContact.Body a = states[i];
                    BodyContact.Body b = states[j];

                    if (!BodyContact.TryGetNormal(a, b, ContactRange, out Vector3 n))
                        continue;

                    // Positive means they are closing in. A pair already
                    // drifting apart must never be hit again, or pressing
                    // against someone would turn into a buzz.
                    float approach = UMath.Dot(UMath.Sub(a.velocity, b.velocity), n);
                    if (approach <= MinApproach)
                        continue;

                    // Both end up moving around the speed they share along
                    // the normal, not around zero. That conserves momentum:
                    // running into someone hands the motion over instead of
                    // stopping the runner dead and barely nudging the other.
                    float alongA = UMath.Dot(a.velocity, n);
                    float alongB = UMath.Dot(b.velocity, n);
                    float shared = (alongA + alongB) * 0.5f;
                    float separate = Bounce * approach;

                    impulses[i] = UMath.Add(impulses[i], UMath.Mul(n, shared - separate - alongA));
                    impulses[j] = UMath.Add(impulses[j], UMath.Mul(n, shared + separate - alongB));
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
                if (UMath.SqrMagnitude(impulses[i]) > 0f)
                    bodies[i].Motor.AddVelocity(impulses[i], KnockbackLock);
            }
        }
    }
}
