using UnityEngine;

namespace KotH.Shared.Player
{
    /// <summary>
    /// Moves the player; the only place that touches CharacterController.
    /// Reads no input and knows no camera - a dedicated server has neither.
    /// Mutable state is kept in a few fields so it can be replayed later.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
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
        [SerializeField] float jumpHeight = 1.5f;
        [SerializeField] float doubleJumpHeight = 1f;

        // Opens on the ground jump. A double tap only counts inside this time.
        [SerializeField] float doubleJumpWindow = 0.5f;

        // Maximum gap between the two taps of the double tap.
        [SerializeField] float doubleTapWindow = 0.25f;

        // Deliberately about twice real gravity; at -9.81 a jump feels floaty.
        // The project setting is untouched and still applies to rigidbodies.
        [SerializeField] float gravity = -20f;

        // Eyes sit 0.12 m below the crown in both poses, as on a real body.
        [Header("Körpermaße (m)")]
        [SerializeField] float standHeight = 1.8f;
        [SerializeField] float crouchHeight = 0.9f;
        [SerializeField] float standEyeHeight = 1.68f;
        [SerializeField] float crouchEyeHeight = 0.78f;

        // Drives collision height and eye together, in m/s of height: the
        // 0.90 m between the poses take 0.15 s. One rate, because two would
        // let the eye leave the body it belongs to.
        [SerializeField] float poseChangeSpeed = 6f;

        // Step height the controller climbs. Crouched it is much lower, so
        // ducking becomes a real trade: you fit through gaps but lose reach.
        [SerializeField] float standStepOffset = 0.45f;
        [SerializeField] float crouchStepOffset = 0.25f;

        // Crouching guards against falling: a step that would leave nothing
        // walkable this far below the feet is refused. Deliberately larger
        // than groundSnapDistance, so a drop is either caught or refused.
        [SerializeField] float ledgeGuardDrop = 0.5f;
        [SerializeField] LayerMask headroomMask = ~0;

        [Header("Hang")]
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

        [Header("Verknüpfung")]
        [SerializeField] Transform cameraMount;

        CharacterController controller;

        // --- mutable state ---
        Vector3 velocity;
        bool isCrouching;         // the INTENT, set by the key, not the shape
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
        bool doubleJumpUsed;      // once per flight
        bool jumpWindowOpen;      // window since the ground jump
        float timeSinceGroundJump;
        bool hasPendingAirPress;  // first air tap waiting for its partner
        float timeSinceAirPress;

        // --- render interpolation only, not part of the simulation ---
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
        public float Height => controller.height;
        public float Radius => controller.radius;
        public bool IsGrounded => grounded;
        public bool IsSliding => onSteepGround;
        public float GroundAngle => groundAngle;
        public bool IsCrouching => isCrouching;

        void Awake()
        {
            // GetComponent belongs here, not in Tick and not in Update.
            controller = GetComponent<CharacterController>();

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
            bool touching = controller.isGrounded;
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
                && (!knownGround || groundAngle > controller.slopeLimit);
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

            UpdateCrouch(input.Crouch, dt);
            UpdateHorizontal(input, dt);

            // A jump this tick wins over the slide: otherwise the redirect
            // would project the fresh upward speed straight back into the
            // slope and the double jump would be dead on arrival.
            bool jumped = ProcessJumpPresses(input.JumpPresses);

            if (onSteepGround && !jumped)
                Slide(dt);
            else
                velocity.y += gravity * dt;

            // Cleared here so the contacts collected below belong to this
            // tick's movement, both the step-up lift and the main Move.
            // After the jump on purpose: a jump leaves the ledge
            // deliberately and must keep its horizontal speed.
            if (isCrouching && grounded && !jumped)
                GuardLedge(dt);

            hasContactNormal = false;
            steppedUp = false;

            Vector3 beforeMove = transform.position;

            TryStepUp(dt);
            controller.Move(velocity * dt);
            UpdateWedged(beforeMove, dt);

            // Only ground that was walkable at the start of the tick is worth
            // keeping. A jump clears grounded itself, so it is never undone,
            // and a step just climbed must not be pulled back down either.
            if (grounded && !steppedUp)
                SnapToGround();

            eyePosition = CurrentEyeWorldPosition();
            bodyCenter = CurrentBodyCenter();
        }

