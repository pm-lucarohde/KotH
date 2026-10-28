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
        // Measured against the Unity build with a virtual uinput mouse: 400
        // counts turned it 20° at sensitivity 2, linear, so its Linux "Mouse X"
        // gave a quarter of the 0.1 per count its axis setting suggests.
        // SDL's relative mode here delivers whole counts.
        const float DegreesPerPixelAtOne = 0.025f;

        [Display("Mausempfindlichkeit", "Maus")] public float MouseSensitivity = 2f;
        [Display("Nick unten", "Maus")] public float MinPitch = -89f;
        [Display("Nick oben", "Maus")] public float MaxPitch = 89f;

        [Display("Sprint", "Tasten")] public Keys SprintKey = Keys.LeftShift;
        [Display("Ducken", "Tasten")] public Keys CrouchKey = Keys.E;

        /// <userdoc>Wie schnell sich die Figur in Third-Person zur Laufrichtung dreht, in Grad pro Sekunde</userdoc>
        [Display("Drehtempo", "Third-Person")] public float TurnSpeed = 720f;

        // yaw and pitch are the view; bodyYaw is where the figure faces. In
        // first person both yaws are one, in third person the view circles
        // freely and the figure turns to where it runs.
        float yaw;
        float pitch;
        float bodyYaw;
        bool thirdPerson;
        int facedTick = -1;

        // Third person: the point under the crosshair, from the camera's last
        // frame, and the pitch a swing this tick goes out at.
        Vector3? aimTarget;
        float sentPitch;
        PlayerMotor motor;
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

        /// <summary>Whether the view sits behind the figure instead of in its eyes.</summary>
        [DataMemberIgnore] public bool ThirdPerson
        {
            get => thirdPerson;
            set => thirdPerson = value;
        }

        /// <summary>Points the view, as a test aid: screenshots at a fixed angle.</summary>
        public void Look(float yawDegrees, float pitchDegrees)
        {
            yaw = yawDegrees;
            pitch = UMath.Clamp(pitchDegrees, MinPitch, MaxPitch);
        }

        /// <summary>Keys held from now on, as a test aid: animations seen in a screenshot run.</summary>
        public void Hold(Vector2 keys, bool sprint)
        {
            heldKeys = keys;
            heldSprint = sprint;
        }

        Vector2? heldKeys;
        bool heldSprint, heldCrouch;

        /// <summary>Crouch pressed now and held from then on, as a test aid: crouching and sliding seen in a screenshot.</summary>
        public void HoldCrouch()
        {
            crouchPressed = true;
            heldCrouch = true;
        }

        /// <summary>One swing in the next tick, as a test aid: hits checked from a screenshot run.</summary>
        public void Swing()
        {
            if (attackPresses < byte.MaxValue)
                attackPresses++;
        }

        /// <summary>One jump press in the next tick, as a test aid: jumps seen in a screenshot.</summary>
        public void Jump()
        {
            if (jumpPresses < byte.MaxValue)
                jumpPresses++;
        }

        protected override void Attach(Shared.Physics.WorldQueries world)
        {
            base.Attach(world);
            yaw = bodyYaw = UMath.YawOf(Entity.Transform.Rotation);
            motor = Entity.Get<PlayerMotor>();
        }

        /// <summary>What the crosshair points at in third person, null in first person.</summary>
        public void AimAt(Vector3? target) => aimTarget = target;

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

                // Wheel back steps out of the eyes, forward back into them.
                if (input.MouseWheelDelta < 0f)
                    thirdPerson = true;
                else if (input.MouseWheelDelta > 0f)
                    thirdPerson = false;
            }

            // Counted rather than latched, so two presses inside one 25 ms
            // tick both arrive - the double jump depends on that. Repeats of
            // a held key are no presses, see KeyPresses.
            jumpPresses = (byte)System.Math.Min(byte.MaxValue, jumpPresses + KeyPresses.Count(input, Keys.Space));

            if (KeyPresses.Pressed(input, CrouchKey))
                crouchPressed = true;

            move = new Vector2(Axis(input, Keys.D, Keys.Right, Keys.A, Keys.Left),
                               Axis(input, Keys.W, Keys.Up, Keys.S, Keys.Down));
            jumpHeld = input.IsKeyDown(Keys.Space);
            sprintHeld = input.IsKeyDown(SprintKey);
            crouchHeld = input.IsKeyDown(CrouchKey);
            if (heldKeys.HasValue)
            {
                move = heldKeys.Value;
                sprintHeld |= heldSprint;
            }
            crouchHeld |= heldCrouch;
        }

        /// <summary>
        /// Per frame instead of Collect while a menu lies over the game: no
        /// key held, no press, the view stays where it was.
        /// </summary>
        public void Rest()
        {
            move = Vector2.Zero;
            jumpHeld = sprintHeld = crouchHeld = false;
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
            // Put somewhere new by the round: look where the round says, at the hill.
            if (PlacedTick != facedTick)
            {
                facedTick = PlacedTick;
                yaw = bodyYaw = PlacedYaw;
                pitch = 0f;
            }
            Vector2 wish = move;
            sentPitch = pitch;
            if (!thirdPerson)
                bodyYaw = yaw;
            else
                wish = FaceAndRun(move);

            var result = new MoveInput
            {
                Move = wish,
                Yaw = bodyYaw,
                Pitch = sentPitch,
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

        /// <summary>
        /// The swing starts at the eye, the camera sits above and behind it:
        /// turned to the point under the crosshair instead of along the view,
        /// it hits what the crosshair shows. Straight along the view if that
        /// point is unknown or not ahead of the eye.
        /// </summary>
        void AimSwing()
        {
            bodyYaw = yaw;
            if (aimTarget == null || motor == null)
                return;

            Vector3 d = UMath.Sub(aimTarget.Value, motor.EyePosition);
            Vector3 view = UMath.Rotate(UMath.Euler(pitch, yaw), UMath.Forward);
            if (UMath.Dot(d, view) < 0.1f)
                return;

            // Forward is -Z: yaw 0 looks along -Z, -90 along +X.
            float flat = UMath.Sqrt(d.X * d.X + d.Z * d.Z);
            bodyYaw = yaw + UMath.DeltaAngle(yaw, UMath.Atan2(-d.X, -d.Z) * UMath.Rad2Deg);
            sentPitch = UMath.Clamp(UMath.Atan2(d.Y, flat) * UMath.Rad2Deg, MinPitch, MaxPitch);
        }

        /// <summary>
        /// Third person: a swing turns the figure at once to what the
        /// crosshair shows, the keys turn it towards where they run. The returned move is that run
        /// in the figure's own frame, so it goes exactly where the view says
        /// while the figure is still turning.
        /// </summary>
        Vector2 FaceAndRun(Vector2 keys)
        {
            if (attackPresses > 0)
                AimSwing();

            if (UMath.SqrMagnitude(keys) < 0.0001f)
                return keys;

            Quaternion view = UMath.EulerYaw(yaw);
            Vector3 run = UMath.Add(UMath.Mul(UMath.Rotate(view, UMath.Right), keys.X),
                                    UMath.Mul(UMath.Rotate(view, UMath.Forward), keys.Y));

            if (attackPresses == 0)
            {
                // Right is -90 degrees: yaw turns about +Y, to the left.
                float heading = yaw + UMath.Atan2(-keys.X, keys.Y) * UMath.Rad2Deg;
                float step = TurnSpeed * SimulationSystem.TickSeconds;
                bodyYaw += UMath.Clamp(UMath.DeltaAngle(bodyYaw, heading), -step, step);
            }

            Quaternion body = UMath.EulerYaw(bodyYaw);
            return new Vector2(UMath.Dot(run, UMath.Rotate(body, UMath.Right)),
                               UMath.Dot(run, UMath.Rotate(body, UMath.Forward)));
        }
    }
}
