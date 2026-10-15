using UnityEngine;

namespace KotH.Shared.Player
{
    /// <summary>
    /// Moves the player. Reads no input and knows no camera - a dedicated
    /// server has neither. All mutable state lives in the fields below and
    /// nowhere else, which is what lets a tick be replayed later.
    /// Collision goes through CapsuleSweep rather than CharacterController:
    /// PhysX keeps the controller's state where nobody can restore it.
    /// </summary>
    public sealed class PlayerMotor : MonoBehaviour
    {
        [Header("Tempo (m/s)")]
        [SerializeField] float walkSpeed = 5.175f;
        [SerializeField] float sprintSpeed = 8.625f;
        [SerializeField] float crouchSpeed = 2.1f;

        [Header("Trägheit (m/s²)")]
        [SerializeField] float groundAcceleration = 50f;
        [SerializeField] float groundDeceleration = 35f;

        // 15% of ground acceleration: enough to correct mid-jump, not enough
        // to reverse direction within a normal flight time.
        [SerializeField] float airAcceleration = 7.5f;

        [Header("Sprung und Schwerkraft")]
        // All three carry a pre-correction: integrating at 40 Hz loses
        // v*dt/2 of every jump, so a plain 1.5 would peak at 1.405 m. These
        // numbers are chosen so the REAL peaks come out at 1.50, 1.00 and
        // 1.00 m. Retuning one means measuring it again, see JumpHeightProbe.
        [SerializeField] float jumpHeight = 1.6f;
        [SerializeField] float doubleJumpHeight = 1.0822f;

        [Tooltip("Sprunghöhe bei ganz kurzem Antippen")]
        [SerializeField] float minJumpHeight = 1.0744f;

        [Tooltip("Haltedauer, bis zu der nur die Mindesthöhe kommt")]
        [SerializeField] float minJumpHold = 0.032f;

        [Tooltip("Haltedauer für die volle Sprunghöhe")]
        [SerializeField] float fullJumpHold = 0.2f;

        // Opens on the ground jump. A double tap only counts inside this time.
        [SerializeField] float doubleJumpWindow = 1f;

        // Maximum gap between the two taps of the double tap.
        [SerializeField] float doubleTapWindow = 0.25f;

        // Deliberately about twice real gravity; at -9.81 a jump feels floaty.
        // The project setting is untouched and still applies to rigidbodies.
        [SerializeField] float gravity = -20f;

        // Eyes sit 0.12 m below the crown in both poses, as on a real body.
        [Header("Körpermaße (m)")]
        // Our own shape from here on. A CharacterController answers a query
        // with a smaller hull than it claims - 18 cm missing at the crown -
        // and the simulation must not rest on a component it is about to
        // lose - the sweep reads them directly.
        [SerializeField] float bodyRadius = 0.4f;
        [SerializeField] float skinWidth = 0.04f;
        [SerializeField] float standHeight = 1.8f;
        [SerializeField] float crouchHeight = 0.9f;
        [SerializeField] float standEyeHeight = 1.68f;
        [SerializeField] float crouchEyeHeight = 0.78f;

        // Drives collision height and eye together, in m/s of height: the
        // 0.90 m between the poses take 0.15 s. One rate, because two would
        // let the eye leave the body it belongs to.
        [SerializeField] float poseChangeSpeed = 6f;

        // Step height the body climbs. Crouched it is much lower, so
        // ducking becomes a real trade: you fit through gaps but lose reach.
        [SerializeField] float standStepOffset = 0.45f;
        [SerializeField] float crouchStepOffset = 0.25f;

        // Crouching guards against falling: a step that would leave nothing
        // walkable this far below the feet is refused. Deliberately larger
        // than groundSnapDistance, so a drop is either caught or refused.
        [SerializeField] float ledgeGuardDrop = 0.5f;
        [SerializeField] LayerMask headroomMask = ~0;

        [Header("Hang")]
        [Tooltip("Steilste Fläche, die noch begehbar ist")]
        [SerializeField] float slopeLimit = 50f;

        [SerializeField] LayerMask groundMask = ~0;

        // How far below the feet the probe still looks for ground.
        [SerializeField] float groundProbeExtra = 0.1f;