        void UpdateHorizontal(in MoveInput input, float dt)
        {
            // Without clamping, moving diagonally would be 1.41x faster.
            Vector2 move = Vector2.ClampMagnitude(input.Move, 1f);
            bool hasInput = move.sqrMagnitude > 0.0001f;

            Vector3 wish = transform.right * move.x + transform.forward * move.y;

            float speed = isCrouching ? crouchSpeed
                        : input.Sprint ? sprintSpeed
                        : walkSpeed;

            // No input while airborne means rate 0, so momentum is preserved.
            // Anything else would be a hidden air brake on key release.
            float rate = grounded
                ? (hasInput ? groundAcceleration : groundDeceleration)
                : (hasInput ? airAcceleration : 0f);

            Vector3 current = new Vector3(velocity.x, 0f, velocity.z);
            Vector3 next = Vector3.MoveTowards(current, wish * speed, rate * dt);

            velocity.x = next.x;
            velocity.z = next.z;
        }

        /// <summary>
        /// Slides down a face that is too steep to stand on. Called instead
        /// of plain gravity, never on top of it.
        /// </summary>
        void Slide(float dt)
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
        /// The only place that reshapes the controller. Shifts the center so
        /// the feet stay put, otherwise the player would sink when crouching.
        /// Called every tick while the pose changes, not once per keypress.
        /// </summary>
        void ApplyHeight(float height)
        {
            controller.height = height;
            controller.center = new Vector3(0f, (height - standHeight) * 0.5f, 0f);

            // The standing budget only once fully upright: anywhere in
            // between, the crouched one holds, or tapping crouch would grant
            // the tall reach for a few ticks at crouch collision height.
            maxStepHeight = height >= standHeight ? standStepOffset : crouchStepOffset;

            // PhysX gets no step budget at all. Its own stepping resolves
            // corner contacts by lifting the capsule, which lets a player who
            // wiggles the view ratchet up walls far above the limit.
            controller.stepOffset = 0f;
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

            float walkable = Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad);
            Vector3 forward = step / travel;
            float reach = travel + controller.skinWidth + stepProbeAhead;
            float radius = controller.radius;

            Vector3 feet = transform.position + controller.center
                         - Vector3.up * (controller.height * 0.5f);
            Vector3 bottom = feet + Vector3.up * radius;
            Vector3 top = feet + Vector3.up * (controller.height - radius);

            // Classify the obstacle from ankle height. Asking the body
            // capsule is unreliable: its lower sphere rides over the edge of
            // a step and reports an almost flat normal, which made crouched
            // stepping fail while standing happened to work.
            Vector3 ankle = feet + Vector3.up * 0.01f;
            if (!Physics.Raycast(ankle, forward, out RaycastHit riser, reach + radius,
                    groundMask, QueryTriggerInteraction.Ignore))
                return;

            // A walkable face is a ramp and the controller handles those
            // itself -- unless it demonstrably did not. On the convex edge
            // where a slope meets its top the capsule jams, and the ankle
            // ray only ever sees the last centimetres of ramp.
            if (riser.normal.y > walkable && !wedged)
                return;

            // Room for the whole body one step higher? This is the check that
            // stops the corner exploit: a step that is too tall still blocks.
            Vector3 lift = Vector3.up * maxStepHeight;
            if (Physics.CapsuleCast(bottom + lift, top + lift, radius, forward,
                    out _, reach, groundMask, QueryTriggerInteraction.Ignore))
                return;

            // Measure the ledge from above rather than trusting the wall hit.
            // The ankle ray starts at the body axis, so its distance already
            // includes the radius.
            Vector3 ahead = feet + lift
                          + forward * (riser.distance + stepProbeAhead);

