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
        readonly int route;          // the route variant this bot would like, before checking it
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
        Vector3 lastSeen;            // own position in the last view, to notice being put elsewhere
        Vector3 jamAt;               // where it hangs off the ground without moving, and how long
        int jamTicks;
        float yaw, pitch;
        float yawRate, pitchRate;    // the hand on the mouse is still moving
        float eyeAbove = 1.6f;       // eye over the feet, as last seen
        Vector2 walkKeys;            // the keys held while walking
        int jumpHeldUntil = -1, doubleJumpAt = -1, lastJump = -1000, lastStrike = -1000;
        int stuckTicks;
        int slideTap = -1;
        bool landSlide;
        Vector3 spot;                // where in the zone this bot stands
        bool hasSpot;
        Vector3 goal;
        int variant = -1;            // the route variant it takes, chosen at its first way to the zone
        bool climbing;               // on the way up to the zone: the pilot steers
        bool farBelow;               // and still far below it: no fights
        LinkKind flight;             // the jump under way while climbing, None on the ground
        Vector3 landing;
        readonly JumpPilot pilot;
        JumpPilot.Hop hop;           // the jump being flown or prepared
        JumpPilot.Attempt attempt;
        bool hopping;
        int hopStart, hopSeen = int.MinValue / 2, verifyAt = -1;
        int fromFloor, toFloor;      // the floors of that jump
        Vector3 landed;              // the last jump's, checked once the bot sees itself down
        int landedFrom, landedTo;
        float landedTime;
        readonly MoveInput[] past = new MoveInput[64];
        readonly int[] pastTick = new int[64];
        readonly ClimbGrid.Memory memory = new ClimbGrid.Memory();
        float bestTime = float.MaxValue;
        int bestTick;

        public BotBrain(int netId, uint seed, int route, BotNavigation nav, BotSettings settings, HillMatch match,
                        PlayerMotor body, MeleeAttack fists)
        {
            this.netId = netId;
            this.route = (route % 1024 + 1024) % 1024;
            rng = seed;
            this.nav = nav;
            this.settings = settings;
            this.match = match;
            this.body = body;
            this.fists = fists;
            wfc = new ManeuverWfc(Math.Max(2, settings.SlotsPerPlan), Math.Max(1, settings.SlotTicks), fists.CooldownTicks);
            allowed = new ushort[wfc.Slots];
            pilot = new JumpPilot(nav.Grid, body);
        }

        public int NetId => netId;
        public BotIntent Intent => intent;
        public int Target => target;
        public Vector3 Goal => goal;
        public Maneuver Running => running;
        public int Variant => variant;
        public bool Climbing => climbing;
        /// <summary>Near a jump or in it, for probes.</summary>
        public bool Hopping => hopping || flight != LinkKind.None;
        public LinkKind Flight => flight;
        public Vector3 Landing => landing;
        public int Lessons => memory.Lessons;

        /// <summary>Jumps climbing so far, those that landed where they should, and those given up before take-off; for probes.</summary>
        public int Hops, HopsLanded, HopsGivenUp;

        /// <summary>Told of every jump climbing and how it went; for probes.</summary>
        public Action<string> HopLog;

        /// <summary>Where the bot thought it was in the last tick it climbed; for probes.</summary>
        public JumpPilot.Body Predicted;
        public IReadOnlyList<Corner> Corners => corners;

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
            // Put somewhere new by the round (a respawn, the next round): start the way over.
            if (started && Flat(me.Motor.Position, lastSeen) > 5f)
            {
                started = false;
                variant = -1;
                corners.Clear();
                flight = LinkKind.None;
                hopping = false;
                verifyAt = -1;
                bestTime = float.MaxValue;
            }
            lastSeen = me.Motor.Position;
            if (!started)
            {
                started = true;
                yaw = UMath.YawOf(me.Motor.Rotation);
                nextThink = u;
                bestTick = u;
            }

            // Everything seen is Reaction ticks old; carried forward on its speed.
            float ahead = Reaction * Dt;
            Vector3 at = Ahead(me.Motor.Position, me.Motor.Velocity, ahead);
            Vector3 feet = new Vector3(at.X, at.Y - body.StandHeight * 0.5f, at.Z);
            // A hit's knock, not the brief soft control after a bump.
            bool knocked = UMath.Magnitude(me.Motor.Knock) > 0.5f;
            bool airborne = !me.Motor.Grounded || u - lastJump < 20;
            MatchPhase phase = match != null ? view.Match.Phase : MatchPhase.Running;

            // The round has just begun: stand a moment before setting off
            // (Luca's wish). What is seen is Reaction ticks old, the round is not.
            if (match != null && phase == MatchPhase.Running &&
                view.Match.RoundTicks + Reaction < (int)MathF.Round(settings.StartWait / Dt))
                return Remember(u, new MoveInput { Yaw = yaw, Pitch = pitch });

            if (u >= nextThink)
            {
                nextThink = u + Math.Max(1, settings.ThinkTicks);
                BotIntent was = intent;
                int wasTarget = target;
                Think(view, self, at, phase, knocked);
                // Up the hill along the own route; inside the zone to the own spot.
                bool toZone = match != null && intent != BotIntent.Attack && intent != BotIntent.Idle && !match.Contains(at);
                if (toZone && variant < 0)
                    variant = nav.ChooseVariant(feet, route, settings.RouteDetour);
                if (toZone)
                    Progress(u, feet);
                if (!(toZone ? nav.PlanToZone(feet, variant, memory, corners) : nav.Plan(feet, goal, corners)))
                    corners.Clear();
                climbing = toZone;
                farBelow = toZone && match.Center.Y - match.HalfSize.Y - feet.Y > ClimbBelow;
                if (intent != was || target != wasTarget || u - planStart >= plan.Length * Slot || (knocked && intent != BotIntent.Recover))
                    Replan(u, view, at, airborne);
            }
            if (u - planStart >= plan.Length * Slot)
                Replan(u, view, at, airborne);

            if (climbing)
            {
                running = Maneuver.Sprint;
                return Remember(u, Passing(u, view, self, me, phase, Climb(u, me)));
            }
            flight = LinkKind.None;
            hopping = false;

            int slot = (u - planStart) / Slot;
            Maneuver m = plan[Math.Min(slot, plan.Length - 1)];
            bool slotStart = (u - planStart) % Slot == 0;
            if (slotStart)
                running = m;

            return Remember(u, Steer(u, view, me, at, feet, m, slotStart, airborne));
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
            // Far below the zone a bot climbs and chases nobody (Luca's rule).
            bool below = match != null && !inZone && match.Center.Y - match.HalfSize.Y - Feet(at).Y > ClimbBelow;
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
                // Only a running round is fought over: no hits in the lobby,
                // the countdown or after a win.
                if (d > settings.EngageRange || !live || below)
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

        // Seconds a failed jump adds to it, and that a floor without progress adds to its way.
        const float FailedJump = 3f;
        const float NoProgress = 2f;
        const int ProgressTicks = 160;

        /// <summary>
        /// Learns on the way up, besides what each search teaches: a floor
        /// counts as further away when the time to the zone has not dropped
        /// for a while.
        /// </summary>
        void Progress(int u, Vector3 feet)
        {
            float left = nav.Grid.TimeToZone(feet, variant);
            if (left < bestTime - 0.5f)
            {
                bestTime = left;
                bestTick = u;
            }
            else if (u - bestTick > ProgressTicks)
            {
                nav.Grid.Stuck(feet, NoProgress, memory);
                bestTime = left;
                bestTick = u;
            }
        }

        /// <summary>The keys of the last tick, for probes.</summary>
        public MoveInput LastInput { get; private set; }

        MoveInput Remember(int u, MoveInput input)
        {
            LastInput = input;
            past[u % past.Length] = input;
            pastTick[u % past.Length] = u;
            return input;
        }

        // Further below the zone than this, a bot climbing does not fight.
        const float ClimbBelow = 15f;

        // A hop is flown from this far before its take-off corner; given up after so many ticks on the ground.
        const float RunUp = 2f;
        const int HopPatience = 120;

        /// <summary>
        /// Where the own body is now: the seen state carried forward over the
        /// reaction time with the bot's own keys since, in the pilot's model
        /// of the motor, as a body knows its own steps.
        /// </summary>
        JumpPilot.Body Predict(int u, in MotorState m)
        {
            JumpPilot.Body b = pilot.From(m, body.StandHeight);
            for (int k = u - Reaction; k < u; k++)
                if (k >= 0 && pastTick[k % past.Length] == k)
                    pilot.Step(ref b, past[k % past.Length]);
            return b;
        }

        /// <summary>
        /// Climbing: the way and nothing else. Walks from corner to corner;
        /// a jump on the way is the pilot's (JumpPilot): it takes off when
        /// its model lands the jump on the top and steers the flight there.
        /// A jump that does not end up there costs this bot more next time.
        /// </summary>
        MoveInput Climb(int u, in BodyState me)
        {
            // Jammed off the ground (a body can wedge between steep faces and
            // cannot jump there): wiggle, a quarter turn every half second.
            bool jammed = !me.Motor.Grounded && Flat(me.Motor.Position, jamAt) < 0.05f && UMath.Abs(me.Motor.Position.Y - jamAt.Y) < 0.05f;
            jamTicks = jammed ? jamTicks + 1 : 0;
            if (!jammed)
                jamAt = me.Motor.Position;
            if (jamTicks > 40)
            {
                flight = LinkKind.None;
                hopping = false;
                return new MoveInput { Yaw = yaw + 90f * ((jamTicks - 40) / 20), Pitch = pitch, Move = new Vector2(0f, 1f) };
            }
            JumpPilot.Body b = Predict(u, me.Motor);
            var feet = new Vector3(b.X, b.Y, b.Z);
            Predicted = b;
            eyeAbove = me.Motor.EyePosition.Y - Feet(me.Motor.Position).Y;
            if (verifyAt >= 0 && u >= verifyAt && (me.Motor.Grounded || u >= verifyAt + 40))
                Verify(u, Feet(me.Motor.Position));

            if (flight != LinkKind.None)
            {
                if (b.Grounded && u - lastJump > 3 || u - lastJump > 60)
                {
                    flight = LinkKind.None;
                    hopping = false;
                    verifyAt = u + Reaction + 1;
                    landed = landing;
                    landedFrom = fromFloor;
                    landedTo = toFloor;
                    landedTime = nav.Grid.TimeToZone(landing, variant);
                }
                else
                {
                    LookAt(u, b, landing, UMath.Atan2(-hop.DX, -hop.DZ) * (180f / MathF.PI));
                    MoveInput flying = pilot.Drive(hop, b, yaw, ref attempt);
                    flying.Pitch = pitch;
                    return flying;
                }
            }

            for (int c = 1; c + 1 < corners.Count; c++)
            {
                Corner k = corners[c];
                if (k.Link != LinkKind.Jump && k.Link != LinkKind.DoubleJump)
                    continue;
                if (Flat(feet, k.Position) > RunUp)
                    break;
                Corner land = corners[c + 1];
                if (!hopping || fromFloor != k.Floor || toFloor != land.Floor)
                {
                    fromFloor = k.Floor;
                    toFloor = land.Floor;
                    Vector3 was = hop.From;
                    hopping = pilot.Prepare(k.Position, land.Position, out hop);
                    attempt = default;
                    // Swapping between take-offs side by side without a jump is
                    // the same try: its patience goes on, or it never runs out.
                    if (!(u - hopSeen <= 40 && lastJump < hopStart && Flat(was, k.Position) < 1f))
                        hopStart = u;
                    if (!hopping)
                    {
                        nav.Grid.Punish(fromFloor, toFloor, FailedJump * 3f, memory);
                        break;
                    }
                }
                if (u - hopStart > HopPatience)
                {
                    // No take-off that lands: let the way go round it.
                    HopsGivenUp++;
                    nav.Grid.Punish(fromFloor, toFloor, FailedJump, memory);
                    hopping = false;
                    hopStart = u;
                    break;
                }
                hopSeen = u;
                LookAt(u, b, land.Position, UMath.Atan2(-hop.DX, -hop.DZ) * (180f / MathF.PI));
                MoveInput input = pilot.Drive(hop, b, yaw, ref attempt);
                input.Pitch = pitch;
                if (input.JumpPresses == 1)
                {
                    // A jump still unchecked is judged by where this one starts.
                    if (verifyAt >= 0)
                        Verify(u, feet);
                    lastJump = u;
                    Hops++;
                    HopLog?.Invoke(FormattableString.Invariant(
                        $"{u} Bot {netId}: jump {(attempt.Twice ? "double" : "single")} at {b.X:F2} {b.Y:F2} {b.Z:F2} v {b.VX:F1} {b.VZ:F1}, from {hop.From.X:F2} {hop.From.Y:F2} {hop.From.Z:F2} direction {hop.DX:F2} {hop.DZ:F2}, edge {hop.Edge:F1} lip {hop.Lip:F1} target {hop.Target:F1} end {hop.End:F1} rise {hop.Rise:F2} need {hop.Need:F2}"));
                    flight = k.Link;
                    landing = land.Position;
                }
                return input;
            }
            return Walk(u, me, feet);
        }

        // Whether the last jump came down where it should; a miss costs it more next time.
        void Verify(int u, Vector3 seen)
        {
            // Down on the top, or anywhere at least as close to the zone.
            bool missed = seen.Y < landed.Y - 0.3f ||
                          Flat(seen, landed) > 0.8f && nav.Grid.TimeToZone(seen, variant) > landedTime + 0.5f;
            if (missed)
                nav.Grid.Punish(landedFrom, landedTo, FailedJump, memory);
            HopsLanded += missed ? 0 : 1;
            HopLog?.Invoke(FormattableString.Invariant(
                $"{u} Bot {netId}: {(missed ? "MISSED" : "good")} sees {seen.X:F2} {seen.Y:F2} {seen.Z:F2}, target {landed.X:F2} {landed.Y:F2} {landed.Z:F2}"));
            verifyAt = -1;
        }

        /// <summary>
        /// Climbing far below the zone a bot swings only at whoever is right
        /// in front of it, within reach and at most 60° off its view: a flick
        /// for that one tick, its way and its view stay as they are.
        /// </summary>
        MoveInput Passing(int u, WorldSnapshot view, int self, in BodyState me, MatchPhase phase, MoveInput input)
        {
            if (!farBelow || phase != MatchPhase.Running || input.JumpPresses > 0 || u < lastStrike + fists.CooldownTicks)
                return input;
            Vector3 eye = Ahead(me.Motor.EyePosition, me.Motor.Velocity, Reaction * Dt);
            float best = fists.Range - 0.2f;
            int pick = -1;
            Vector3 aim = Vector3.Zero;
            for (int i = 0; i < view.Count; i++)
            {
                if (i == self || view.BodyAt(i).NetId == 0)
                    continue;
                BodyState s = view.StateAt(i);
                Vector3 d = UMath.Sub(Ahead(s.Motor.BodyCenter, s.Motor.Velocity, Reaction * Dt), eye);
                float flat = UMath.Sqrt(d.X * d.X + d.Z * d.Z);
                float off = MathF.IEEERemainder(UMath.Atan2(-d.X, -d.Z) * (180f / MathF.PI) - yaw, 360f);
                if (flat < best && UMath.Abs(off) < 60f)
                {
                    best = flat;
                    pick = i;
                    aim = d;
                }
            }
            if (pick < 0)
                return input;
            float flatAim = UMath.Sqrt(aim.X * aim.X + aim.Z * aim.Z);
            input.Yaw = UMath.Atan2(-aim.X, -aim.Z) * (180f / MathF.PI) + BotRandom.Signed(ref rng, settings.AimError);
            input.Pitch = UMath.Clamp(UMath.Atan2(aim.Y, flatAim) * (180f / MathF.PI) + BotRandom.Signed(ref rng, settings.AimError), -89f, 89f);
            input.AttackPresses = 1;
            lastStrike = u;
            return input;
        }

        // A yaw to turn to, the eyes on a spot: up at a top, down at the way.
        void LookAt(int u, in JumpPilot.Body b, Vector3 spot, float wantYaw)
        {
            float dx = spot.X - b.X, dz = spot.Z - b.Z;
            float far = UMath.Max(UMath.Sqrt(dx * dx + dz * dz), 2f);
            float wantPitch = UMath.Atan2(spot.Y + 0.3f - (b.Y + eyeAbove), far) * (180f / MathF.PI);
            Look(u, wantYaw, UMath.Clamp(wantPitch, -35f, 35f), settings.TurnRate);
        }

        // How fast the hand takes up a new turn rate, per tick.
        const float Grip = 0.6f;

        /// <summary>
        /// The view as a hand moves a mouse: it speeds up into a turn, slows
        /// into the target, overshoots a little and never rests perfectly
        /// still (a slow wander of a degree or two). At most maxRate a tick.
        /// </summary>
        void Look(int u, float wantYaw, float wantPitch, float maxRate)
        {
            double t = u * Dt, phase = netId * 1.618;
            float wanderYaw = (float)(1.2 * DetMath.Sin(t * 0.9 + phase) + 0.6 * DetMath.Sin(t * 2.3 + phase * 1.7));
            float wanderPitch = (float)(1.0 * DetMath.Sin(t * 0.7 + phase * 2.1) + 0.5 * DetMath.Sin(t * 1.9 + phase * 0.6));
            float turn = MathF.IEEERemainder(wantYaw + wanderYaw - yaw, 360f);
            yawRate += (UMath.Clamp(turn * Ease, -maxRate, maxRate) - yawRate) * Grip;
            yaw = MathF.IEEERemainder(yaw + yawRate, 360f);
            float nod = UMath.Clamp(wantPitch + wanderPitch, -60f, 60f) - pitch;
            pitchRate += (UMath.Clamp(nod * Ease, -maxRate, maxRate) - pitchRate) * Grip;
            pitch += pitchRate;
        }

        // How far ahead on the way a walking bot aims, metres.
        const float Pursuit = 1.8f;

        // Between jumps: towards a point a little ahead on the way, not from
        // corner to corner, sprinting where it is far.
        MoveInput Walk(int u, in BodyState me, Vector3 feet)
        {
            Vector3 toward = PursuitPoint(feet, out float open);
            var input = new MoveInput { Sprint = open > 2f };
            Vector3 way = new Vector3(toward.X - feet.X, 0f, toward.Z - feet.Z);
            float distance = UMath.Magnitude(way);
            float moveYaw = distance > 1e-3f ? UMath.Atan2(-way.X, -way.Z) * (180f / MathF.PI) : yaw;
            var b = new JumpPilot.Body { X = feet.X, Y = feet.Y, Z = feet.Z };
            LookAt(u, b, new Vector3(toward.X, toward.Y - 0.3f, toward.Z), moveYaw);
            input.Yaw = yaw;
            input.Pitch = pitch;
            walkKeys = JumpPilot.Keys(moveYaw - yaw, distance > 0.15f, walkKeys);

            // Stuck against something the grid does not know: hop.
            float speed = UMath.Magnitude(new Vector3(me.Motor.Velocity.X, 0f, me.Motor.Velocity.Z));
            stuckTicks = distance > 0.3f && speed < 0.5f ? stuckTicks + 1 : 0;
            if (stuckTicks > 30 && me.Motor.Grounded && u - lastJump >= 20)
            {
                Jump(u, ref input, me.Motor.Sliding);
                stuckTicks = 0;
            }
            if (u < jumpHeldUntil)
                input.JumpHeld = true;
            input.Move = walkKeys;
            return input;
        }

        // The point Pursuit metres on along the corners from the feet, never
        // past a corner where a jump starts; open is the way to that corner.
        Vector3 PursuitPoint(Vector3 feet, out float open)
        {
            open = 0f;
            Vector3 at = feet;
            for (int c = 1; c < corners.Count; c++)
            {
                open += Flat(at, corners[c].Position);
                at = corners[c].Position;
                if (Takeoff(corners[c].Link))
                    break;
            }
            at = feet;
            float left = Pursuit;
            for (int c = 1; c < corners.Count; c++)
            {
                Vector3 next = corners[c].Position;
                float d = Flat(at, next);
                if (d >= left)
                    return new Vector3(at.X + (next.X - at.X) * left / d, next.Y, at.Z + (next.Z - at.Z) * left / d);
                left -= d;
                at = next;
                if (Takeoff(corners[c].Link))
                    break;
            }
            return corners.Count > 1 ? at : goal;
        }

        // A link the pilot jumps; a drop is walked off.
        static bool Takeoff(LinkKind link) => link == LinkKind.Jump || link == LinkKind.DoubleJump;

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

            // A swing only on a strike slot, with the target in reach, while the
            // round runs (a plan from before the lobby may still hold one).
            bool live = match == null || view.Match.Phase == MatchPhase.Running;
            if (m == Maneuver.Strike && live && ti >= 0 && targetDistance < fists.Range - 0.2f &&
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
