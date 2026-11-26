using KotH.Client.Gamepad;
using KotH.Client.Settings;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Input;

namespace KotH.Client.Player
{
    /// <summary>Turns keyboard, mouse and gamepad into a MoveInput, once per tick. The only class
    /// that reads input the SIMULATION consumes, so switching to network packets later
    /// means changing exactly one file. Collect runs every frame, ReadInput once per
    /// tick; presses are counted in between. Keys and mouse sensitivity come from the settings.</summary>
    [DataContract("KotH.PlayerInputReader")]
    public sealed class PlayerInputReader : SimulationBody
    {
        // Measured against the Unity build with a virtual uinput mouse: 400
        // counts turned it 20° at sensitivity 2, linear, so its Linux "Mouse X"
        // gave a quarter of the 0.1 per count its axis setting suggests.
        // SDL's relative mode here delivers whole counts.
        const float DegreesPerPixelAtOne = 0.025f;

        // Gamepad look at sensitivity 1, degrees per second with the stick at the rim; the stick's
        // length is raised to LookCurve, so a small tilt aims finely. Held at the rim sideways for
        // TurnBoostAfter, the turn speeds up to TurnBoost times over as long again.
        const float PadYawSpeed = 260f, PadPitchSpeed = 170f, LookCurve = 2f, TurnBoost = 1.7f, TurnBoostAfter = 0.3f;
        float rimSeconds;

        [Display("Pitch down", "Mouse")] public float MinPitch = -89f;
        [Display("Pitch up", "Mouse")] public float MaxPitch = 89f;

        /// <userdoc>How fast the figure turns to its running direction in third person, in degrees per second</userdoc>
        [Display("Turn speed", "Third person")] public float TurnSpeed = 720f;

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

        // Blaster in hand, else the cane: keys 1 and 2 switch, nothing else does.
        bool blaster;

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

        /// <summary>Test aid: while it gives an input, that is sent instead of keys and mouse, and the
        /// view follows it (a bot flying the own figure in measured rounds).</summary>
        [DataMemberIgnore] public System.Func<MoveInput?> Pilot { get; set; }

        // The pilot's look one tick before, so the view blends between ticks as the figure does.
        float pilotYaw, pilotPitch;
        bool piloted;

        /// <summary>The view the camera shows: the mouse's, or with a pilot its look blended over the
        /// last tick (it changes only 40 times a second); alpha as TickInterpolation.Alpha.</summary>
        public (float Yaw, float Pitch) ViewAt(float alpha)
            => piloted ? (pilotYaw + System.MathF.IEEERemainder(yaw - pilotYaw, 360f) * alpha, pilotPitch + (pitch - pilotPitch) * alpha)
                       : (yaw, pitch);

        /// <summary>Keys held from now on, as a test aid: animations seen in a screenshot run.</summary>
        public void Hold(Vector2 keys, bool sprint)
        {
            heldKeys = keys;
            heldSprint = sprint;
        }

        Vector2? heldKeys;
        bool heldSprint, heldCrouch;

        /// <summary>Crouch pressed once, not held, as a test aid: a dash in the air.</summary>
        public void TapCrouch() => crouchPressed = true;

        /// <summary>Crouch pressed now and held from then on, as a test aid: crouching and sliding seen in a screenshot.</summary>
        public void HoldCrouch()
        {
            crouchPressed = true;
            heldCrouch = true;
        }