        // How far the player is pulled back down onto ground it ran off.
        // Covers a sprint down the steepest walkable ramp; much more would
        // glue the player to the floor at a real ledge.
        [SerializeField] float groundSnapDistance = 0.3f;

        // Speed the slide starts at, so stepping onto a steep face slides
        // at once instead of accelerating out of a standstill.
        [SerializeField] float minSlideSpeed = 3f;

        // A stair edge can report a steep normal for a tick or two. Sliding
        // only starts once the reading holds, so an edge cannot shove the
        // player back down the step.
        [SerializeField] float slideConfirmTime = 0.075f;

        // How far past the blocking wall the ledge is measured.
        [SerializeField] float stepProbeAhead = 0.05f;

        [Header("Gleiten")]
        [Tooltip("Reibung beim Gleiten, deutlich kleiner als beim Bremsen")]
        [SerializeField] float slideDeceleration = 6f;

        [Tooltip("Tempo, über das ein Hang das Gleiten nicht hinaustreibt")]
        [SerializeField] float maxSlideSpeed = 15f;

        [Tooltip("Abstand zwischen zwei Gleitvorgängen, ab dem Start gezählt")]
        [SerializeField] float slideCooldown = 0.5f;

        [Header("Verknüpfung")]
        [SerializeField] Transform cameraMount;

        CapsuleCollider hull;     // what a shot may hit, full height
        readonly CapsuleSweep sweep = new CapsuleSweep();

        // Bodies never block movement - BodyContact resolves them - so no
        // world query may see one. The body's own hull sits on that layer
        // too, so this skips it as well. An invariant, not a setting.
        LayerMask WorldMask => groundMask & ~Combat.Hittable.Mask;
        LayerMask HeadroomMask => headroomMask & ~Combat.Hittable.Mask;

        // Feet relative to the transform. The pose shrinks downward from the
        // standing centre, so this does not move when crouching.
        float FootOffset => -standHeight * 0.5f;

        // Where the capsule's middle sits above the transform. The shape
        // shrinks downward so the feet stay put, which is what ApplyHeight
        // the hull is given in ApplyHeight.
        float CenterY => (currentHeight - standHeight) * 0.5f;

        // --- mutable state ---
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
        Vector3 groundNormal = Vector3.up;
        float groundAngle;
        float steepTime;          // how long the ground has read too steep
        Vector3 contactNormal;    // most upright contact of the last Move
        bool hasContactNormal;
        float maxStepHeight;      // our own step limit, PhysX gets none
        bool steppedUp;           // climbed a ledge this tick
        bool wedged;              // last move was refused by the world
        float knockbackTime;      // ground control stays soft this long
        bool doubleJumpUsed;      // once per flight
        bool jumpWindowOpen;      // window since the ground jump
        float timeSinceGroundJump;
        bool hasPendingAirPress;  // first air tap waiting for its partner
        float timeSinceAirPress;
        bool jumpCutPending;      // ground jump running, not trimmed yet
        float jumpHeldTime;       // how long the key stayed down since take-off
        float jumpStartY;         // take-off height, the cut measures from it

        // --- read by the simulation too: contacts and the swing start here ---
        Vector3 eyePosition;
        Vector3 previousEyePosition;
        Vector3 bodyCenter;
        Vector3 previousBodyCenter;

        // Preallocated so the headroom check allocates nothing per tick.
        readonly Collider[] headroomHits = new Collider[8];
        readonly RaycastHit[] groundHits = new RaycastHit[8];

        public Vector3 EyePosition => eyePosition;
        public Vector3 PreviousEyePosition => previousEyePosition;
        public Vector3 BodyCenter => bodyCenter;
        public Vector3 PreviousBodyCenter => previousBodyCenter;
        public float Height => currentHeight;
        public float Radius => bodyRadius;
        public float SlopeLimit => slopeLimit;
        public float MaxStepHeight => maxStepHeight;
        public bool IsGrounded => grounded;

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

        /// <summary>
        /// Adds a push from outside the motor, for instance a body collision.
        /// Softening ground control afterwards is what makes a push visible
        /// at all: at 50 m/s2 a 1.4 m/s push would be regulated away inside
        /// two ticks, leaving nothing to feel.
        /// </summary>
        public void AddVelocity(Vector3 delta, float maxSoftenTime)
        {
            velocity += delta;

            // Only as long as the body really needs to come to rest again.
            // A flat cap left anyone in constant contact permanently limp,
            // because every nudge, however small, renewed the full time.
            float leaving = Mathf.Abs(Vector3.Dot(velocity, delta.normalized));

            knockbackTime = Mathf.Max(knockbackTime,
                Mathf.Min(maxSoftenTime, leaving / airAcceleration));
        }