            if (!Physics.Raycast(ahead, Vector3.down, out RaycastHit ledge,
                    maxStepHeight, groundMask, QueryTriggerInteraction.Ignore))
                return;

            if (ledge.normal.y < walkable)
                return;

            float rise = maxStepHeight - ledge.distance;
            if (rise <= 0.001f)
                return;

            controller.Move(Vector3.up * (rise + controller.skinWidth));

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
            float probeRadius = controller.radius * 0.25f;
            float toFeet = controller.height * 0.5f - probeRadius;
            Vector3 origin = transform.position + controller.center + step;

            int count = Physics.SphereCastNonAlloc(
                origin, probeRadius, Vector3.down, groundHits,
                toFeet + ledgeGuardDrop, groundMask,
                QueryTriggerInteraction.Ignore);

            float walkable = Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad);

            for (int i = 0; i < count; i++)
            {
                // The cast starts inside the player's own capsule.
                if (groundHits[i].collider == controller)
                    continue;

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
            if (controller.isGrounded || velocity.y > 0f)
                return;

            float probeRadius = controller.radius * 0.25f;
            float toFeet = controller.height * 0.5f - probeRadius;
            Vector3 origin = transform.position + controller.center;

            int count = Physics.SphereCastNonAlloc(
                origin, probeRadius, Vector3.down, groundHits,
                toFeet + groundSnapDistance, groundMask,
                QueryTriggerInteraction.Ignore);

            float walkable = Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad);
            float nearest = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                // The cast starts inside the player's own capsule.
                if (groundHits[i].collider == controller)
                    continue;

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
            float gap = nearest - toFeet;
            if (gap > 0f)
                controller.Move(Vector3.down * gap);
        }

        /// <summary>
        /// Remembers a move the world refused, so the next TryStepUp may
        /// measure a ledge it would otherwise have waved through. Compares
        /// what was asked for against what the controller actually did.
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
        /// Does the standing capsule fit at the current position? The player's
        /// own controller does not count as an obstacle.
        /// </summary>
        bool HasHeadroom()
        {
            float radius = controller.radius;
            Vector3 feet = transform.position
                         + Vector3.up * (controller.center.y - controller.height * 0.5f);

            Vector3 bottom = feet + Vector3.up * radius;
            Vector3 top = feet + Vector3.up * (standHeight - radius);

            // Slightly smaller radius, otherwise the check grazes walls the
            // player is currently running along.
            int count = Physics.OverlapCapsuleNonAlloc(
                bottom, top, radius * 0.95f, headroomHits,
                headroomMask, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                if (headroomHits[i] != controller)
                    return false;
            }

            return true;
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
            float probeRadius = controller.radius * 0.25f;
            Vector3 origin = transform.position + controller.center;

            float distance = controller.height * 0.5f - probeRadius
                           + controller.skinWidth + groundProbeExtra;

            int count = Physics.SphereCastNonAlloc(
                origin, probeRadius, Vector3.down, groundHits,
                distance, groundMask, QueryTriggerInteraction.Ignore);

            groundNormal = Vector3.up;
            groundAngle = 0f;
            float nearest = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                // The cast starts inside the player's own capsule, so its
                // collider always shows up at distance zero.
                if (groundHits[i].collider == controller)
                    continue;

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
        void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (hit.normal.y <= 0f)
                return;

            if (!hasContactNormal || hit.normal.y > contactNormal.y)
            {
                contactNormal = hit.normal;
                hasContactNormal = true;
            }
        }

        Vector3 EyeLocalPosition(float eyeHeightAboveFeet)
            => new Vector3(0f, eyeHeightAboveFeet - standHeight * 0.5f, 0f);

        Vector3 CurrentEyeWorldPosition()
            => cameraMount != null ? cameraMount.position : transform.position;

        Vector3 CurrentBodyCenter()
            => transform.position + Vector3.up * controller.center.y;
    }
}
