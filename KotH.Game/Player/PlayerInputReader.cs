using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Input;

namespace KotH.Client.Player
{
    /// <summary>
    /// Turns keyboard and mouse into a MoveInput, once per tick. The only
    /// class that reads input the SIMULATION consumes, so switching to
    /// network packets later means changing exactly one file. Collect runs
    /// every frame, ReadInput once per tick; presses are counted in between.
    /// </summary>
    [DataContract("KotH.PlayerInputReader")]
    public sealed class PlayerInputReader : SimulationBody
    {
        // Unity's "Mouse X" axis was 0.1 per pixel, times this: 0.2° a count.
        const float DegreesPerPixelAtOne = 0.1f;

        [Display("Mausempfindlichkeit", "Maus")] public float MouseSensitivity = 2f;
        [Display("Nick unten", "Maus")] public float MinPitch = -89f;
        [Display("Nick oben", "Maus")] public float MaxPitch = 89f;

        [Display("Sprint", "Tasten")] public Keys SprintKey = Keys.LeftShift;
        [Display("Ducken", "Tasten")] public Keys CrouchKey = Keys.E;

        float yaw;
        float pitch;
        byte jumpPresses;
        bool crouchPressed;
        byte attackPresses;

        // Held states, sampled at the last Collect; a tick reads them as the
        // frame before it left them, which is what Unity's GetKey gave.
        Vector2 move;
        bool jumpHeld, sprintHeld, crouchHeld;

        /// <summary>Stride angles: yaw about +Y, pitch positive looks up.</summary>
        [DataMemberIgnore] public float Yaw => yaw;
        [DataMemberIgnore] public float Pitch => pitch;

        protected override void Attach(Shared.Physics.WorldQueries world)
        {
            base.Attach(world);
            yaw = UMath.YawOf(Entity.Transform.Rotation);
        }

        /// <summary>
        /// Per frame: mouse look and every press since the last frame. The
        /// driver calls it before ticking, so a press reaches the very next
        /// tick. Mouse look only while the cursor is captured.
        /// </summary>
        public void Collect(InputManager input, bool captured)
        {
            if (captured && input.HasMouse)
            {
                // Pixels, not Stride's window-normalised delta: the sensitivity
                // must not change with the window size. Screen y points down.
                Vector2 pixels = input.MouseDelta * input.Mouse.SurfaceSize;
                float scale = DegreesPerPixelAtOne * MouseSensitivity;
                yaw -= pixels.X * scale;
                pitch = UMath.Clamp(pitch - pixels.Y * scale, MinPitch, MaxPitch);

                // Only while captured: the click that grabs the mouse back
                // never lands a swing.
                if (input.IsMouseButtonPressed(MouseButton.Left) && attackPresses < byte.MaxValue)
                    attackPresses++;
            }

            // Counted rather than latched, so two presses inside one 25 ms
            // tick both arrive - the double jump depends on that.
            if (input.IsKeyPressed(Keys.Space) && jumpPresses < byte.MaxValue)
                jumpPresses++;

            if (input.IsKeyPressed(CrouchKey))
                crouchPressed = true;

            move = new Vector2(Axis(input, Keys.D, Keys.Right, Keys.A, Keys.Left),
                               Axis(input, Keys.W, Keys.Up, Keys.S, Keys.Down));
            jumpHeld = input.IsKeyDown(Keys.Space);
            sprintHeld = input.IsKeyDown(SprintKey);
            crouchHeld = input.IsKeyDown(CrouchKey);
        }

        /// <summary>Unity's GetAxisRaw with snap: -1, 0 or 1.</summary>
        static float Axis(InputManager input, Keys positive, Keys altPositive, Keys negative, Keys altNegative)
        {
            float v = 0f;
            if (input.IsKeyDown(positive) || input.IsKeyDown(altPositive))
                v += 1f;
            if (input.IsKeyDown(negative) || input.IsKeyDown(altNegative))
                v -= 1f;
            return v;
        }

        public override MoveInput ReadInput()
        {
            var result = new MoveInput
            {
                Move = move,
                Yaw = yaw,
                Pitch = pitch,
                JumpPresses = jumpPresses,
                AttackPresses = attackPresses,
                JumpHeld = jumpHeld,
                CrouchPressed = crouchPressed,
                Sprint = sprintHeld,
                Crouch = crouchHeld,
            };

            // Cleared on read, so a press is consumed by exactly one tick.
            jumpPresses = 0;
            attackPresses = 0;
            crouchPressed = false;
            return result;
        }
    }
}
