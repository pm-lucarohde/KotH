using KotH.Shared.Player;
using KotH.Shared.Simulation;
using UnityEngine;

namespace KotH.Client.Player
{
    /// <summary>
    /// Turns keyboard and mouse into a MoveInput, once per tick. The only
    /// class that reads input the SIMULATION consumes, so switching to the
    /// new Input System or to network packets later means changing exactly
    /// one file. Debug keys and menus read their own keys; they never feed
    /// the motor and never go over the wire. SimulationSystem does the
    /// ticking; switching this component off takes the player out of it.
    /// </summary>
    public sealed class PlayerInputReader : SimulationBody
    {
        [Header("Maus")]
        [SerializeField] float mouseSensitivity = 2f;
        [SerializeField] float minPitch = -89f;
        [SerializeField] float maxPitch = 89f;

        [Header("Tasten")]
        [SerializeField] KeyCode sprintKey = KeyCode.LeftShift;
        [SerializeField] KeyCode crouchKey = KeyCode.LeftControl;

        float yaw;
        float pitch;
        byte jumpPresses;

        public float Yaw => yaw;
        public float Pitch => pitch;

        protected override void Awake()
        {
            base.Awake();
            yaw = transform.eulerAngles.y;
            SetCursorLocked(true);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            SetCursorLocked(false);
        }

        // Update runs per frame, ReadInput once per tick. At 144 fps and
        // 40 Hz that is about three and a half frames per tick, so anything
        // that happens per frame has to be collected here.
        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                // First press frees the mouse, a second one quits. Two
                // presses, so it never fires by accident in a fullscreen test.
                if (Cursor.lockState == CursorLockMode.Locked)
                    SetCursorLocked(false);
                else
                    Quit();
            }
            else if (Input.GetMouseButtonDown(0) && Cursor.lockState != CursorLockMode.Locked)
                SetCursorLocked(true);

            if (Cursor.lockState == CursorLockMode.Locked)
            {
                // The mouse axes already are a per-frame delta, so NO
                // Time.deltaTime here. Multiplying is the most common mistake
                // in Unity mouse look and makes it frame rate dependent.
                yaw += Input.GetAxisRaw("Mouse X") * mouseSensitivity;
                pitch = Mathf.Clamp(
                    pitch - Input.GetAxisRaw("Mouse Y") * mouseSensitivity,
                    minPitch, maxPitch);
            }

            // GetButtonDown is true for a single frame only. Counted rather
            // than latched, so two presses inside one 25 ms tick both arrive -
            // the double jump depends on that.
            if (Input.GetButtonDown("Jump") && jumpPresses < byte.MaxValue)
                jumpPresses++;
        }

        public override MoveInput ReadInput()
        {
            var input = new MoveInput
            {
                Move = new Vector2(
                    Input.GetAxisRaw("Horizontal"),
                    Input.GetAxisRaw("Vertical")),
                Yaw = yaw,
                Pitch = pitch,
                JumpPresses = jumpPresses,
                // Held states may be read here directly: GetKey is stable
                // across frames, unlike GetKeyDown.
                Sprint = Input.GetKey(sprintKey),
                Crouch = Input.GetKey(crouchKey),
            };

            // Cleared on read, so a press is consumed by exactly one tick.
            jumpPresses = 0;

            return input;
        }

        /// <summary>
        /// Leaves play mode in the editor instead of closing it, which
        /// Application.Quit would do. A placeholder until there is a menu.
        /// </summary>
        static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
