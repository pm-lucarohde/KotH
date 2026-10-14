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

        [Header("Körpermaße (m)")]
        [SerializeField] float standHeight = 2f;
        [SerializeField] float crouchHeight = 1.2f;
        [SerializeField] float standEyeHeight = 1.7f;
        [SerializeField] float crouchEyeHeight = 1f;
        [SerializeField] float eyeMoveSpeed = 6f;
        [SerializeField] LayerMask headroomMask = ~0;

        [Header("Verknüpfung")]
        [SerializeField] Transform cameraMount;

        CharacterController controller;

        // --- mutable state ---
        Vector3 velocity;
        bool isCrouching;
        bool grounded;
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

        public Vector3 EyePosition => eyePosition;
        public Vector3 PreviousEyePosition => previousEyePosition;
        public Vector3 BodyCenter => bodyCenter;
        public Vector3 PreviousBodyCenter => previousBodyCenter;
        public float Height => controller.height;
        public bool IsGrounded => grounded;
        public bool IsCrouching => isCrouching;

        void Awake()
        {
            // GetComponent belongs here, not in Tick and not in Update.
            controller = GetComponent<CharacterController>();

            ApplyHeight(standHeight);
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

            // isGrounded reflects the result of the last Move.
            grounded = controller.isGrounded;
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
            ProcessJumpPresses(input.JumpPresses);

            velocity.y += gravity * dt;

            controller.Move(velocity * dt);

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
        /// Ground jump and double jump. The counter can exceed 1 when the key
        /// was pressed more than once inside a single 25 ms tick.
        /// </summary>
        void ProcessJumpPresses(int presses)
        {
            for (int i = 0; i < presses; i++)
            {
                // Jumping while crouched is allowed; the player stays crouched
                // and keeps the slower crouch speed.
                if (grounded)
                {
                    velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
                    grounded = false;
                    doubleJumpUsed = false;

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
                }

                hasPendingAirPress = false;
            }
        }

        void UpdateCrouch(bool wantsCrouch, float dt)
        {
            if (wantsCrouch && !isCrouching)
            {
                isCrouching = true;
                ApplyHeight(crouchHeight);
            }
            else if (!wantsCrouch && isCrouching && HasHeadroom())
            {
                isCrouching = false;
                ApplyHeight(standHeight);
            }

            if (cameraMount == null)
                return;

            Vector3 target = EyeLocalPosition(isCrouching ? crouchEyeHeight : standEyeHeight);
            cameraMount.localPosition =
                Vector3.MoveTowards(cameraMount.localPosition, target, eyeMoveSpeed * dt);
        }

        /// <summary>
        /// Sets the height and shifts the center so the feet stay put.
        /// Without that correction the player would sink when crouching.
        /// </summary>
        void ApplyHeight(float height)
        {
            controller.height = height;
            controller.center = new Vector3(0f, (height - standHeight) * 0.5f, 0f);
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

        Vector3 EyeLocalPosition(float eyeHeightAboveFeet)
            => new Vector3(0f, eyeHeightAboveFeet - standHeight * 0.5f, 0f);

        Vector3 CurrentEyeWorldPosition()
            => cameraMount != null ? cameraMount.position : transform.position;

        Vector3 CurrentBodyCenter()
            => transform.position + Vector3.up * controller.center.y;
    }
}
