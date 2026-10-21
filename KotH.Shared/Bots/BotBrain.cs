using System;
using System.Collections.Generic;
using KotH.Shared.Combat;
using KotH.Shared.Match;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;

namespace KotH.Shared.Bots
{
    /// <summary>What a bot wants right now; the utility AI picks one per think.</summary>
    public enum BotIntent : byte { Idle, Capture, Hold, Attack, Recover }

    /// <summary>
    /// One bot's mind. Sees only a final state ReactionTicks old, steps once
    /// per tick in tick order, and keeps everything else to itself: the
    /// intent, the path, the maneuver plan and its xorshift. Called with the
    /// same states it always answers the same, which is all the netcode needs.
    /// </summary>
    public sealed class BotBrain
    {
        const float Dt = SimulationSystem.TickSeconds;

        // Maneuver weights per intent, in Maneuver order:
        // walk, sprint, strafe l/r, feint, jump, double jump, slide, crouch, strike.
        static readonly float[][] Weights =
        {
            new[] { 0.5f, 0f, 0.3f, 0.3f, 6f, 0.1f, 0f, 0f, 0.2f, 0f },          // Idle
            new[] { 1f, 6f, 0.2f, 0.2f, 0.05f, 0.8f, 4f, 2f, 0f, 0f },           // Capture
            new[] { 3f, 0.5f, 1f, 1f, 2f, 0.3f, 0.1f, 0f, 0.3f, 0f },            // Hold
            new[] { 1f, 3f, 1.2f, 1.2f, 0.6f, 0.9f, 3f, 2.5f, 0.1f, 2.5f },       // Attack
            new[] { 1f, 2f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f },                    // Recover
        };

        readonly int netId;
        readonly BotNavigation nav;
        readonly BotSettings settings;
        readonly HillMatch match;
        readonly PlayerMotor body;
        readonly MeleeAttack fists;
        readonly ManeuverWfc wfc;
        readonly ushort[] allowed;
        readonly List<Corner> corners = new List<Corner>();
        readonly float[] scores = new float[16];
        readonly int[] targets = new int[16];
        readonly BotIntent[] intents = new BotIntent[16];
        uint rng;

        BotIntent intent = BotIntent.Idle;
        int target;                  // NetId of the body attacked, 0 for none
        Maneuver[] plan = Array.Empty<Maneuver>();
        int planStart;
        Maneuver running = Maneuver.Walk;
        int nextThink;
        bool started;
        float yaw, pitch;
        int jumpHeldUntil = -1, doubleJumpAt = -1, lastJump = -1000, lastStrike = -1000;
        int stuckTicks;
        int slideTap = -1;
        bool landSlide;
        Vector3 spot;                // where in the zone this bot stands
        bool hasSpot;
        Vector3 goal;

        public BotBrain(int netId, uint seed, BotNavigation nav, BotSettings settings, HillMatch match,
                        PlayerMotor body, MeleeAttack fists)
        {
            this.netId = netId;
            rng = seed;
            this.nav = nav;
            this.settings = settings;
            this.match = match;
            this.body = body;
            this.fists = fists;
            wfc = new ManeuverWfc(Math.Max(2, settings.SlotsPerPlan), Math.Max(1, settings.SlotTicks), fists.CooldownTicks);
            allowed = new ushort[wfc.Slots];
        }

        public int NetId => netId;
        public BotIntent Intent => intent;
        public int Target => target;
        public Vector3 Goal => goal;
        public Maneuver Running => running;

        int Reaction => Math.Max(3, settings.ReactionTicks);