        /// <summary>
        /// Puts the body somewhere. Deliberately moves nothing else: velocity
        /// survives, so a caller that wants a clean start has to say so. The
        /// probes rely on that - clearing it here moved their starting points
        /// and changed 84 measured cases out of 252.
        /// </summary>
        public void Teleport(Vector3 position)
        {
            transform.position = position;

            // autoSyncTransforms is off, so the hull's collider would keep
            // its old place until the next physics step. The sweep queries
            // against it in the same tick, so it has to know now.
            Physics.SyncTransforms();
        }

        /// <summary>The motor as this tick left it, for a later rollback.</summary>
        public MotorState CaptureState()
        {
            return new MotorState
            {
                Position = transform.position,
                Rotation = transform.rotation,
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
                Wedged = wedged,
                KnockbackTime = knockbackTime,
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
        /// follow the restored height, and PhysX learns the new hull pose
        /// at once: a swing in the very next tick already asks for it.
        /// </summary>
        public void RestoreState(in MotorState s)
        {
            transform.SetPositionAndRotation(s.Position, s.Rotation);
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
            wedged = s.Wedged;
            knockbackTime = s.KnockbackTime;
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

            // maxStepHeight is derived from the height, not stored.
            ApplyHeight(currentHeight);
            PlaceEye();
            Physics.SyncTransforms();
        }

        /// <summary>
        /// Shifts the body out of another one. Velocity is left alone: being
        /// pushed out of an overlap is a correction, not a hit - the push
        /// itself goes through AddVelocity.
        /// </summary>
        public void Nudge(Vector3 delta)
        {
            transform.position += delta;
        }

        void Awake()
        {
            // GetComponent belongs here, not in Tick and not in Update.
            hull = GetComponent<CapsuleCollider>();

            if (hull == null)
                Debug.LogWarning("Keine Trefferkapsel am Körper.", this);

            currentHeight = standHeight;
            ApplyHeight(currentHeight);
            if (cameraMount != null)
                cameraMount.localPosition = EyeLocalPosition(standEyeHeight);

            eyePosition = previousEyePosition = CurrentEyeWorldPosition();
            bodyCenter = previousBodyCenter = CurrentBodyCenter();
        }

        /// <summary>
        /// One simulation step, called with a fixed dt (40 Hz) so that client
        /// and server can compute the same step.
        /// </summary>
        public void Tick(in MoveInput input, float dt)
        {
            previousEyePosition = eyePosition;
            previousBodyCenter = bodyCenter;

            // Yaw turns the body; pitch belongs to the camera alone, else the
            // player would tilt backwards when looking up.
            transform.rotation = Quaternion.Euler(0f, input.Yaw, 0f);

            // isGrounded only says THAT there is ground, never how steep it
            // is. Too steep counts as not grounded, so jumping, ground
            // acceleration and the double jump reset all fall into place.
            // What the last tick's sweep ended up touching. PhysX used to
            // answer this from inside the controller; now it is our own
            // field and therefore part of what a rollback can restore.
            bool touching = touchingGround;
            bool knownGround = false;

            if (touching)
            {
                knownGround = ProbeGround();
            }
            else
            {
                groundNormal = Vector3.up;
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
                && (!knownGround || groundAngle > slopeLimit);
            steepTime = steepReading ? steepTime + dt : 0f;

            grounded = touching && !steepReading;
            onSteepGround = steepReading && steepTime >= slideConfirmTime;

            if (grounded)
            {
                if (velocity.y < 0f)
                    velocity.y = -2f; // keeps the player pinned on slopes

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
                    if (timeSinceGroundJump > doubleJumpWindow)
                    {
                        jumpWindowOpen = false;
                        hasPendingAirPress = false;
                    }
                }

                if (hasPendingAirPress)
                {
                    timeSinceAirPress += dt;
                    if (timeSinceAirPress > doubleTapWindow)
                        hasPendingAirPress = false;
                }
            }

            // Before UpdateHorizontal, so it judges the speed this tick
            // started with and the result is used in the same tick.
            UpdateSprint(input.Sprint);

            // After UpdateSprint, which it asks, and before UpdateHorizontal,
            // which reads slideSpeed as its target.
            UpdateSlide(input, dt);

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
                velocity.y += gravity * dt;

            // Cleared here so the contacts collected below belong to this
            // tick's movement, both the step-up lift and the main Move.
            // After the jump on purpose: a jump leaves the ledge
            // deliberately and must keep its horizontal speed.
            // Not while sliding: the guard is there so crouched creeping
            // does not walk off a ledge, and it zeroes the velocity. A slide
            // is meant to carry over the edge instead of sticking to it.
            if (isCrouching && !sliding && grounded && !jumped)
                GuardLedge(dt);

            hasContactNormal = false;
            steppedUp = false;

            Vector3 beforeMove = transform.position;

            TryStepUp(dt);
            CapsuleSweep.Result moved = MoveBody(velocity * dt);
            touchingGround = moved.hasGround;

            // Before the speed is trimmed: UpdateWedged asks what the tick
            // wanted to cover, and a trimmed speed never shows a block.
            UpdateWedged(beforeMove, dt);

            // Speed a wall refused is gone, not carried. Kept, it fed a
            // sprint that never ends, a body push with invented momentum
            // and a lastStepSpeed that disagreed with velocity. A riser low
            // enough to climb keeps it, see CapsuleSweep.IsRiser.
            velocity = moved.velocity;

            // Only ground that was walkable at the start of the tick is worth
            // keeping. A jump clears grounded itself, so it is never undone,
            // and a step just climbed must not be pulled back down either.
            if (grounded && !steppedUp)
                SnapToGround();

            // What the tick really covered, not what it asked for: a blocked
            // body leaves velocity untouched. Measured in 3D, because
            // uphill the horizontal part alone shrinks by cos squared and
            // would drop below walking speed on any ramp past 39 degrees.
            Vector3 step = transform.position - beforeMove;
            lastStepSpeed = step.magnitude / dt;

            eyePosition = CurrentEyeWorldPosition();
            bodyCenter = CurrentBodyCenter();

            // Counted down last, so the tick a push arrived in is softened
            // in full rather than by whatever is left of dt.
            if (knockbackTime > 0f)
                knockbackTime = Mathf.Max(0f, knockbackTime - dt);
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

            // Measured, not wished for: velocity survives a wall untouched.
            // It is last tick's value, so the sprint ends one tick late,
            // which at 25 ms nobody can feel.
            float flat = lastStepSpeed;

            // The limit may only bite once walk speed was reached: from a
            // standstill the speed starts below it, which would end the
            // sprint in the very tick it began.
            if (!sprintArmed)
                sprintArmed = flat > walkSpeed;
            else if (flat < walkSpeed)
                sprinting = false;
        }

        // The crouch slide: a sprint that keeps its momentum while ducking
        // instead of being pulled down to crouch speed. Only speed or the
        // key end it on flat ground; downhill only the key does.
        void UpdateSlide(in MoveInput input, float dt)
        {
            // Counted down before the start below, so the tick a slide
            // begins in sets the full time rather than a shortened one.
            if (slideCooldownLeft > 0f)
                slideCooldownLeft = Mathf.Max(0f, slideCooldownLeft - dt);

            if (sliding)
            {
                // Too slow, airborne, or on a face the steep slide owns.
                // Letting go of the key does NOT end it -- a tap is enough
                // to start, so there is nothing to release. The jump key
                // cancels it in ProcessJumpPresses, before any of this.
                if (!grounded || onSteepGround || slideSpeed < walkSpeed)
                {
                    sliding = false;
                    return;
                }

                // Friction always, gravity only along the slope: downhill it
                // adds, uphill it subtracts, on the flat it is zero. Same
                // projection SlideDownSteep uses, so both agree on downhill.
                Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
                float push = 0f;
                if (flat.sqrMagnitude > 0.0001f)
                {
                    Vector3 slope =
                        Vector3.ProjectOnPlane(Vector3.up * gravity, groundNormal);
                    push = Vector3.Dot(slope, flat.normalized);
                }

                slideSpeed = Mathf.Min(maxSlideSpeed,
                    slideSpeed + (push - slideDeceleration) * dt);
                return;
            }

            // Out of a sprint only, and only while the body really moves that
            // fast: lastStepSpeed, because velocity survives a wall untouched
            // and a slide out of the run-up would end in the tick it began.
            // Measured from the start, not the end: a slide that ran its
            // full course is longer than the cooldown and so never waits,
            // while hammering the key does.
            if (input.CrouchPressed && sprinting && grounded && !onSteepGround
                && slideCooldownLeft <= 0f && lastStepSpeed > walkSpeed)
            {
                sliding = true;
                slideCooldownLeft = slideCooldown;
                slideSpeed = new Vector2(velocity.x, velocity.z).magnitude;
            }
        }

        void UpdateHorizontal(in MoveInput input, float dt)
        {
            // Without clamping, moving diagonally would be 1.41x faster.
            Vector2 move = Vector2.ClampMagnitude(input.Move, 1f);
            bool hasInput = move.sqrMagnitude > 0.0001f;

            Vector3 wish = transform.right * move.x + transform.forward * move.y;

            // A slide rolls out along the direction it has, even with no key
            // down: the usual deceleration to zero would otherwise end it
            // the moment the key is released.
            if (sliding && !hasInput)
            {
                Vector3 rolling = new Vector3(velocity.x, 0f, velocity.z);
                if (rolling.sqrMagnitude > 0.0001f)
                {
                    wish = rolling.normalized;
                    hasInput = true;
                }
            }

            float speed = sliding ? slideSpeed
                        : isCrouching ? crouchSpeed
                        : sprinting ? sprintSpeed
                        : walkSpeed;

            // No input while airborne means rate 0, so momentum is preserved.
            // Anything else would be a hidden air brake on key release.
            //
            // Right after a push, air control holds for both of them: full
            // ground acceleration would cancel the push within a tick or two.
            float rate = knockbackTime > 0f
                ? airAcceleration
                : grounded
                    ? (hasInput ? groundAcceleration : groundDeceleration)
                    : (hasInput ? airAcceleration : 0f);

            Vector3 current = new Vector3(velocity.x, 0f, velocity.z);
            Vector3 next = Vector3.MoveTowards(current, wish * speed, rate * dt);

            velocity.x = next.x;
            velocity.z = next.z;
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
            velocity = Vector3.ProjectOnPlane(velocity, groundNormal);

            Vector3 downhill = Vector3.ProjectOnPlane(Vector3.down, groundNormal);

            // No usable slope direction, so there is nothing to redirect
            // along. Normalizing a zero vector here would poison velocity
            // with NaN and freeze the player for the rest of the session.
            if (downhill.sqrMagnitude < 0.0001f)
            {
                velocity.y += gravity * dt;
                return;
            }

            downhill.Normalize();

            // Floor the downhill speed, so there is no dead time at all.
            float speed = Vector3.Dot(velocity, downhill);
            if (speed < minSlideSpeed)
                velocity += downhill * (minSlideSpeed - speed);

            // The slope cancels the part of gravity pressing into it; what
            // is left accelerates the player on downhill.
            velocity += Vector3.ProjectOnPlane(Vector3.up * gravity, groundNormal) * dt;
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
                    velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
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
                    jumpStartY = transform.position.y;

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
                // timers are tracked in Tick, so an expired one never gets
                // here. Vertical velocity no longer matters, the window
                // replaced it. Crouch is checked at trigger time, not take-off.
                if (!doubleJumpUsed && !isCrouching)
                {
                    velocity.y = Mathf.Sqrt(doubleJumpHeight * -2f * gravity);
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
            Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
            if (flat.magnitude <= sprintSpeed)
                return;

            flat = flat.normalized * sprintSpeed;
            velocity.x = flat.x;
            velocity.z = flat.z;
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
                if (jumpHeldTime >= fullJumpHold)
                    jumpCutPending = false;   // full height earned
                return;
            }

            // Trims what is LEFT to climb, not the speed, so the total comes
            // out at the earned height whichever tick this runs in. Min, not
            // assignment: anyone already slower keeps his speed.
            float t = Mathf.InverseLerp(minJumpHold, fullJumpHold, jumpHeldTime);
            float target = Mathf.Lerp(minJumpHeight, jumpHeight, t);
            float risen = transform.position.y - jumpStartY;
            float rest = Mathf.Sqrt(Mathf.Max(0f, (target - risen) * -2f * gravity));

            velocity.y = Mathf.Min(velocity.y, rest);
            jumpCutPending = false;
        }

        void UpdateCrouch(bool wantsCrouch, float dt)
        {
            // Intent switches instantly, so movement speed and the double
            // jump react to the key rather than to the ongoing animation.
            if (wantsCrouch)
                isCrouching = true;
            else if (currentHeight < standHeight)
                // Re-asked on every tick of the 0.15 s rise instead of once
                // on release: a ceiling arriving midway has to push the pose
                // back down rather than let the body grow into it.
                isCrouching = !HasHeadroom();

            float wanted = isCrouching ? crouchHeight : standHeight;
            if (currentHeight != wanted)
            {
                currentHeight =
                    Mathf.MoveTowards(currentHeight, wanted, poseChangeSpeed * dt);
                ApplyHeight(currentHeight);
            }

            PlaceEye();
        }

        /// <summary>
        /// Puts the camera mount at the eye height the current pose implies.
        /// The mount is simulation state as much as presentation: the swing
        /// starts at its world position.
        /// </summary>
        void PlaceEye()
        {
            if (cameraMount == null)
                return;

            // The eye comes out of the body's own progress instead of running
            // its own timer. That makes lockstep a property of the shape, not
            // an agreement between two speeds that can drift apart.
            float t = Mathf.InverseLerp(crouchHeight, standHeight, currentHeight);
            cameraMount.localPosition =
                EyeLocalPosition(Mathf.Lerp(crouchEyeHeight, standEyeHeight, t));
        }

        /// <summary>
        /// The only place that reshapes the body. Shifts the hull's centre so
        /// the feet stay put, otherwise the player would sink when crouching.
        /// Called every tick while the pose changes, not once per keypress.
        /// </summary>
        void ApplyHeight(float height)
        {
            Vector3 center = new Vector3(0f, (height - standHeight) * 0.5f, 0f);

            // The hit hull follows the pose: crouching has to make the body
            // a smaller target, or ducking would be free cover. The sweep
            // reads currentHeight directly and needs nothing here.
            if (hull != null)
            {
                hull.height = height;
                hull.center = center;
            }

            // The standing budget only once fully upright: anywhere in
            // between, the crouched one holds, or tapping crouch would grant
            // the tall reach for a few ticks at crouch collision height.
            maxStepHeight = height >= standHeight ? standStepOffset : crouchStepOffset;
        }

        /// <summary>
        /// Moves the body and keeps the most upright upward contact of the
        /// whole tick, which is what OnControllerColliderHit used to collect.
        /// </summary>
        CapsuleSweep.Result MoveBody(Vector3 delta)
        {
            CapsuleSweep.Result r = sweep.Move(
                hull, transform.position, FootOffset, currentHeight,
                bodyRadius, skinWidth, Mathf.Cos(slopeLimit * Mathf.Deg2Rad),
                maxStepHeight, delta, velocity, WorldMask);

            transform.position = r.position;

            if (r.hasGround
                && (!hasContactNormal || r.groundNormal.y > contactNormal.y))
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
        void TryStepUp(float dt)
        {
            if (!grounded || maxStepHeight <= 0f)
                return;

            Vector3 step = new Vector3(velocity.x, 0f, velocity.z) * dt;
            float travel = step.magnitude;
            if (travel < 0.0001f)
                return;

            float walkable = Mathf.Cos(slopeLimit * Mathf.Deg2Rad);
            Vector3 forward = step / travel;
            float reach = travel + skinWidth + stepProbeAhead;
            float radius = bodyRadius;

            Vector3 feet = transform.position + Vector3.up * CenterY
                         - Vector3.up * (currentHeight * 0.5f);
            Vector3 bottom = feet + Vector3.up * radius;
            Vector3 top = feet + Vector3.up * (currentHeight - radius);

            // Classify the obstacle from ankle height. Asking the body
            // capsule is unreliable: its lower sphere rides over the edge of
            // a step and reports an almost flat normal, which made crouched
            // stepping fail while standing happened to work.
            Vector3 ankle = feet + Vector3.up * 0.01f;
            if (!Physics.Raycast(ankle, forward, out RaycastHit riser, reach + radius,
                    WorldMask, QueryTriggerInteraction.Ignore))
                return;

            // A walkable face is a ramp and the sweep handles those
            // itself -- unless it demonstrably did not. On the convex edge
            // where a slope meets its top the capsule jams, and the ankle
            // ray only ever sees the last centimetres of ramp.
            if (riser.normal.y > walkable && !wedged)
                return;

            // Room for the whole body one step higher? This is the check that
            // stops the corner exploit: a step that is too tall still blocks.
            Vector3 lift = Vector3.up * maxStepHeight;
            if (Physics.CapsuleCast(bottom + lift, top + lift, radius, forward,
                    out _, reach, WorldMask, QueryTriggerInteraction.Ignore))
                return;

            // Measure the ledge from above rather than trusting the wall hit.
            // The ankle ray starts at the body axis, so its distance already
            // includes the radius.
            Vector3 ahead = feet + lift
                          + forward * (riser.distance + stepProbeAhead);

            if (!Physics.Raycast(ahead, Vector3.down, out RaycastHit ledge,
                    maxStepHeight, WorldMask, QueryTriggerInteraction.Ignore))
                return;

            if (ledge.normal.y < walkable)
                return;

            float rise = maxStepHeight - ledge.distance;
            if (rise <= 0.001f)
                return;

            MoveBody(Vector3.up * (rise + skinWidth));

            // The body axis is still behind the ledge for a tick or two, so
            // the ground snap would find the step BELOW and undo this lift.
            // It only ever showed on flights shallower than the snap range.
            steppedUp = true;

            // The lift only clears the ledge by skinWidth, and the settling
            // speed of -2 m/s eats more than that in one tick. Without this
            // the player drops straight back onto the lip and sticks there.
            if (velocity.y < 0f)
                velocity.y = 0f;
        }

        /// <summary>
        /// Keeps a crouching player from walking off a ledge. The step is
        /// tried axis by axis, so running ALONG an edge still works and only
        /// the part pointing over it is dropped. In an inside corner neither
        /// axis survives and the player stops, still fully steerable.
        /// </summary>
        void GuardLedge(float dt)
        {
            Vector3 step = new Vector3(velocity.x, 0f, velocity.z) * dt;
            if (step.sqrMagnitude < 0.0000001f || HasFooting(step))
                return;

            if (step.x != 0f && HasFooting(new Vector3(step.x, 0f, 0f)))
            {
                velocity.z = 0f;
                return;
            }

            if (step.z != 0f && HasFooting(new Vector3(0f, 0f, step.z)))
            {
                velocity.x = 0f;
                return;
            }

            velocity.x = 0f;
            velocity.z = 0f;
        }

        /// <summary>
        /// Is there walkable ground under the player after this horizontal
        /// step? Thin probe down the body axis, so the player may stand with
        /// half the capsule over the edge - that is what it should feel like.
        /// </summary>
        bool HasFooting(Vector3 step)
        {
            float probeRadius = bodyRadius * 0.25f;
            float toFeet = currentHeight * 0.5f - probeRadius;
            Vector3 origin = transform.position + Vector3.up * CenterY + step;

            int count = Physics.SphereCastNonAlloc(
                origin, probeRadius, Vector3.down, groundHits,
                toFeet + ledgeGuardDrop, WorldMask,
                QueryTriggerInteraction.Ignore);

            float walkable = Mathf.Cos(slopeLimit * Mathf.Deg2Rad);

            for (int i = 0; i < count; i++)
            {
                // A steep face below is a slide, not footing.
                if (groundHits[i].normal.y >= walkable)
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
            if (touchingGround || velocity.y > 0f)
                return;

            float probeRadius = bodyRadius * 0.25f;
            float toFeet = currentHeight * 0.5f - probeRadius;
            Vector3 origin = transform.position + Vector3.up * CenterY;

            int count = Physics.SphereCastNonAlloc(
                origin, probeRadius, Vector3.down, groundHits,
                toFeet + groundSnapDistance, WorldMask,
                QueryTriggerInteraction.Ignore);

            float walkable = Mathf.Cos(slopeLimit * Mathf.Deg2Rad);
            float nearest = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                // Steep faces are not worth sticking to; that is the slide's
                // job, and gluing to one would kill it.
                if (groundHits[i].normal.y < walkable)
                    continue;

                if (groundHits[i].distance < nearest)
                    nearest = groundHits[i].distance;
            }

            if (nearest == float.MaxValue)
                return;

            // On a slope the capsule rests on the SIDE of its lower sphere,
            // so this reads too deep. That is the safe direction: Move stops
            // at the first contact, it never pushes through the surface.
            // Ground within snap range either way: either the feet are
            // pulled down onto it, or it already reaches them.
            float gap = nearest - toFeet;
            if (gap > 0f)
                MoveBody(Vector3.down * gap);

            touchingGround = true;
        }

        /// <summary>
        /// Remembers a move the world refused, so the next TryStepUp may
        /// measure a ledge it would otherwise have waved through. Compares
        /// what was asked for against what the sweep actually did.
        /// </summary>
        void UpdateWedged(Vector3 before, float dt)
        {
            Vector3 wanted = new Vector3(velocity.x, 0f, velocity.z) * dt;
            float asked = wanted.magnitude;
            if (asked < 0.0001f)
            {
                wedged = false;
                return;
            }

            Vector3 moved = transform.position - before;
            moved.y = 0f;

            // A quarter of what was asked: sliding along a wall still makes
            // progress, a capsule stuck on an edge makes almost none.
            wedged = moved.magnitude < asked * 0.25f;
        }

        /// <summary>
        /// Does the standing capsule fit at the current position? The body's
        /// own hull is a trigger on a masked-out layer, so it never shows up
        /// here and needs no check of its own.
        /// </summary>
        bool HasHeadroom()
        {
            float radius = bodyRadius;
            Vector3 feet = transform.position
                         + Vector3.up * (CenterY - currentHeight * 0.5f);

            Vector3 bottom = feet + Vector3.up * radius;
            Vector3 top = feet + Vector3.up * (standHeight - radius);

            // Slightly smaller radius, otherwise the check grazes walls the
            // player is currently running along.
            int count = Physics.OverlapCapsuleNonAlloc(
                bottom, top, radius * 0.95f, headroomHits,
                HeadroomMask, QueryTriggerInteraction.Ignore);

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
            // What PhysX actually touched during the last Move comes first.
            // On a slope the capsule rests on the SIDE of its lower sphere,
            // so the surface sits radius * (1/cos a - 1) below the body axis
            // - 0.30 m at 55 degrees - where no cast from the axis reaches.
            if (hasContactNormal)
            {
                groundNormal = contactNormal;
                groundAngle = Vector3.Angle(groundNormal, Vector3.up);
                return true;
            }

            // A quarter of the body radius stays well clear of anything the
            // player is standing against, and the cast length follows it so
            // the sphere still reaches below the feet.
            float probeRadius = bodyRadius * 0.25f;
            Vector3 origin = transform.position + Vector3.up * CenterY;

            float distance = currentHeight * 0.5f - probeRadius
                           + skinWidth + groundProbeExtra;

            int count = Physics.SphereCastNonAlloc(
                origin, probeRadius, Vector3.down, groundHits,
                distance, WorldMask, QueryTriggerInteraction.Ignore);

            groundNormal = Vector3.up;
            groundAngle = 0f;
            float nearest = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                // Walls and overhangs are not ground. Without this a vertical
                // face reads as 90 degrees and triggers the slide.
                if (groundHits[i].normal.y <= 0f)
                    continue;

                if (groundHits[i].distance >= nearest)
                    continue;

                nearest = groundHits[i].distance;
                groundNormal = groundHits[i].normal;
            }

            if (nearest == float.MaxValue)
                return false;

            groundAngle = Vector3.Angle(groundNormal, Vector3.up);
            return true;
        }

        /// <summary>
        /// Collects the surface holding the player up. The most upright
        /// contact wins: on a stair the tread beats the riser in front of it,
        /// which is what a body-wide cast used to get wrong.
        /// </summary>
        Vector3 EyeLocalPosition(float eyeHeightAboveFeet)
            => new Vector3(0f, eyeHeightAboveFeet - standHeight * 0.5f, 0f);

        Vector3 CurrentEyeWorldPosition()
            => cameraMount != null ? cameraMount.position : transform.position;

        Vector3 CurrentBodyCenter()
            => transform.position + Vector3.up * CenterY;
    }
}
