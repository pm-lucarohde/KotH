using System.Collections.Generic;
using KotH.Shared.Mathematics;
using KotH.Shared.Physics;
using Stride.BepuPhysics;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.Shared.Player
{
    /// <summary>
    /// Moves the player. Reads no input and knows no camera - a dedicated
    /// server has neither. All mutable state lives in the private fields
    /// below and nowhere else, which is what lets a tick be replayed later.
    /// Collision goes through CapsuleSweep and plain physics queries.
    /// </summary>
    [DataContract("KotH.PlayerMotor")]
    [Display("Spieler-Motor")]
    [ComponentCategory("KotH")]
    public sealed class PlayerMotor : EntityComponent
    {
        /// <userdoc>Gehtempo in m/s.</userdoc>
        [Display("Gehen", "Tempo (m/s)")] public float WalkSpeed = 5.175f;
        [Display("Sprinten", "Tempo (m/s)")] public float SprintSpeed = 8.625f;
        [Display("Ducken", "Tempo (m/s)")] public float CrouchSpeed = 2.1f;

        [Display("Beschleunigen am Boden", "Trägheit (m/s²)")] public float GroundAcceleration = 50f;
        [Display("Bremsen am Boden", "Trägheit (m/s²)")] public float GroundDeceleration = 35f;

        // 15% of ground acceleration: enough to correct mid-jump, not enough
        // to reverse direction within a normal flight time.
        [Display("Beschleunigen in der Luft", "Trägheit (m/s²)")] public float AirAcceleration = 7.5f;

        // All three carry a pre-correction: integrating at 40 Hz loses
        // v*dt/2 of every jump, so a plain 1.5 would peak at 1.405 m. These
        // numbers are chosen so the REAL peaks come out at 1.50, 1.00 and
        // 1.00 m. Retuning one means measuring it again, see JumpHeightProbe.
        [Display("Sprunghöhe", "Sprung und Schwerkraft")] public float JumpHeight = 1.6f;
        [Display("Doppelsprunghöhe", "Sprung und Schwerkraft")] public float DoubleJumpHeight = 1.0822f;

        /// <userdoc>Sprunghöhe bei ganz kurzem Antippen</userdoc>
        [Display("Mindestsprunghöhe", "Sprung und Schwerkraft")] public float MinJumpHeight = 1.0744f;

        /// <userdoc>Haltedauer, bis zu der nur die Mindesthöhe kommt</userdoc>
        [Display("Mindesthaltedauer", "Sprung und Schwerkraft")] public float MinJumpHold = 0.032f;

        /// <userdoc>Haltedauer für die volle Sprunghöhe</userdoc>
        [Display("Volle Haltedauer", "Sprung und Schwerkraft")] public float FullJumpHold = 0.2f;

        // Opens on the ground jump. A double tap only counts inside this time.
        [Display("Doppelsprung-Fenster", "Sprung und Schwerkraft")] public float DoubleJumpWindow = 1f;

        // Maximum gap between the two taps of the double tap.
        [Display("Doppeltipp-Abstand", "Sprung und Schwerkraft")] public float DoubleTapWindow = 0.25f;

        // Deliberately about twice real gravity; at -9.81 a jump feels floaty.
        [Display("Schwerkraft", "Sprung und Schwerkraft")] public float Gravity = -20f;

        // Eyes sit 0.12 m below the crown in both poses, as on a real body.
        [Display("Radius", "Körpermaße (m)")] public float BodyRadius = 0.4f;
        [Display("Hautdicke", "Körpermaße (m)")] public float SkinWidth = 0.04f;
        [Display("Höhe stehend", "Körpermaße (m)")] public float StandHeight = 1.8f;
        [Display("Höhe geduckt", "Körpermaße (m)")] public float CrouchHeight = 0.9f;
        [Display("Augenhöhe stehend", "Körpermaße (m)")] public float StandEyeHeight = 1.68f;
        [Display("Augenhöhe geduckt", "Körpermaße (m)")] public float CrouchEyeHeight = 0.78f;

        // Drives collision height and eye together, in m/s of height: the
        // 0.90 m between the poses take 0.15 s. One rate, because two would
        // let the eye leave the body it belongs to.
        [Display("Posenwechsel", "Körpermaße (m)")] public float PoseChangeSpeed = 6f;

        // Step height the body climbs. Crouched it is much lower, so
        // ducking becomes a real trade: you fit through gaps but lose reach.
        [Display("Stufe stehend", "Körpermaße (m)")] public float StandStepOffset = 0.45f;
        [Display("Stufe geduckt", "Körpermaße (m)")] public float CrouchStepOffset = 0.25f;

        // Crouching guards against falling: a step that would leave nothing
        // walkable this far below the feet is refused. Deliberately larger
        // than GroundSnapDistance, so a drop is either caught or refused.
        [Display("Kantenschutz", "Körpermaße (m)")] public float LedgeGuardDrop = 0.5f;
        [Display("Kopffreiheit-Maske", "Körpermaße (m)")] public CollisionMask HeadroomMask = CollisionMask.Everything;

        /// <userdoc>Steilste Fläche, die noch begehbar ist</userdoc>
        [Display("Hanglimit (°)", "Hang")] public float SlopeLimit = 50f;
        [Display("Boden-Maske", "Hang")] public CollisionMask GroundMask = CollisionMask.Everything;

        // How far below the feet the probe still looks for ground.
        [Display("Bodensonde extra", "Hang")] public float GroundProbeExtra = 0.1f;

        // How far the player is pulled back down onto ground it ran off.
        // Covers a sprint down the steepest walkable ramp; much more would
        // glue the player to the floor at a real ledge.
        [Display("Bodenhaftung", "Hang")] public float GroundSnapDistance = 0.3f;

        // Speed the slide starts at, so stepping onto a steep face slides
        // at once instead of accelerating out of a standstill.
        [Display("Mindestrutschtempo", "Hang")] public float MinSlideSpeed = 3f;

        // A stair edge can report a steep normal for a tick or two. Sliding
        // only starts once the reading holds, so an edge cannot shove the
        // player back down the step.
        [Display("Rutsch-Bestätigung", "Hang")] public float SlideConfirmTime = 0.075f;

        // How far past the blocking wall the ledge is measured.
        [Display("Stufensonde voraus", "Hang")] public float StepProbeAhead = 0.05f;

        /// <userdoc>Reibung beim Gleiten, deutlich kleiner als beim Bremsen</userdoc>
        [Display("Gleitreibung", "Gleiten")] public float SlideDeceleration = 6f;

        /// <userdoc>Tempo, über das ein Hang das Gleiten nicht hinaustreibt</userdoc>
        [Display("Höchsttempo", "Gleiten")] public float MaxSlideSpeed = 15f;

        /// <userdoc>Abstand zwischen zwei Gleitvorgängen, ab dem Start gezählt</userdoc>
        [Display("Abklingzeit", "Gleiten")] public float SlideCooldown = 0.5f;

        /// <userdoc>Wie viel vom Rückstoß in eine Wand hinein als Abprall zurückkommt; das eigene Laufen prallt nie ab</userdoc>
        [Display("Abprall an Wänden", "Rückstoß")] public float KnockWallBounce = 0.4f;

        // --- wiring, set by Initialize ---
        WorldQueries world;
        StaticComponent hull;      // what a shot may hit, full height
        readonly List<HullPart> hullParts = new List<HullPart>();
        readonly CapsuleSweep sweep = new CapsuleSweep();

        // Bodies never block movement - BodyContact resolves them - so no
        // world query may see one. The body's own hull sits on that layer
        // too, so this skips it as well. An invariant, not a setting.
        CollisionMask WorldMask => GroundMask & ~WorldQueries.HittableMask;
        CollisionMask HeadroomWorldMask => HeadroomMask & ~WorldQueries.HittableMask;

        // Feet relative to the entity. The pose shrinks downward from the
        // standing centre, so this does not move when crouching.
        float FootOffset => -StandHeight * 0.5f;

        // Where the capsule's middle sits above the entity. The shape
        // shrinks downward so the feet stay put, which is also where
        // ApplyHeight puts the hull.
        float CenterY => (currentHeight - StandHeight) * 0.5f;

        // --- mutable state ---
        // The pose the simulation works on. The entity learns it only in
        // Publish, so no body can see another's half finished tick.
        Vector3 position;
        Quaternion rotation = Quaternion.Identity;
        Vector3 velocity;
        bool isCrouching;         // the INTENT, set by the key, not the shape
        bool sprinting;           // the STATE, started by the key, ended by speed
        bool sprintArmed;         // walk speed has been exceeded at least once
        bool sliding;             // crouch slide, the deliberate one
        float slideSpeed;         // what the slide rolls out at, not velocity
        float slideCooldownLeft;  // counted from the START of the last slide
        float lastStepSpeed;      // speed the last tick really made, in 3D
        bool touchingGround;      // any-steepness contact AFTER the move
        float currentHeight;      // the shape, which needs time to follow
        bool grounded;            // standing on WALKABLE ground
        bool onSteepGround;       // confirmed too steep, sliding
        Vector3 groundNormal = UMath.Up;
        float groundAngle;
        float steepTime;          // how long the ground has read too steep
        Vector3 contactNormal;    // most upright contact of the last Move
        bool hasContactNormal;
        float maxStepHeight;      // our own step limit, derived from the height
        bool steppedUp;           // climbed a ledge this tick
        int stepSettleTicks;      // ticks a step-up still counts as standing
        bool wedged;              // last move was refused by the world
        float knockbackTime;      // ground control stays soft this long
        Vector3 knock;            // horizontal knockback still to shed, part of velocity
        bool doubleJumpUsed;      // once per flight
        bool jumpWindowOpen;      // window since the ground jump
        float timeSinceGroundJump;
        bool hasPendingAirPress;  // first air tap waiting for its partner
        float timeSinceAirPress;
        bool jumpCutPending;      // ground jump running, not trimmed yet
        float jumpHeldTime;       // how long the key stayed down since take-off
        float jumpStartY;         // take-off height, the cut measures from it

        // Unity kept this on a camera mount child. Derived from the height
        // like maxStepHeight, so it is not part of the snapshot.
        Vector3 eyeLocal;

        // --- read by the simulation too: contacts and the swing start here ---
        Vector3 eyePosition;
        Vector3 previousEyePosition;
        Vector3 bodyCenter;
        Vector3 previousBodyCenter;

        // Preallocated so the probes allocate nothing per tick.
        readonly CollidableComponent[] headroomHits = new CollidableComponent[8];
        readonly QueryHit[] groundHits = new QueryHit[8];

        public Vector3 EyePosition => eyePosition;
        public Vector3 PreviousEyePosition => previousEyePosition;
        public Vector3 BodyCenter => bodyCenter;
        public Vector3 PreviousBodyCenter => previousBodyCenter;
        public float Height => currentHeight;
        public float Radius => BodyRadius;
        public float MaxStepHeight => maxStepHeight;
        public bool IsGrounded => grounded;

        /// <summary>Speed of the knock still to shed, m/s. Zero once a hit is spent.</summary>
        public float KnockLeft => UMath.Magnitude(knock);

        /// <summary>
        /// Ground of any steepness, walkable or not. IsGrounded is the
        /// walkable part of it; a probe asking about contact needs this one.
        /// </summary>
        public bool IsTouchingGround => touchingGround;
        public bool IsSlidingDownSteep => onSteepGround;
        public float GroundAngle => groundAngle;
        public bool IsCrouching => isCrouching;
        public bool IsSprinting => sprinting;
        public bool IsSliding => sliding;
        public Vector3 Velocity => velocity;

        /// <summary>The simulated pose, which the entity only learns in Publish.</summary>
        public Vector3 Position => position;
        public Quaternion Rotation => rotation;

        /// <summary>The hit hull, null on a body without one.</summary>
        public StaticComponent Hull => hull;

        public WorldQueries World => world;

        /// <summary>
        /// Binds the motor to the world and picks up where the entity was
        /// placed. Unity did this in Awake; here the simulation calls it when
        /// the body joins, once the entity sits in a scene.
        /// </summary>
        public void Initialize(WorldQueries queries)
        {
            world = queries;
            hull = Entity.Get<StaticComponent>();
            hullParts.Clear();

            // The standing shapes as authored; ApplyHeight scales from these
            if (hull?.Collider is CompoundCollider compound)
            {
                for (int i = 0; i < compound.Colliders.Count; i++)
                {
                    if (compound.Colliders[i] is CapsuleCollider capsule)
                        hullParts.Add(new HullPart(capsule, capsule.PositionLocal,
                                                   capsule.Length + 2f * capsule.Radius));
                    else if (compound.Colliders[i] is CylinderCollider cylinder)
                        hullParts.Add(new HullPart(cylinder, cylinder.PositionLocal, cylinder.Length));
                }
            }

            if (hullParts.Count == 0)
                Log.Warning($"Keine Trefferform am Körper {Entity.Name}.");

            position = Entity.Transform.Position;
            rotation = Entity.Transform.Rotation;

            currentHeight = StandHeight;
            ApplyHeight(currentHeight);
            eyeLocal = EyeLocalPosition(StandEyeHeight);

            eyePosition = previousEyePosition = CurrentEyeWorldPosition();
            bodyCenter = previousBodyCenter = CurrentBodyCenter();

            if (hull != null)
                world.Register(hull);

            SyncHull();
        }

        /// <summary>One shape of the hit hull with its standing pose.</summary>
        readonly struct HullPart
        {
            public readonly ColliderBase Shape;
            public readonly Vector3 Position;
            public readonly float Height;   // full height, end caps included

            public HullPart(ColliderBase shape, Vector3 position, float height)
            {
                Shape = shape;
                Position = position;
                Height = height;
            }
        }

        static readonly Stride.Core.Diagnostics.Logger Log =
            Stride.Core.Diagnostics.GlobalLogger.GetLogger("KotH");

        /// <summary>
        /// Adds a push from outside the motor, for instance a body collision.
        /// Softening ground control afterwards is what makes a push visible
        /// at all: at 50 m/s2 a 1.4 m/s push would be regulated away inside
        /// two ticks, leaving nothing to feel.
        /// </summary>
        public void AddVelocity(Vector3 delta, float maxSoftenTime)
        {
            velocity = UMath.Add(velocity, delta);

            // Only as long as the body really needs to come to rest again.
            // A flat cap left anyone in constant contact permanently limp,
            // because every nudge, however small, renewed the full time.
            float leaving = UMath.Abs(UMath.Dot(velocity, UMath.Normalized(delta)));

            knockbackTime = UMath.Max(knockbackTime,
                UMath.Min(maxSoftenTime, leaving / AirAcceleration));
        }

        /// <summary>
        /// Knocks the body away from a hit: without input, on flat ground, it
        /// travels distance horizontally and rises height. carry is speed
        /// added on top along away, in m/s (the attacker's own run). absorb
        /// is the share of the body's own running along away that goes into
        /// the knock as well: running at the attacker weakens it, fleeing
        /// strengthens it. The knock never points back. It is its own share
        /// of the velocity and sheds at the air acceleration no matter what
        /// is pressed; running after the hit adds on top of it, so running
        /// against a knock takes exactly that speed off. Upward the hit adds
        /// to the body's own climb or fall in the air.
        /// </summary>
        public void Knock(Vector3 away, float distance, float height, float carry, float absorb, float dt)
        {
            if (distance <= 0f && height <= 0f)
                return;

            // A hop alone, as at 0 %, leaves the horizontal untouched,
            // an older knock included.
            if (distance > 0f || carry != 0f || absorb > 0f)
                PushAway(away, distance, carry, absorb, dt);

            // Upward the own climb or fall in the air keeps going, the hit
            // adds on top. On the ground it counts as nothing: there it is
            // only the settling speed. A jump in progress is no longer trimmed
            // on release, that would eat the rise of the hit.
            float rising = grounded || touchingGround ? 0f : velocity.Y;
            velocity.Y = rising + LaunchSpeed(UMath.Max(0f, height), -Gravity, dt);
            jumpCutPending = false;
        }

        /// <summary>The horizontal part of Knock; an older knock is replaced.</summary>
        void PushAway(Vector3 away, float distance, float carry, float absorb, float dt)
        {
            Vector3 flat = new Vector3(away.X, 0f, away.Z);
            flat = UMath.SqrMagnitude(flat) < 1e-8f ? Vector3.Zero : UMath.Normalized(flat);

            // The share of the own running along the line moves into the
            // knock; the rest and anything across it keep going.
            float taken = 0f;
            if (absorb > 0f)
            {
                Vector3 own = new Vector3(velocity.X - knock.X, 0f, velocity.Z - knock.Z);
                taken = UMath.Dot(own, flat) * absorb;
                velocity.X -= flat.X * taken;
                velocity.Z -= flat.Z * taken;
            }

            float speed = UMath.Max(0f, LaunchSpeed(UMath.Max(0f, distance), AirAcceleration, dt) + carry + taken);
            Vector3 fresh = UMath.Mul(flat, speed);
            velocity.X += fresh.X - knock.X;
            velocity.Z += fresh.Z - knock.Z;
            knock = fresh;
        }

        /// <summary>
        /// Speed a wall took comes out of the knock first: its part into the
        /// wall is gone, KnockWallBounce of it comes back away from the wall,
        /// its part along the wall goes on. Only shortened, a knock hitting a
        /// wall at an angle kept pointing into it, and once the body landed
        /// it carried the body back to the wall it had come off.
        /// </summary>
        void ClipKnock(Vector3 asked)
        {
            if (UMath.SqrMagnitude(knock) <= 0f)
                return;

            var taken = new Vector3(asked.X - velocity.X, 0f, asked.Z - velocity.Z);
            float depth = UMath.Magnitude(taken);
            if (depth <= 0f)
                return;

            // No more than the wall took: the rest of the knock's push into
            // it was already cancelled by the body's own running away.
            Vector3 into = UMath.Div(taken, depth);
            float hit = UMath.Min(UMath.Dot(knock, into), depth);
            if (hit <= 0f)
                return;

            float back = hit * KnockWallBounce;
            knock = UMath.Sub(knock, UMath.Mul(into, hit + back));
            velocity.X -= into.X * back;
            velocity.Z -= into.Z * back;
        }

        /// <summary>
        /// Start speed that covers distance while braking at decel. The motor
        /// brakes before it moves, so each tick loses decel*dt first; that
        /// costs half a tick of travel, added back here.
        /// </summary>
        public static float LaunchSpeed(float distance, float decel, float dt)
        {
            if (distance <= 0f)
                return 0f;
            float half = decel * dt * 0.5f;
            return half + UMath.Sqrt(half * half + 2f * decel * distance);
        }

        /// <summary>
        /// Puts the body somewhere. Deliberately moves nothing else: velocity
        /// survives, so a caller that wants a clean start has to say so. The
        /// probes rely on that - clearing it here moved their starting points
        /// and changed 84 measured cases out of 252.
        /// </summary>
        public void Teleport(Vector3 to)
        {
            position = to;
            Entity.Transform.Position = to;

            // The sweep queries against the hull in the same tick, so the
            // physics world has to know the new pose now.
            SyncHull();
        }

        /// <summary>The motor as this tick left it, for a later rollback.</summary>
        public MotorState CaptureState()
        {
            return new MotorState
            {
                Position = position,
                Rotation = rotation,
                Velocity = velocity,
                IsCrouching = isCrouching,
                Sprinting = sprinting,
                SprintArmed = sprintArmed,
                Sliding = sliding,
                SlideSpeed = slideSpeed,
                SlideCooldownLeft = slideCooldownLeft,
                LastStepSpeed = lastStepSpeed,
                TouchingGround = touchingGround,
                CurrentHeight = currentHeight,
                Grounded = grounded,
                OnSteepGround = onSteepGround,
                GroundNormal = groundNormal,
                GroundAngle = groundAngle,
                SteepTime = steepTime,
                ContactNormal = contactNormal,
                HasContactNormal = hasContactNormal,
                SteppedUp = steppedUp,
                StepSettleTicks = stepSettleTicks,
                Wedged = wedged,
                KnockbackTime = knockbackTime,
                Knock = knock,
                DoubleJumpUsed = doubleJumpUsed,
                JumpWindowOpen = jumpWindowOpen,
                TimeSinceGroundJump = timeSinceGroundJump,
                HasPendingAirPress = hasPendingAirPress,
                TimeSinceAirPress = timeSinceAirPress,
                JumpCutPending = jumpCutPending,
                JumpHeldTime = jumpHeldTime,
                JumpStartY = jumpStartY,
                EyePosition = eyePosition,
                PreviousEyePosition = previousEyePosition,
                BodyCenter = bodyCenter,
                PreviousBodyCenter = previousBodyCenter,
            };
        }

        /// <summary>
        /// Puts the motor back to a captured tick. The shape and the eye
        /// follow the restored height, and the physics world learns the new
        /// hull pose at once: a swing in the very next tick already asks.
        /// </summary>
        public void RestoreState(in MotorState s)
        {
            position = s.Position;
            rotation = s.Rotation;
            velocity = s.Velocity;
            isCrouching = s.IsCrouching;
            sprinting = s.Sprinting;
            sprintArmed = s.SprintArmed;
            sliding = s.Sliding;
            slideSpeed = s.SlideSpeed;
            slideCooldownLeft = s.SlideCooldownLeft;
            lastStepSpeed = s.LastStepSpeed;
            touchingGround = s.TouchingGround;
            currentHeight = s.CurrentHeight;
            grounded = s.Grounded;
            onSteepGround = s.OnSteepGround;
            groundNormal = s.GroundNormal;
            groundAngle = s.GroundAngle;
            steepTime = s.SteepTime;
            contactNormal = s.ContactNormal;
            hasContactNormal = s.HasContactNormal;
            steppedUp = s.SteppedUp;
            stepSettleTicks = s.StepSettleTicks;
            wedged = s.Wedged;
            knockbackTime = s.KnockbackTime;
            knock = s.Knock;
            doubleJumpUsed = s.DoubleJumpUsed;
            jumpWindowOpen = s.JumpWindowOpen;
            timeSinceGroundJump = s.TimeSinceGroundJump;
            hasPendingAirPress = s.HasPendingAirPress;
            timeSinceAirPress = s.TimeSinceAirPress;
            jumpCutPending = s.JumpCutPending;
            jumpHeldTime = s.JumpHeldTime;
            jumpStartY = s.JumpStartY;
            eyePosition = s.EyePosition;
            previousEyePosition = s.PreviousEyePosition;
            bodyCenter = s.BodyCenter;
            previousBodyCenter = s.PreviousBodyCenter;

            // maxStepHeight and the eye are derived from the height.
            ApplyHeight(currentHeight);
            PlaceEye();
            Publish();
            SyncHull();
        }

        /// <summary>
        /// Shifts the body out of another one, but never into the world:
        /// unchecked, a crowd squeezed bodies into the wedge under a ramp's
        /// foot and they fell through the one-sided ground (a deliberate
        /// change from Unity). Velocity is left alone: being pushed out of
        /// an overlap is a correction, not a hit - the push itself goes
        /// through AddVelocity.
        /// </summary>
        public void Nudge(Vector3 delta)
        {
            Vector3 free = sweep.Clear(world, position, FootOffset, currentHeight,
                                       BodyRadius, SkinWidth, delta, WorldMask);
            position = UMath.Add(position, free);
        }

        /// <summary>
        /// Hands the simulated pose to the entity. The physics world is left
        /// to the caller: SyncHull once all bodies have published.
        /// </summary>
        public void Publish()
        {
            Entity.Transform.Position = position;
            Entity.Transform.Rotation = rotation;
        }

        // Far below the arena, where no swing reaches.
        static readonly Vector3 ParkedHull = new Vector3(0f, -10000f, 0f);

        /// <summary>
        /// Takes the hit hull out of reach while the body is out of the tick,
        /// without unregistering it: its place in the stable query order
        /// has to survive a rollback that brings the body back.
        /// </summary>
        public void ParkHull()
        {
            hull?.Teleport(ParkedHull, Quaternion.Identity);
        }

        /// <summary>
        /// Moves the hit hull to the simulated pose, which Unity's
        /// Physics.SyncTransforms did. Statics only reach the broadphase
        /// when teleported, and nobody steps this physics world.
        /// </summary>
        public void SyncHull()
        {
            if (hull == null)
                return;

            // Teleport puts the shape at pose plus centre of mass, right, but
            // writes the entity back at pose MINUS it: a crouched body, whose
            // capsule sits off-centre, would be drawn up to 0.45 m too high.
            hull.Teleport(position, rotation);
            Publish();
        }

        /// <summary>
        /// Simulate and Publish in one, for a body ticked on its own.
        /// SimulationSystem calls the halves itself, so every body has
        /// computed its tick before the first one becomes visible.
        /// </summary>
        public void Tick(in MoveInput input, float dt)
        {
            Simulate(input, dt);
            Publish();

            // Unity's system synced every transform after each tick, this
            // body's too; a body ticked on its own has nobody else to do it.
            SyncHull();
        }

        /// <summary>
        /// One simulation step, called with a fixed dt (40 Hz) so that client
        /// and server can compute the same step. Reads and writes only the
        /// motor's own state; the entity stays as it was until Publish.
        /// </summary>
        public void Simulate(in MoveInput input, float dt)
        {
            previousEyePosition = eyePosition;
            previousBodyCenter = bodyCenter;

            // Yaw turns the body; pitch belongs to the camera alone, else the
            // player would tilt backwards when looking up.
            rotation = UMath.EulerYaw(input.Yaw);

            // isGrounded only says THAT there is ground, never how steep it
            // is. Too steep counts as not grounded, so jumping, ground
            // acceleration and the double jump reset all fall into place.
            // What the last tick's sweep ended up touching is our own field
            // and therefore part of what a rollback can restore.
            bool touching = touchingGround;
            bool knownGround = false;
            bool wasGrounded = grounded;

            if (touching)
            {
                knownGround = ProbeGround();
            }
            else
            {
                groundNormal = UMath.Up;
                groundAngle = 0f;
            }

            // Ground nothing could read is never flat ground: it means the
            // capsule rests against something too steep to measure. Reading
            // it as flat let the player stand on a cliff face.
            //
            // A steep reading blocks jumping at once, but sliding waits until
            // the reading holds - a stair edge reports steep for a tick or
            // two and would otherwise shove the player back down the step.
            bool steepReading = touching
                && (!knownGround || groundAngle > SlopeLimit);
            steepTime = steepReading ? steepTime + dt : 0f;

            grounded = touching && !steepReading;
            onSteepGround = steepReading && steepTime >= SlideConfirmTime;

            if (grounded)
            {
                if (velocity.Y < 0f)
                    velocity.Y = -2f; // keeps the player pinned on slopes

                doubleJumpUsed = false;
                jumpWindowOpen = false;
                hasPendingAirPress = false;
                jumpCutPending = false;
            }
            else
            {
                if (jumpWindowOpen)
                {
                    timeSinceGroundJump += dt;
                    if (timeSinceGroundJump > DoubleJumpWindow)
                    {
                        jumpWindowOpen = false;
                        hasPendingAirPress = false;
                    }
                }

                if (hasPendingAirPress)
                {
                    timeSinceAirPress += dt;
                    if (timeSinceAirPress > DoubleTapWindow)
                        hasPendingAirPress = false;
                }
            }

            // Before UpdateHorizontal, so it judges the speed this tick
            // started with and the result is used in the same tick.
            UpdateSprint(input.Sprint);

            // After UpdateSprint, which it asks, and before UpdateHorizontal,
            // which reads slideSpeed as its target.
            UpdateSlide(input, grounded && !wasGrounded, dt);

            // A slide crouches on its own, so a tapped one lowers the eyes
            // exactly like a held key does.
            UpdateCrouch(input.Crouch || sliding, dt);
            UpdateHorizontal(input, dt);

            // A jump this tick wins over the slide: otherwise the redirect
            // would project the fresh upward speed straight back into the
            // slope and the double jump would be dead on arrival.
            bool jumped = ProcessJumpPresses(input.JumpPresses);
            UpdateJumpCut(input.JumpHeld, dt);

            if (onSteepGround && !jumped)
                SlideDownSteep(dt);
            else
                velocity.Y += Gravity * dt;

            // After the jump on purpose: a jump leaves the ledge
            // deliberately and must keep its horizontal speed.
            // Not while sliding: the guard is there so crouched creeping
            // does not walk off a ledge, and it zeroes the velocity. A slide
            // is meant to carry over the edge instead of sticking to it.
            // Nor while knocked away: it held a crouched body at every
            // edge, the summit included, against any hit.
            if (isCrouching && !sliding && grounded && !jumped
                && UMath.SqrMagnitude(knock) <= 0f)
                GuardLedge(dt);

            // Cleared here so the contacts collected below belong to this
            // tick's movement, both the step-up lift and the main Move.
            hasContactNormal = false;
            steppedUp = false;

            Vector3 beforeMove = position;

            // A step-up leaves the body a skin above the ledge, and it takes
            // two ticks to count as grounded again. A sprint meets the next
            // riser inside those two ticks, so they may climb as well.
            bool settling = stepSettleTicks > 0;
            if (settling)
                stepSettleTicks--;

            TryStepUp(dt, settling);
            CapsuleSweep.Result moved = MoveBody(UMath.Mul(velocity, dt), grounded);
            touchingGround = moved.hasGround;

            // Before the speed is trimmed: UpdateWedged asks what the tick
            // wanted to cover, and a trimmed speed never shows a block.
            UpdateWedged(beforeMove, dt);

            // Speed a wall refused is gone, not carried. Kept, it fed a
            // sprint that never ends, a body push with invented momentum
            // and a lastStepSpeed that disagreed with velocity. A riser low
            // enough to climb keeps it, see CapsuleSweep.IsRiser.
            Vector3 asked = velocity;
            velocity = moved.velocity;
            ClipKnock(asked);

            // Only ground that was walkable at the start of the tick is worth
            // keeping. A jump clears grounded itself, so it is never undone,
            // and a step just climbed must not be pulled back down either.
            if (grounded && !steppedUp)
                SnapToGround();

            // What the tick really covered, not what it asked for. Measured
            // in 3D, because uphill the horizontal part alone shrinks by cos
            // squared and would drop below walking speed past 39 degrees.
            Vector3 step = UMath.Sub(position, beforeMove);
            lastStepSpeed = UMath.Magnitude(step) / dt;

            if (Jammed())
                EscapeJam();

            eyePosition = CurrentEyeWorldPosition();
            bodyCenter = CurrentBodyCenter();

            // Counted down last, so the tick a push arrived in is softened
            // in full rather than by whatever is left of dt.
            if (knockbackTime > 0f)
                knockbackTime = UMath.Max(0f, knockbackTime - dt);
        }

        // The key starts the sprint, only speed ends it. Holding it rearms
        // every tick, so a held key never lapses; after a single tap the
        // limit takes over. Crouching and a push end it through that rule.
        void UpdateSprint(bool wantsSprint)
        {
            if (wantsSprint)
            {
                sprinting = true;
                sprintArmed = false;
            }

            if (!sprinting)
                return;

            // Measured, not wished for. It is last tick's value, so the
            // sprint ends one tick late, which at 25 ms nobody can feel.
            float flat = lastStepSpeed;

            // The limit may only bite once walk speed was reached: from a
            // standstill the speed starts below it, which would end the
            // sprint in the very tick it began.
            if (!sprintArmed)
                sprintArmed = flat > WalkSpeed;
            else if (flat < WalkSpeed)
                sprinting = false;
        }

        // The crouch slide: a sprint that keeps its momentum while ducking
        // instead of being pulled down to crouch speed. Only speed or the
        // key end it on flat ground; downhill only the key does.
        void UpdateSlide(in MoveInput input, bool landed, float dt)
        {
            // Counted down before the start below, so the tick a slide
            // begins in sets the full time rather than a shortened one.
            if (slideCooldownLeft > 0f)
                slideCooldownLeft = UMath.Max(0f, slideCooldownLeft - dt);

            if (sliding)
            {
                // Too slow, airborne, or on a face the steep slide owns.
                // Letting go of the key does NOT end it -- a tap is enough
                // to start, so there is nothing to release. The jump key
                // cancels it in ProcessJumpPresses, before any of this.
                if (!grounded || onSteepGround || slideSpeed < WalkSpeed)
                {
                    sliding = false;
                    return;
                }

                // Friction always, gravity only along the slope: downhill it
                // adds, uphill it subtracts, on the flat it is zero. Same
                // projection SlideDownSteep uses, so both agree on downhill.
                Vector3 flat = new Vector3(velocity.X, 0f, velocity.Z);
                float push = 0f;
                if (UMath.SqrMagnitude(flat) > 0.0001f)
                {
                    Vector3 slope =
                        UMath.ProjectOnPlane(UMath.Mul(UMath.Up, Gravity), groundNormal);
                    push = UMath.Dot(slope, UMath.Normalized(flat));
                }

                slideSpeed = UMath.Min(MaxSlideSpeed,
                    slideSpeed + (push - SlideDeceleration) * dt);
                return;
            }

            // Out of a sprint only, and only while the body really moves that
            // fast: lastStepSpeed, because a slide out of the run-up would
            // otherwise end in the tick it began.
            // Measured from the start, not the end: a slide that ran its
            // full course is longer than the cooldown and so never waits,
            // while hammering the key does.
            // Landing with the key held slides straight out of a sprint jump.
            // The landing tick's step is mostly the fall, so the flat speed
            // is what has to be fast enough there.
            float flatSpeed = UMath.Magnitude(new Vector2(velocity.X, velocity.Z));
            bool tapped = input.CrouchPressed && lastStepSpeed > WalkSpeed;
            bool landedCrouched = landed && input.Crouch && flatSpeed > WalkSpeed;

            if ((tapped || landedCrouched) && sprinting && grounded && !onSteepGround
                && slideCooldownLeft <= 0f)
            {
                sliding = true;
                slideCooldownLeft = SlideCooldown;
                slideSpeed = flatSpeed;
            }
        }

        void UpdateHorizontal(in MoveInput input, float dt)
        {
            // Without clamping, moving diagonally would be 1.41x faster.
            Vector2 move = UMath.ClampMagnitude(input.Move, 1f);
            bool hasInput = UMath.SqrMagnitude(move) > 0.0001f;

            Vector3 wish = UMath.Add(
                UMath.Mul(UMath.Rotate(rotation, UMath.Right), move.X),
                UMath.Mul(UMath.Rotate(rotation, UMath.Forward), move.Y));

            // A slide rolls out along the direction it has, even with no key
            // down: the usual deceleration to zero would otherwise end it
            // the moment the key is released.
            if (sliding && !hasInput)
            {
                Vector3 rolling = new Vector3(velocity.X, 0f, velocity.Z);
                if (UMath.SqrMagnitude(rolling) > 0.0001f)
                {
                    wish = UMath.Normalized(rolling);
                    hasInput = true;
                }
            }

            // Crouching in the air out of a sprint keeps the sprint's speed:
            // it is the run-up to a slide on landing, not crouched walking.
            float speed = sliding ? slideSpeed
                        : isCrouching && (grounded || !sprinting) ? CrouchSpeed
                        : sprinting ? SprintSpeed
                        : WalkSpeed;

            // No input while airborne means rate 0, so momentum is preserved.
            // Anything else would be a hidden air brake on key release.
            //
            // Right after a push, air control holds for both of them: full
            // ground acceleration would cancel the push within a tick or two.
            float rate = knockbackTime > 0f
                ? AirAcceleration
                : grounded
                    ? (hasInput ? GroundAcceleration : GroundDeceleration)
                    : (hasInput ? AirAcceleration : 0f);

            // The body's own running, without the knock riding on it. The
            // knock sheds on its own, whatever is pressed. Untouched without
            // one: adding a zero flips the sign of a -0 and the bits change.
            bool knocked = UMath.SqrMagnitude(knock) > 0f;
            Vector3 current = knocked
                ? new Vector3(velocity.X - knock.X, 0f, velocity.Z - knock.Z)
                : new Vector3(velocity.X, 0f, velocity.Z);
            Vector3 next = UMath.MoveTowards(current, UMath.Mul(wish, speed), rate * dt);
            if (knocked)
            {
                knock = UMath.MoveTowards(knock, Vector3.Zero, AirAcceleration * dt);
                next = new Vector3(next.X + knock.X, 0f, next.Z + knock.Z);
            }

            velocity.X = next.X;
            velocity.Z = next.Z;
        }

        /// <summary>
        /// Slides down a face that is too steep to stand on, against the
        /// player's will. Called instead of plain gravity, never on top of
        /// it. Not to be confused with UpdateSlide, the crouch slide.
        /// </summary>
        void SlideDownSteep(float dt)
        {
            // Landing redirects momentum along the slope instead of dropping
            // it. Without this the slide restarts from zero on every contact
            // and velocity.y drifts into nonsense while the slope blocks it.
            velocity = UMath.ProjectOnPlane(velocity, groundNormal);

            Vector3 downhill = UMath.ProjectOnPlane(UMath.Down, groundNormal);

            // No usable slope direction, so there is nothing to redirect
            // along. Normalizing a zero vector here would poison velocity
            // with NaN and freeze the player for the rest of the session.
            if (UMath.SqrMagnitude(downhill) < 0.0001f)
            {
                velocity.Y += Gravity * dt;
                return;
            }

            downhill = UMath.Normalized(downhill);

            // Floor the downhill speed, so there is no dead time at all.
            float speed = UMath.Dot(velocity, downhill);
            if (speed < MinSlideSpeed)
                velocity = UMath.Add(velocity, UMath.Mul(downhill, MinSlideSpeed - speed));

            // The slope cancels the part of gravity pressing into it; what
            // is left accelerates the player on downhill.
            velocity = UMath.Add(velocity, UMath.Mul(
                UMath.ProjectOnPlane(UMath.Mul(UMath.Up, Gravity), groundNormal), dt));
        }

        /// <summary>
        /// Ground jump and double jump. The counter can exceed 1 when the key
        /// was pressed more than once inside a single 25 ms tick. Returns
        /// whether a jump actually fired this tick.
        /// </summary>
        bool ProcessJumpPresses(int presses)
        {
            bool jumped = false;

            for (int i = 0; i < presses; i++)
            {
                // The first press out of a slide only cancels it, and is
                // used up doing so. A following press jumps as usual, even
                // within this same tick. The clamp keeps a slope's lent
                // speed on the ground instead of carrying it into flight.
                if (sliding)
                {
                    sliding = false;
                    ClampToSprintSpeed();
                    continue;
                }

                // Jumping while crouched is allowed; the player stays crouched
                // and keeps the slower crouch speed.
                if (grounded)
                {
                    velocity.Y = UMath.Sqrt(JumpHeight * -2f * Gravity);
                    grounded = false;
                    doubleJumpUsed = false;
                    jumped = true;

                    // Opens the window in which a double tap counts.
                    jumpWindowOpen = true;
                    timeSinceGroundJump = 0f;

                    // The cut needs the take-off height, since it trims what
                    // is left to climb rather than the speed itself.
                    jumpCutPending = true;
                    jumpHeldTime = 0f;
                    jumpStartY = position.Y;

                    // The take-off press does NOT count as the first tap.
                    hasPendingAirPress = false;

                    continue;
                }

                // Window closed: taps expire without effect.
                if (!jumpWindowOpen)
                    continue;

                if (!hasPendingAirPress)
                {
                    // Press 2 of 3: first tap of the double tap, remember it.
                    hasPendingAirPress = true;
                    timeSinceAirPress = 0f;
                    continue;
                }

                // Press 3 of 3: the only place the double jump fires. Both
                // timers are tracked in Simulate, so an expired one never
                // gets here. Crouch is checked at trigger time, not take-off.
                if (!doubleJumpUsed && !isCrouching)
                {
                    velocity.Y = UMath.Sqrt(DoubleJumpHeight * -2f * Gravity);
                    doubleJumpUsed = true;
                    jumpWindowOpen = false;
                    jumped = true;
                }

                hasPendingAirPress = false;
            }

            return jumped;
        }

        void ClampToSprintSpeed()
        {
            Vector3 flat = new Vector3(velocity.X, 0f, velocity.Z);
            if (UMath.Magnitude(flat) <= SprintSpeed)
                return;

            flat = UMath.Mul(UMath.Normalized(flat), SprintSpeed);
            velocity.X = flat.X;
            velocity.Z = flat.Z;
        }

        // Called right after the presses, so a tap shorter than a single
        // tick is already trimmed in the tick it took off in.
        void UpdateJumpCut(bool held, float dt)
        {
            if (!jumpCutPending)
                return;

            if (held)
            {
                jumpHeldTime += dt;
                if (jumpHeldTime >= FullJumpHold)
                    jumpCutPending = false;   // full height earned
                return;
            }

            // Trims what is LEFT to climb, not the speed, so the total comes
            // out at the earned height whichever tick this runs in. Min, not
            // assignment: anyone already slower keeps his speed.
            float t = UMath.InverseLerp(MinJumpHold, FullJumpHold, jumpHeldTime);
            float target = UMath.Lerp(MinJumpHeight, JumpHeight, t);
            float risen = position.Y - jumpStartY;
            float rest = UMath.Sqrt(UMath.Max(0f, (target - risen) * -2f * Gravity));

            velocity.Y = UMath.Min(velocity.Y, rest);
            jumpCutPending = false;
        }

        void UpdateCrouch(bool wantsCrouch, float dt)
        {
            // Intent switches instantly, so movement speed and the double
            // jump react to the key rather than to the ongoing animation.
            if (wantsCrouch)
                isCrouching = true;
            else if (currentHeight < StandHeight)
                // Re-asked on every tick of the 0.15 s rise instead of once
                // on release: a ceiling arriving midway has to push the pose
                // back down rather than let the body grow into it.
                isCrouching = !HasHeadroom();

            float wanted = isCrouching ? CrouchHeight : StandHeight;
            if (currentHeight != wanted)
            {
                currentHeight =
                    UMath.MoveTowards(currentHeight, wanted, PoseChangeSpeed * dt);
                ApplyHeight(currentHeight);
            }

            PlaceEye();
        }

        /// <summary>
        /// Puts the eye at the height the current pose implies. The eye is
        /// simulation state as much as presentation: the swing starts there.
        /// </summary>
        void PlaceEye()
        {
            // The eye comes out of the body's own progress instead of running
            // its own timer. That makes lockstep a property of the shape, not
            // an agreement between two speeds that can drift apart.
            float t = UMath.InverseLerp(CrouchHeight, StandHeight, currentHeight);
            eyeLocal = EyeLocalPosition(UMath.Lerp(CrouchEyeHeight, StandEyeHeight, t));
        }

        /// <summary>
        /// The only place that reshapes the body. Shifts the hull's centre so
        /// the feet stay put, otherwise the player would sink when crouching.
        /// Called every tick while the pose changes, not once per keypress.
        /// </summary>
        void ApplyHeight(float height)
        {
            // The hit hull follows the pose: crouching has to make the body
            // a smaller target, or ducking would be free cover. Every shape
            // squashes toward the feet like the model does. Bepu's capsule
            // length is the segment between the end spheres.
            float scale = height / StandHeight;
            for (int i = 0; i < hullParts.Count; i++)
            {
                HullPart part = hullParts[i];
                Vector3 local = part.Position;
                local.Y = FootOffset + (local.Y - FootOffset) * scale;
                float tall = part.Height * scale;

                if (part.Shape is CapsuleCollider capsule)
                {
                    capsule.Length = tall - 2f * capsule.Radius;
                    capsule.PositionLocal = local;
                }
                else if (part.Shape is CylinderCollider cylinder)
                {
                    cylinder.Length = tall;
                    cylinder.PositionLocal = local;
                }
            }

            // The standing budget only once fully upright: anywhere in
            // between, the crouched one holds, or tapping crouch would grant
            // the tall reach for a few ticks at crouch collision height.
            maxStepHeight = height >= StandHeight ? StandStepOffset : CrouchStepOffset;
        }

        /// <summary>
        /// Moves the body and keeps the most upright upward contact of the
        /// whole tick. onGround is for the tick's main move only: an edge
        /// too high to step onto is then a wall, see CapsuleSweep.Move.
        /// </summary>
        CapsuleSweep.Result MoveBody(Vector3 delta, bool onGround = false)
        {
            CapsuleSweep.Result r = sweep.Move(
                world, CenterY, position, FootOffset, currentHeight,
                BodyRadius, SkinWidth, UMath.Cos(SlopeLimit * UMath.Deg2Rad),
                maxStepHeight, delta, velocity, WorldMask, onGround);

            position = r.position;

            if (r.hasGround
                && (!hasContactNormal || r.groundNormal.Y > contactNormal.Y))
            {
                contactNormal = r.groundNormal;
                hasContactNormal = true;
            }

            return r;
        }

        /// <summary>
        /// Climbs a step, but only onto a ledge it has actually measured:
        /// blocked ahead, room one step higher, and a walkable surface there.
        /// </summary>
        void TryStepUp(float dt, bool settling)
        {
            if (maxStepHeight <= 0f)
                return;
            if (!grounded && !(settling && velocity.Y <= 0f))
                return;

            Vector3 step = UMath.Mul(new Vector3(velocity.X, 0f, velocity.Z), dt);
            float travel = UMath.Magnitude(step);
            if (travel < 0.0001f)
                return;

            float walkable = UMath.Cos(SlopeLimit * UMath.Deg2Rad);
            Vector3 forward = UMath.Div(step, travel);
            float reach = travel + SkinWidth + StepProbeAhead;
            float radius = BodyRadius;

            Vector3 feet = UMath.Sub(UMath.Add(position, UMath.Mul(UMath.Up, CenterY)),
                                     UMath.Mul(UMath.Up, currentHeight * 0.5f));
            Vector3 bottom = UMath.Add(feet, UMath.Mul(UMath.Up, radius));
            Vector3 top = UMath.Add(feet, UMath.Mul(UMath.Up, currentHeight - radius));

            // Classify the obstacle from ankle height. Asking the body
            // capsule is unreliable: its lower sphere rides over the edge of
            // a step and reports an almost flat normal, which made crouched
            // stepping fail while standing happened to work.
            Vector3 ankle = UMath.Add(feet, UMath.Mul(UMath.Up, 0.01f));
            if (!world.Raycast(ankle, forward, reach + radius, WorldMask, out QueryHit riser))
                return;

            // A walkable face is a ramp and the sweep handles those
            // itself -- unless it demonstrably did not. On the convex edge
            // where a slope meets its top the capsule jams, and the ankle
            // ray only ever sees the last centimetres of ramp.
            if (riser.Normal.Y > walkable && !wedged)
                return;

            // Room for the whole body one step higher? This is the check that
            // stops the corner exploit: a step that is too tall still blocks.
            Vector3 lift = UMath.Mul(UMath.Up, maxStepHeight);
            if (world.CapsuleCastAny(UMath.Add(bottom, lift), UMath.Add(top, lift), radius,
                    forward, reach, WorldMask))
                return;

            // Measure the ledge from above rather than trusting the wall hit.
            // The ankle ray starts at the body axis, so its distance already
            // includes the radius.
            Vector3 ahead = UMath.Add(UMath.Add(feet, lift),
                                      UMath.Mul(forward, riser.Distance + StepProbeAhead));

            if (!world.Raycast(ahead, UMath.Down, maxStepHeight, WorldMask, out QueryHit ledge))
                return;

            if (ledge.Normal.Y < walkable)
                return;

            float rise = maxStepHeight - ledge.Distance;
            if (rise <= 0.001f)
                return;

            MoveBody(UMath.Mul(UMath.Up, rise + SkinWidth));

            // The body axis is still behind the ledge for a tick or two, so
            // the ground snap would find the step BELOW and undo this lift.
            // It only ever showed on flights shallower than the snap range.
            steppedUp = true;
            stepSettleTicks = 2;

            // The lift only clears the ledge by skinWidth, and the settling
            // speed of -2 m/s eats more than that in one tick. Without this
            // the player drops straight back onto the lip and sticks there.
            if (velocity.Y < 0f)
                velocity.Y = 0f;
        }

        /// <summary>
        /// Keeps a crouching player from walking off a ledge. The step is
        /// tried axis by axis, so running ALONG an edge still works and only
        /// the part pointing over it is dropped. In an inside corner neither
        /// axis survives and the player stops, still fully steerable.
        /// </summary>
        void GuardLedge(float dt)
        {
            Vector3 step = UMath.Mul(new Vector3(velocity.X, 0f, velocity.Z), dt);
            if (UMath.SqrMagnitude(step) < 0.0000001f || HasFooting(step))
                return;

            if (step.X != 0f && HasFooting(new Vector3(step.X, 0f, 0f)))
            {
                velocity.Z = 0f;
                return;
            }

            if (step.Z != 0f && HasFooting(new Vector3(0f, 0f, step.Z)))
            {
                velocity.X = 0f;
                return;
            }

            velocity.X = 0f;
            velocity.Z = 0f;
        }

        /// <summary>
        /// Is there walkable ground under the player after this horizontal
        /// step? Thin probe down the body axis, so the player may stand with
        /// half the capsule over the edge - that is what it should feel like.
        /// </summary>
        bool HasFooting(Vector3 step)
        {
            float probeRadius = BodyRadius * 0.25f;
            float toFeet = currentHeight * 0.5f - probeRadius;
            Vector3 origin = UMath.Add(UMath.Add(position, UMath.Mul(UMath.Up, CenterY)), step);

            int count = world.SphereCastAll(
                origin, probeRadius, UMath.Down, toFeet + LedgeGuardDrop,
                WorldMask, groundHits);

            float walkable = UMath.Cos(SlopeLimit * UMath.Deg2Rad);

            for (int i = 0; i < count; i++)
            {
                // A steep face below is a slide, not footing.
                if (groundHits[i].Normal.Y >= walkable)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Pulls the player back onto walkable ground it just ran off.
        /// Downhill the surface drops faster than the -2 m/s settling speed
        /// follows, so grounded flickered: no jump, air acceleration instead
        /// of ground acceleration, and visible hopping.
        /// </summary>
        void SnapToGround()
        {
            // Nothing was lost, or the player is on the way up anyway.
            if (touchingGround || velocity.Y > 0f)
                return;

            float probeRadius = BodyRadius * 0.25f;
            float toFeet = currentHeight * 0.5f - probeRadius;
            Vector3 origin = UMath.Add(position, UMath.Mul(UMath.Up, CenterY));

            int count = world.SphereCastAll(
                origin, probeRadius, UMath.Down, toFeet + GroundSnapDistance,
                WorldMask, groundHits);

            float walkable = UMath.Cos(SlopeLimit * UMath.Deg2Rad);
            float nearest = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                // Steep faces are not worth sticking to; that is the slide's
                // job, and gluing to one would kill it.
                if (groundHits[i].Normal.Y < walkable)
                    continue;

                if (groundHits[i].Distance < nearest)
                    nearest = groundHits[i].Distance;
            }

            if (nearest == float.MaxValue)
                return;

            // On a slope the capsule rests on the SIDE of its lower sphere,
            // so this reads too deep. That is the safe direction: the sweep
            // stops at the first contact, it never pushes through a surface.
            float gap = nearest - toFeet;
            if (gap > 0f)
                MoveBody(UMath.Mul(UMath.Down, gap));

            touchingGround = true;
        }

        /// <summary>
        /// Remembers a move the world refused, so the next TryStepUp may
        /// measure a ledge it would otherwise have waved through. Compares
        /// what was asked for against what the sweep actually did.
        /// </summary>
        void UpdateWedged(Vector3 before, float dt)
        {
            Vector3 wanted = UMath.Mul(new Vector3(velocity.X, 0f, velocity.Z), dt);
            float asked = UMath.Magnitude(wanted);
            if (asked < 0.0001f)
            {
                wedged = false;
                return;
            }

            Vector3 moved = UMath.Sub(position, before);
            moved.Y = 0f;

            // A quarter of what was asked: sliding along a wall still makes
            // progress, a capsule stuck on an edge makes almost none.
            wedged = UMath.Magnitude(moved) < asked * 0.25f;
        }

        // A capsule caught in a shaft narrower than itself on steep ground
        // reads steep forever, slides nowhere and no key frees it. Only that:
        // steep for a quarter second, refused, and still this tick.
        const float JamSteepTime = 0.25f;
        const float JamSpeed = 0.4f;
        const float JamLiftStep = 0.1f;
        const int JamLifts = 8;

        // Downhill first, then turning away from it to either side in turn.
        static readonly Vector2[] JamTurns =
        {
            new Vector2(1f, 0f), new Vector2(0.70710677f, 0.70710677f), new Vector2(0.70710677f, -0.70710677f),
            new Vector2(0f, 1f), new Vector2(0f, -1f),
            new Vector2(-0.70710677f, 0.70710677f), new Vector2(-0.70710677f, -0.70710677f), new Vector2(-1f, 0f),
        };

        bool Jammed() => onSteepGround && !grounded && wedged
                      && steepTime >= JamSteepTime && lastStepSpeed < JamSpeed;

        /// <summary>
        /// Lifts the jammed capsule to the nearest pose where it fits whole:
        /// 0,1 to 0,8 m up and 0,3 or 0,6 m aside, in eight directions,
        /// downhill first. Never straight up: the body would fall back into
        /// the same groove. Speed goes, the fall from there is an ordinary
        /// one. Nothing fits, nothing moves.
        /// </summary>
        void EscapeJam()
        {
            Vector3 n = new Vector3(groundNormal.X, 0f, groundNormal.Z);
            float nl = UMath.Magnitude(n);
            n = nl > 0.01f ? UMath.Mul(n, 1f / nl) : UMath.Rotate(rotation, UMath.Forward);
            var p = new Vector3(-n.Z, 0f, n.X);

            for (int lift = 1; lift <= JamLifts; lift++)
            {
                float up = lift * JamLiftStep;
                for (int reach = 1; reach <= 2; reach++)
                {
                    float side = reach * 0.3f;
                    for (int d = 0; d < JamTurns.Length; d++)
                    {
                        Vector3 dir = UMath.Add(UMath.Mul(n, JamTurns[d].X), UMath.Mul(p, JamTurns[d].Y));
                        Vector3 to = UMath.Add(position, UMath.Add(UMath.Mul(dir, side), UMath.Mul(UMath.Up, up)));
                        if (!Fits(to) || !Reaches(to))
                            continue;
                        position = to;
                        velocity = Vector3.Zero;
                        knock = Vector3.Zero;
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Is the way from the body's centre to the same point at <paramref name="at"/>
        /// open? Fits alone passed a pose inside the mountain: its collision is a
        /// hollow shell of triangles, and a capsule within touches none of them
        /// (Luca fell through there). The ray crosses that shell from outside.
        /// </summary>
        bool Reaches(Vector3 at)
        {
            Vector3 from = UMath.Add(position, UMath.Mul(UMath.Up, CenterY));
            Vector3 way = UMath.Sub(at, position);
            float length = UMath.Magnitude(way);
            return !world.Raycast(from, UMath.Mul(way, 1f / length), length, WorldMask, out _);
        }

        /// <summary>Does the current shape fit whole, at full radius, at this position?</summary>
        bool Fits(Vector3 at)
        {
            float radius = BodyRadius;
            Vector3 feet = UMath.Add(at, UMath.Mul(UMath.Up, CenterY - currentHeight * 0.5f));
            Vector3 bottom = UMath.Add(feet, UMath.Mul(UMath.Up, radius));
            Vector3 top = UMath.Add(feet, UMath.Mul(UMath.Up, currentHeight - radius));
            return world.OverlapCapsule(bottom, top, radius, WorldMask, headroomHits) == 0;
        }

        /// <summary>
        /// Does the standing capsule fit at the current position? The body's
        /// own hull sits on a masked-out layer, so it never shows up here.
        /// </summary>
        bool HasHeadroom()
        {
            float radius = BodyRadius;
            Vector3 feet = UMath.Add(position,
                UMath.Mul(UMath.Up, CenterY - currentHeight * 0.5f));

            Vector3 bottom = UMath.Add(feet, UMath.Mul(UMath.Up, radius));
            Vector3 top = UMath.Add(feet, UMath.Mul(UMath.Up, StandHeight - radius));

            // Slightly smaller radius, otherwise the check grazes walls the
            // player is currently running along.
            int count = world.OverlapCapsule(bottom, top, radius * 0.95f,
                                             HeadroomWorldMask, headroomHits);

            return count == 0;
        }

        /// <summary>
        /// Reads the surface under the feet into groundNormal and groundAngle.
        /// Returns false when nothing was found, which the caller must not
        /// confuse with flat ground. Thin on purpose: a sphere as wide as the
        /// body would catch the riser of the stair in front of the player.
        /// </summary>
        bool ProbeGround()
        {
            // What the last sweep actually touched comes first. On a slope
            // the capsule rests on the SIDE of its lower sphere, so the
            // surface sits radius * (1/cos a - 1) below the body axis - 0.30 m
            // at 55 degrees - where no cast from the axis reaches.
            if (hasContactNormal)
            {
                groundNormal = contactNormal;
                groundAngle = UMath.Angle(groundNormal, UMath.Up);
                return true;
            }

            // A quarter of the body radius stays well clear of anything the
            // player is standing against, and the cast length follows it so
            // the sphere still reaches below the feet.
            float probeRadius = BodyRadius * 0.25f;
            Vector3 origin = UMath.Add(position, UMath.Mul(UMath.Up, CenterY));

            float distance = currentHeight * 0.5f - probeRadius
                           + SkinWidth + GroundProbeExtra;

            int count = world.SphereCastAll(
                origin, probeRadius, UMath.Down, distance, WorldMask, groundHits);

            groundNormal = UMath.Up;
            groundAngle = 0f;
            float nearest = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                // Walls and overhangs are not ground. Without this a vertical
                // face reads as 90 degrees and triggers the slide.
                if (groundHits[i].Normal.Y <= 0f)
                    continue;

                if (groundHits[i].Distance >= nearest)
                    continue;

                nearest = groundHits[i].Distance;
                groundNormal = groundHits[i].Normal;
            }

            if (nearest == float.MaxValue)
                return false;

            groundAngle = UMath.Angle(groundNormal, UMath.Up);
            return true;
        }

        Vector3 EyeLocalPosition(float eyeHeightAboveFeet)
            => new Vector3(0f, eyeHeightAboveFeet - StandHeight * 0.5f, 0f);

        Vector3 CurrentEyeWorldPosition()
            => UMath.Add(position, UMath.Rotate(rotation, eyeLocal));

        Vector3 CurrentBodyCenter()
            => UMath.Add(position, UMath.Mul(UMath.Up, CenterY));
    }
}