        /// <summary>
        /// The input for tick u from what the world looked like at u minus
        /// the reaction time; view is null before that tick existed.
        /// </summary>
        public MoveInput Step(int u, WorldSnapshot view)
        {
            int self = view != null ? IndexOf(view, netId) : -1;
            if (self < 0)
                return new MoveInput { Yaw = yaw, Pitch = pitch };

            BodyState me = view.StateAt(self);
            if (!started)
            {
                started = true;
                yaw = UMath.YawOf(me.Motor.Rotation);
                nextThink = u;
            }

            // Everything seen is Reaction ticks old; carried forward on its speed.
            float ahead = Reaction * Dt;
            Vector3 at = Ahead(me.Motor.Position, me.Motor.Velocity, ahead);
            Vector3 feet = new Vector3(at.X, at.Y - body.StandHeight * 0.5f, at.Z);
            // A hit's knock, not the brief soft control after a bump.
            bool knocked = UMath.Magnitude(me.Motor.Knock) > 0.5f;
            bool airborne = !me.Motor.Grounded || u - lastJump < 20;
            MatchPhase phase = match != null ? view.Match.Phase : MatchPhase.Running;

            if (u >= nextThink)
            {
                nextThink = u + Math.Max(1, settings.ThinkTicks);
                BotIntent was = intent;
                int wasTarget = target;
                Think(view, self, at, phase, knocked);
                if (!nav.Plan(feet, goal, corners))
                    corners.Clear();
                if (intent != was || target != wasTarget || u - planStart >= plan.Length * Slot || (knocked && intent != BotIntent.Recover))
                    Replan(u, view, at, airborne);
            }
            if (u - planStart >= plan.Length * Slot)
                Replan(u, view, at, airborne);

            int slot = (u - planStart) / Slot;
            Maneuver m = plan[Math.Min(slot, plan.Length - 1)];
            bool slotStart = (u - planStart) % Slot == 0;
            if (slotStart)
                running = m;

            return Steer(u, view, me, at, feet, m, slotStart, airborne);
        }

        int Slot => Math.Max(1, settings.SlotTicks);

        // How far from a target a bot likes to stand, inside the reach.
        const float Spacing = 2.3f;

        // Share of the remaining angle turned per tick.
        const float Ease = 0.3f;

        static int IndexOf(WorldSnapshot view, int id)
        {
            for (int i = 0; i < view.Count; i++)
                if (view.BodyAt(i).NetId == id)
                    return i;
            return -1;
        }

        static Vector3 Ahead(Vector3 p, Vector3 v, float seconds)
            => new Vector3(p.X + v.X * seconds, p.Y, p.Z + v.Z * seconds);