        /// <summary>The blaster in hand (key 2) or the cane (key 1); set as a test aid.</summary>
        [DataMemberIgnore] public bool Blaster
        {
            get => blaster;
            set => blaster = value;
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

        /// <summary>Left clicks while captured, counted by the view alone (watching: the next player).</summary>
        [DataMemberIgnore] public int Clicks { get; private set; }

        /// <summary>What the crosshair points at in third person, null in first person.</summary>
        public void AimAt(Vector3? target) => aimTarget = target;

        /// <summary>Per frame: mouse look and every press since the last frame. The
        /// driver calls it before ticking, so a press reaches the very next tick. Mouse
        /// look only while the cursor is captured.</summary>
        public void Collect(InputManager input, bool captured, float seconds)
        {
            PlayerSettings settings = ClientSettings.Applied;
            if (captured)
                PadLook(settings, seconds);
            if (captured && input.HasMouse)
            {
                // Pixels, not Stride's window-normalised delta: the sensitivity
                // must not change with the window size. Screen y points down.
                Vector2 pixels = input.MouseDelta * input.Mouse.SurfaceSize;
                float scale = DegreesPerPixelAtOne * settings.MouseSensitivity;
                yaw -= pixels.X * scale;
                pitch = UMath.Clamp(pitch - pixels.Y * scale, MinPitch, MaxPitch);

                // Only while captured: the click that grabs the mouse back
                // never lands a swing.
                if (input.IsMouseButtonPressed(MouseButton.Left) && attackPresses < byte.MaxValue)
                    attackPresses++;
                if (input.IsMouseButtonPressed(MouseButton.Left))
                    Clicks++;

                // Wheel back steps out of the eyes, forward back into them.
                if (input.MouseWheelDelta < 0f)
                    thirdPerson = true;
                else if (input.MouseWheelDelta > 0f)
                    thirdPerson = false;
            }

            // Counted rather than latched, so two presses inside one 25 ms
            // tick both arrive - the double jump depends on that. Repeats of
            // a held key are no presses, see KeyPresses.
            Keys jump = settings.Key(Binding.Jump), crouch = settings.Key(Binding.Crouch);
            jumpPresses = (byte)System.Math.Min(byte.MaxValue, jumpPresses + KeyPresses.Count(input, jump));

            if (KeyPresses.Pressed(input, crouch))
                crouchPressed = true;

            // The cane (1) or the blaster (2); the choice stays until the other key.
            if (Pressed(input, settings.Key(Binding.Cane)) || input.IsKeyPressed(Keys.NumPad1))
                blaster = false;
            else if (Pressed(input, settings.Key(Binding.Blaster)) || input.IsKeyPressed(Keys.NumPad2))
                blaster = true;

            move = new Vector2(Axis(input, settings.Key(Binding.Right), Keys.Right, settings.Key(Binding.Left), Keys.Left),
                               Axis(input, settings.Key(Binding.Forward), Keys.Up, settings.Key(Binding.Back), Keys.Down));
            jumpHeld = Down(input, jump);
            sprintHeld = Down(input, settings.Key(Binding.Sprint));
            crouchHeld = Down(input, crouch);
            if (captured)
                PadButtons(settings);
            if (heldKeys.HasValue)
            {
                move = heldKeys.Value;
                sprintHeld |= heldSprint;
            }
            crouchHeld |= heldCrouch;
        }

        /// <summary>The gamepad's buttons as the keys: counted presses, held states, the weapon and the
        /// camera switched. The left stick walks when no walking key is held, analog.</summary>
        void PadButtons(PlayerSettings settings)
        {
            if (!Pad.Connected)
                return;
            if (Pad.Pressed(settings.Button(PadAction.Jump)) && jumpPresses < byte.MaxValue)
                jumpPresses++;
            if (Pad.Pressed(settings.Button(PadAction.Crouch)))
                crouchPressed = true;
            if (Pad.Pressed(settings.Button(PadAction.Attack)))
            {
                if (attackPresses < byte.MaxValue)
                    attackPresses++;
                Clicks++;
            }
            if (Pad.Pressed(settings.Button(PadAction.Weapon)))
                blaster = !blaster;
            if (Pad.Pressed(settings.Button(PadAction.View)))
                thirdPerson = !thirdPerson;
            jumpHeld |= Pad.Down(settings.Button(PadAction.Jump));
            sprintHeld |= Pad.Down(settings.Button(PadAction.Sprint));
            crouchHeld |= Pad.Down(settings.Button(PadAction.Crouch));
            if (move == Vector2.Zero)
                move = Pad.Left;
        }

        /// <summary>The right stick turns the view, per frame like the mouse.</summary>
        void PadLook(PlayerSettings settings, float seconds)
        {
            Vector2 stick = Pad.Right;
            float length = stick.Length();
            if (length <= 0f)
            {
                rimSeconds = 0f;
                return;
            }
            rimSeconds = System.Math.Abs(stick.X) > 0.95f ? rimSeconds + seconds : 0f;
            float boost = 1f + (TurnBoost - 1f) * System.Math.Clamp((rimSeconds - TurnBoostAfter) / TurnBoostAfter, 0f, 1f);
            Vector2 look = stick * (System.MathF.Pow(length, LookCurve) / length) * (settings.StickSensitivity * seconds);
            yaw -= look.X * PadYawSpeed * boost;
            pitch = UMath.Clamp(pitch + look.Y * PadPitchSpeed * (settings.InvertY ? -1f : 1f), MinPitch, MaxPitch);
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
            if (Down(input, positive) || input.IsKeyDown(altPositive))
                v += 1f;
            if (Down(input, negative) || input.IsKeyDown(altNegative))
                v -= 1f;
            return v;
        }

        // An unbound action (None) is never held or pressed.
        static bool Down(InputManager input, Keys key) => key != Keys.None && input.IsKeyDown(key);

        static bool Pressed(InputManager input, Keys key) => key != Keys.None && input.IsKeyPressed(key);

        public override MoveInput ReadInput()
        {
            // Put somewhere new by the round: look where the round says, at the hill.
            if (PlacedTick != facedTick)
            {
                facedTick = PlacedTick;
                yaw = bodyYaw = PlacedYaw;
                pitch = 0f;
            }
            if (Pilot?.Invoke() is { } flown)
            {
                (pilotYaw, pilotPitch) = piloted ? (yaw, pitch) : (flown.Yaw, flown.Pitch);
                piloted = true;
                yaw = bodyYaw = flown.Yaw;
                pitch = flown.Pitch;
                blaster = flown.Blaster;
                jumpPresses = attackPresses = 0;
                crouchPressed = false;
                return flown;
            }
            piloted = false;
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
                Blaster = blaster,
            };

            // Cleared on read, so a press is consumed by exactly one tick.
            jumpPresses = 0;
            attackPresses = 0;
            crouchPressed = false;
            return result;
        }

        /// <summary>The swing starts at the eye, the camera sits above and behind it:
        /// turned to the point under the crosshair instead of along the view, it hits
        /// what the crosshair shows. Straight along the view if that point is unknown or
        /// not ahead of the eye.</summary>
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

        /// <summary>Third person: a swing turns the figure at once to what the crosshair shows,
        /// the keys turn it towards where they run. The returned move is that run in the figure's
        /// own frame, so it goes exactly where the view says while the figure is still
        /// turning.</summary>
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