        static float Flat(Vector3 a, Vector3 b)
        {
            float dx = b.X - a.X, dz = b.Z - a.Z;
            return UMath.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// The utility AI: every intent and every target gets a score, the
        /// running one a bonus; among those close to the best the xorshift
        /// picks, weighted by score.
        /// </summary>
        void Think(WorldSnapshot view, int self, Vector3 at, MatchPhase phase, bool knocked)
        {
            int n = 0;

            void Offer(BotIntent i, int t, float score)
            {
                if (n >= scores.Length || score <= 0f)
                    return;
                if (i == intent && t == target)
                    score += settings.Persistence;
                intents[n] = i;
                targets[n] = t;
                scores[n++] = score;
            }

            bool live = phase == MatchPhase.Running;
            bool inZone = match != null && match.Contains(at);
            SimulationBody holder = view.Match.Holder;

            if (knocked)
                Offer(BotIntent.Recover, 0, 2f);
            if (live && match != null)
            {
                Offer(BotIntent.Capture, 0, inZone ? 0f : 1.2f);
                Offer(BotIntent.Hold, 0, inZone ? 0.9f + (holder != null && holder.NetId == netId ? 0.2f : 0f) : 0f);
            }
            Offer(BotIntent.Idle, 0, live ? 0.05f : phase == MatchPhase.Over ? 1f : 0.4f);

            for (int i = 0; i < view.Count; i++)
            {
                SimulationBody other = view.BodyAt(i);
                if (i == self || other.NetId == 0)
                    continue;
                BodyState s = view.StateAt(i);
                Vector3 there = Ahead(s.Motor.Position, s.Motor.Velocity, Reaction * Dt);
                float d = UMath.Magnitude(UMath.Sub(there, at));
                if (d > settings.EngageRange || phase == MatchPhase.Over)
                    continue;

                // Fights are about the hill: far from it they are worth little.
                bool theyHold = match != null && match.Contains(there);
                float score = 0.3f + 0.5f * (1f - d / settings.EngageRange)
                            + (theyHold ? 0.6f : 0f)
                            + (holder == other ? 0.4f : 0f)
                            + 0.2f * UMath.Min(1f, s.DamagePercent / 100f);
                if (nav.NearestEdge(Feet(there), 2.5f, out _, out _))
                    score += 0.2f;
                if (live && match != null && !theyHold && !NearHill(at) && !NearHill(there))
                    score *= 0.4f;
                if (inZone && !theyHold && d > 4f)
                    score *= 0.5f;
                if (!live)
                    score *= 0.8f;
                Offer(BotIntent.Attack, other.NetId, score);
            }

            if (n == 0)
            {
                intent = BotIntent.Idle;
                target = 0;
                goal = Feet(at);
                return;
            }

            float best = 0f;
            for (int i = 0; i < n; i++)
                best = UMath.Max(best, scores[i]);
            float floor = best * (1f - settings.Randomness), total = 0f;
            for (int i = 0; i < n; i++)
                if (scores[i] >= floor)
                    total += scores[i];
            float roll = BotRandom.Unit(ref rng) * total;
            int pick = 0;
            for (int i = 0; i < n; i++)
            {
                if (scores[i] < floor)
                    continue;
                pick = i;
                roll -= scores[i];
                if (roll < 0f)
                    break;
            }

            intent = intents[pick];
            target = targets[pick];
            goal = GoalOf(view, at);
        }

        // Within a few metres of the zone's box, sideways.
        bool NearHill(Vector3 p)
            => match != null && UMath.Abs(p.X - match.Center.X) < match.HalfSize.X + 4f &&
               UMath.Abs(p.Z - match.Center.Z) < match.HalfSize.Z + 4f;

        Vector3 Feet(Vector3 position) => new Vector3(position.X, position.Y - body.StandHeight * 0.5f, position.Z);

        Vector3 GoalOf(WorldSnapshot view, Vector3 at)
        {
            switch (intent)
            {
                case BotIntent.Capture:
                case BotIntent.Hold:
                case BotIntent.Recover:
                    if (!hasSpot || intent == BotIntent.Hold && BotRandom.Unit(ref rng) < 0.15f)
                        PickSpot();
                    return spot;

                case BotIntent.Attack:
                    int i = IndexOf(view, target);
                    if (i < 0)
                        return Feet(at);
                    BodyState s = view.StateAt(i);
                    Vector3 there = Feet(Ahead(s.Motor.Position, s.Motor.Velocity, Reaction * Dt + 0.2f));
                    if (Flat(at, there) > 4.5f)
                        return there;

                    // Close by: keep a step inside the reach instead of running
                    // into them, on the side away from the nearest edge, so the
                    // knock, which points away from us, goes over it.
                    Vector3 side = new Vector3(at.X - there.X, 0f, at.Z - there.Z);
                    if (nav.NearestEdge(there, 4f, out Vector3 edge, out _))
                        side = new Vector3(there.X - edge.X, 0f, there.Z - edge.Z);
                    if (UMath.SqrMagnitude(side) < 1e-4f)
                        return there;
                    Vector3 stand = UMath.Add(there, UMath.Mul(UMath.Normalized(side), Spacing));
                    return nav.Nearest(stand, out Vector3 onMesh) ? onMesh : there;

                default:
                    return Feet(at);
            }
        }

        // A place on the hill of its own, so the bots do not all pile on its centre.
        void PickSpot()
        {
            if (match == null)
                return;
            float angle = BotRandom.Unit(ref rng) * MathF.PI * 2f;
            float radius = 0.5f + BotRandom.Unit(ref rng) * UMath.Min(match.HalfSize.X, match.HalfSize.Z) * 0.45f;
            var p = new Vector3(match.Center.X + radius * UMath.Cos(angle), match.Center.Y - match.HalfSize.Y,
                                match.Center.Z + radius * UMath.Sin(angle));
            spot = nav.Nearest(p, out Vector3 onMesh) ? onMesh : p;
            hasSpot = true;
        }

        /// <summary>
        /// A fresh maneuver plan by wave function collapse. Attacking, the
        /// slot in which the target should come within reach must swing,
        /// mostly after a slide; in the air no jump may start.
        /// </summary>
        void Replan(int u, WorldSnapshot view, Vector3 at, bool airborne)
        {
            Array.Clear(allowed);
            if (airborne)
                allowed[0] = (ushort)(ManeuverWfc.All & ~(1 << (int)Maneuver.Jump));

            int i = intent == BotIntent.Attack ? IndexOf(view, target) : -1;
            if (i >= 0)
            {
                float d = Flat(at, view.StateAt(i).Motor.Position);
                int k = (int)MathF.Floor(UMath.Max(0f, d - fists.Range + 0.3f) / (body.SprintSpeed * Slot * Dt));
                if (k < allowed.Length)
                    allowed[k] = (ushort)(1 << (int)Maneuver.Strike);

                // Mostly slide in: the attacker's speed adds to the knock.
                if (k >= 1 && k < allowed.Length && BotRandom.Unit(ref rng) < 0.6f)
                {
                    allowed[k - 1] = (ushort)(1 << (int)Maneuver.Slide);
                    if (k >= 2)
                        allowed[k - 2] = (ushort)(1 << (int)Maneuver.Sprint);
                }
            }

            plan = wfc.Collapse(allowed, running, Weights[(int)intent], ref rng);
            planStart = u;
        }

        /// <summary>The maneuver of this tick as keys: where to go, where to look, what to press.</summary>
        MoveInput Steer(int u, WorldSnapshot view, in BodyState me, Vector3 at, Vector3 feet, Maneuver m,
                        bool slotStart, bool airborne)
        {
            var input = new MoveInput();

            // The next corner more than a step away; a link starts at its corner.
            Vector3 toward = goal;
            LinkKind link = LinkKind.None;
            float linkDistance = float.MaxValue;
            for (int c = 1; c < corners.Count; c++)
            {
                if (corners[c - 1].Link != LinkKind.None && Flat(feet, corners[c - 1].Position) < 1f)
                {
                    link = corners[c - 1].Link;
                    linkDistance = Flat(feet, corners[c - 1].Position);
                }
                if (Flat(feet, corners[c].Position) > 0.4f || c == corners.Count - 1)
                {
                    toward = corners[c].Position;
                    if (link == LinkKind.None && corners[c].Link != LinkKind.None)
                    {
                        link = corners[c].Link;
                        linkDistance = Flat(feet, corners[c].Position);
                    }
                    break;
                }
            }

            Vector3 way = new Vector3(toward.X - feet.X, 0f, toward.Z - feet.Z);
            float distance = UMath.Magnitude(way);
            bool arrived = distance < 0.4f && Flat(feet, goal) < 0.6f;
            float moveYaw = distance > 1e-3f ? UMath.Atan2(-way.X, -way.Z) * (180f / MathF.PI) : yaw;

            // Facing: the target when it is close, else the way.
            int ti = target != 0 ? IndexOf(view, target) : -1;
            float faceYaw = moveYaw;
            float wantPitch = 0f;
            float targetDistance = float.MaxValue;
            if (ti >= 0)
            {
                BodyState t = view.StateAt(ti);
                Vector3 centre = Ahead(t.Motor.BodyCenter, t.Motor.Velocity, Reaction * Dt);
                Vector3 eye = Ahead(me.Motor.EyePosition, me.Motor.Velocity, Reaction * Dt);
                Vector3 d = UMath.Sub(centre, eye);
                targetDistance = UMath.Sqrt(d.X * d.X + d.Z * d.Z);
                if (targetDistance < 6f || intent == BotIntent.Attack && arrived)
                {
                    faceYaw = UMath.Atan2(-d.X, -d.Z) * (180f / MathF.PI);
                    wantPitch = UMath.Atan2(d.Y, targetDistance) * (180f / MathF.PI);
                }
            }
            else if (arrived && intent == BotIntent.Idle)
                faceYaw = yaw;

            // A hand on a mouse: fast across a wide angle, slowing down into it.
            float turn = MathF.IEEERemainder(faceYaw - yaw, 360f);
            yaw = MathF.IEEERemainder(yaw + UMath.Clamp(turn * Ease, -settings.TurnRate, settings.TurnRate), 360f);
            pitch += UMath.Clamp((UMath.Clamp(wantPitch, -60f, 60f) - pitch) * Ease, -settings.TurnRate, settings.TurnRate);
            input.Yaw = yaw;
            input.Pitch = pitch;

            // Where to go, in the frame of the facing: x right, y forward.
            Vector2 local = Vector2.Zero;
            if (!arrived)
            {
                float rel = (moveYaw - yaw) * (MathF.PI / 180f);
                local = new Vector2(-UMath.Sin(rel), UMath.Cos(rel));
            }

            switch (m)
            {
                case Maneuver.Feint:
                    local = Vector2.Zero;
                    break;
                case Maneuver.StrafeLeft:
                case Maneuver.StrafeRight:
                    float side = m == Maneuver.StrafeLeft ? -1f : 1f;
                    local = UMath.ClampMagnitude(new Vector2(local.X + side, local.Y * 0.6f), 1f);
                    break;
                case Maneuver.Sprint:
                    input.Sprint = true;
                    break;
                case Maneuver.Slide:
                    // Tapped once the run is fast enough; in the air the held
                    // key does it, a sprint lands into a slide. Never a long
                    // crouch: a slide that did not start would only crawl.
                    input.Sprint = true;
                    if (slotStart)
                    {
                        slideTap = -1;
                        landSlide = airborne;
                    }
                    float run = UMath.Magnitude(new Vector3(me.Motor.Velocity.X, 0f, me.Motor.Velocity.Z));
                    if (airborne)
                        input.Crouch = true;
                    else if (slideTap < 0 && run > body.WalkSpeed + 0.3f)
                    {
                        input.CrouchPressed = input.Crouch = true;
                        slideTap = u;
                    }
                    input.Crouch |= slideTap >= 0 && u - slideTap < 3;
                    break;
                case Maneuver.Crouch:
                    input.Crouch = true;
                    break;
                case Maneuver.Jump:
                    input.Sprint = true;
                    if (slotStart && !airborne)
                        Jump(u, ref input, me.Motor.Sliding);
                    break;
                case Maneuver.DoubleJump:
                    if (doubleJumpAt < u && u - lastJump < 20)
                        doubleJumpAt = Math.Max(u, lastJump + 13);
                    break;
                case Maneuver.Strike:
                    break;
            }

            // The path comes first: a ledge to jump onto is jumped whatever the plan says.
            if ((link == LinkKind.Jump || link == LinkKind.DoubleJump) && linkDistance < 0.9f && !airborne)
            {
                Jump(u, ref input, me.Motor.Sliding);
                if (link == LinkKind.DoubleJump)
                    doubleJumpAt = u + 13;
            }

            // Stuck against something the mesh does not know: hop.
            float speed = UMath.Magnitude(new Vector3(me.Motor.Velocity.X, 0f, me.Motor.Velocity.Z));
            stuckTicks = !arrived && UMath.SqrMagnitude(local) > 0.25f && speed < 0.5f ? stuckTicks + 1 : 0;
            if (stuckTicks > 30 && !airborne)
            {
                Jump(u, ref input, me.Motor.Sliding);
                stuckTicks = 0;
            }

            // A slide planned in the air waits for the landing with the key
            // held, unless a double jump is still due: crouched, it would not fire.
            if (landSlide && airborne && doubleJumpAt < u)
            {
                input.Crouch = true;
                input.Sprint = true;
            }
            else if (!airborne)
                landSlide = false;

            if (u == doubleJumpAt)
                input.JumpPresses = 2;
            if (u < jumpHeldUntil)
                input.JumpHeld = true;

            // A swing only on a strike slot and only with the target in reach.
            if (m == Maneuver.Strike && ti >= 0 && targetDistance < fists.Range - 0.2f &&
                u >= lastStrike + fists.CooldownTicks)
            {
                input.Yaw = yaw + BotRandom.Signed(ref rng, settings.AimError);
                input.Pitch = UMath.Clamp(pitch + BotRandom.Signed(ref rng, settings.AimError), -89f, 89f);
                input.AttackPresses = 1;
                lastStrike = u;
            }

            // Keys, not a stick: each axis pressed or not, as on WASD.
            input.Move = new Vector2(Key(local.X), Key(local.Y));
            return input;
        }

        static float Key(float axis) => axis > 0.38f ? 1f : axis < -0.38f ? -1f : 0f;

        // Out of a slide the first press only ends it; the second jumps.
        void Jump(int u, ref MoveInput input, bool sliding)
        {
            if (u - lastJump < 20)
                return;
            input.JumpPresses = (byte)(sliding ? 2 : 1);
            input.JumpHeld = true;
            jumpHeldUntil = u + 8;
            lastJump = u;
        }
    }
}
