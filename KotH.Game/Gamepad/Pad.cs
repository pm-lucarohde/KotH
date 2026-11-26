using System;
using KotH.Client.Settings;
using Silk.NET.SDL;
using Stride.Core.Mathematics;
using Stride.Input;
using SdlWindow = Stride.Graphics.SDL.Window;

namespace KotH.Client.Gamepad
{
    /// <summary>A gamepad's buttons in SDL's standard layout (Xbox names); the triggers count as buttons
    /// past half way. None is no button (an unbound action).</summary>
    public enum PadButton : byte
    {
        None, A, B, X, Y, Back, Start, LeftStick, RightStick, LeftShoulder, RightShoulder,
        DPadUp, DPadDown, DPadLeft, DPadRight, LeftTrigger, RightTrigger,
    }

    /// <summary>The button-label family for the connected gamepad (Xbox default, PlayStation for DualShock/DualSense).</summary>
    public enum PadStyle : byte { Xbox, PlayStation }

    /// <summary>The first gamepad SDL knows a layout for (Xbox, PlayStation, Switch …), read once a frame
    /// through SDL's GameController API: Stride's own gamepads know the Xbox layout only under Windows.
    /// Sticks have their dead zone taken out, up is +Y. Whether the pad or mouse and keyboard were used
    /// last decides what the menus and the Settings page show (<see cref="Active"/>).</summary>
    public static unsafe class Pad
    {
        const float StickFull = 0.9f, TriggerOn = 0.5f, TriggerOff = 0.4f;
        const double ScanSeconds = 0.5;

        // SDL's button numbers for PadButton 1 to 14 (A … DPadRight); the triggers are axes 4 and 5.
        static readonly int[] SdlButtons = { -1, 0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14 };

        static GameController* controller;
        static bool initialised, failed;
        static double sinceScan = ScanSeconds, ignoreMouse;
        static int listed = -1;
        static uint down, before;
        static Vector2 left, right;
        static float leftTrigger, rightTrigger;

        /// <summary>Whether a gamepad is there.</summary>
        public static bool Connected => controller != null;

        /// <summary>Xbox or PlayStation button labels for the open gamepad.</summary>
        public static PadStyle Style { get; private set; }

        /// <summary>The pad was used last (a button or a stick), not the mouse or the keyboard.</summary>
        public static bool Active { get; private set; }

        /// <summary>The left stick, -1 to 1 each way, up +Y; zero inside the dead zone, full a little before the rim.</summary>
        public static Vector2 Left => left;

        /// <summary>The right stick, as the left.</summary>
        public static Vector2 Right => right;

        public static bool Down(PadButton b) => b != PadButton.None && (down & Bit(b)) != 0;

        /// <summary>Went down this frame.</summary>
        public static bool Pressed(PadButton b) => b != PadButton.None && (down & ~before & Bit(b)) != 0;

        /// <summary>Went up this frame.</summary>
        public static bool Released(PadButton b) => b != PadButton.None && (~down & before & Bit(b)) != 0;

        /// <summary>The first button that went down this frame, None for none.</summary>
        public static PadButton AnyPressed()
        {
            uint fresh = down & ~before;
            for (int i = 1; i <= (int)PadButton.RightTrigger; i++)
                if ((fresh & (1u << i)) != 0)
                    return (PadButton)i;
            return PadButton.None;
        }

        static uint Bit(PadButton b) => 1u << (int)b;

        /// <summary>Ignores pointer-warp deltas right after capturing or releasing the mouse, so opening
        /// or closing a menu never drops <see cref="Active"/> back to the mouse.</summary>
        public static void IgnoreMouseBriefly() => ignoreMouse = 0.2;

        /// <summary>Once a frame before the scripts (KotHApp.Update): the pad's state, a pad plugged in or
        /// out, and which device was used last.</summary>
        public static void Update(InputManager input, double seconds)
        {
            before = down;
            if (ignoreMouse > 0.0)
                ignoreMouse = Math.Max(0.0, ignoreMouse - seconds);
            if (!Open(seconds))
            {
                down = 0;
                left = right = Vector2.Zero;
                Active = false;
                return;
            }
            Sdl sdl = SdlWindow.SDL;
            uint now = 0;
            for (int i = 1; i < SdlButtons.Length; i++)
                if (sdl.GameControllerGetButton(controller, (GameControllerButton)SdlButtons[i]) != 0)
                    now |= 1u << i;
            leftTrigger = Axis(sdl, 4);
            rightTrigger = Axis(sdl, 5);
            if (leftTrigger >= TriggerOn || leftTrigger > TriggerOff && (before & Bit(PadButton.LeftTrigger)) != 0)
                now |= Bit(PadButton.LeftTrigger);
            if (rightTrigger >= TriggerOn || rightTrigger > TriggerOff && (before & Bit(PadButton.RightTrigger)) != 0)
                now |= Bit(PadButton.RightTrigger);
            down = now;
            left = Stick(new Vector2(Axis(sdl, 0), -Axis(sdl, 1)));
            right = Stick(new Vector2(Axis(sdl, 2), -Axis(sdl, 3)));

            if ((down & ~before) != 0 || left != Vector2.Zero || right != Vector2.Zero)
                Active = true;
            else if (Active && input != null && UsedKeysOrMouse(input))
                Active = false;
        }

        static bool UsedKeysOrMouse(InputManager input)
        {
            if (input.KeyEvents.Count > 0 || input.MouseWheelDelta != 0f)
                return true;
            if (!input.HasMouse)
                return false;
            if (input.IsMouseButtonPressed(MouseButton.Left) || input.IsMouseButtonPressed(MouseButton.Right))
                return true;
            if (ignoreMouse > 0.0)
                return false;
            Vector2 pixels = input.MouseDelta * input.Mouse.SurfaceSize;
            return pixels.LengthSquared() > 4f;
        }

        static float Axis(Sdl sdl, int axis) => Math.Max(-1f, sdl.GameControllerGetAxis(controller, (GameControllerAxis)axis) / 32767f);

        // Radial: the direction stays, the length runs 0 to 1 between the setting's dead zone and StickFull.
        static Vector2 Stick(Vector2 raw)
        {
            float dead = Math.Clamp(ClientSettings.Applied.StickDeadZone, 0f, StickFull - 0.05f);
            float length = raw.Length();
            if (length <= dead)
                return Vector2.Zero;
            float scaled = Math.Min(1f, (length - dead) / (StickFull - dead));
            return raw * (scaled / length);
        }

        // The pad in use, opened when none is: the first one SDL has a layout for, looked for twice a second.
        static bool Open(double seconds)
        {
            Sdl sdl = SdlWindow.SDL;
            if (sdl == null || failed)
                return false;
            if (!initialised)
            {
                initialised = true;
                if (sdl.InitSubSystem(Sdl.InitGamecontroller) != 0)
                {
                    failed = true;
                    Console.WriteLine("[pad] SDL's game controllers could not start: " + sdl.GetErrorS());
                    return false;
                }
            }
            if (controller != null && sdl.GameControllerGetAttached(controller) == SdlBool.True)
                return true;
            if (controller != null)
            {
                Console.WriteLine("[pad] gone");
                sdl.GameControllerClose(controller);
                controller = null;
            }
            sinceScan += seconds;
            if (sinceScan < ScanSeconds)
                return false;
            sinceScan = 0;
            int count = sdl.NumJoysticks();
            if (count != listed)
            {
                // Logged once per change: which joysticks SDL sees and whether it knows their layout.
                listed = count;
                for (int i = 0; i < count; i++)
                    Console.WriteLine($"[pad] joystick {i}: {sdl.JoystickNameForIndexS(i)}, {(sdl.IsGameController(i) == SdlBool.True ? "gamepad" : "no layout")}");
            }
            for (int i = 0; i < count; i++)
            {
                if (sdl.IsGameController(i) != SdlBool.True || Wanted != null && !Named(sdl, i, Wanted))
                    continue;
                controller = sdl.GameControllerOpen(i);
                if (controller != null)
                {
                    Style = DetectStyle(sdl, controller);
                    Console.WriteLine($"[pad] {sdl.GameControllerNameS(controller)} ({Style})");
                    before = down = 0;
                    return true;
                }
            }
            return false;
        }

        // Test aid: KOTH_PAD=part of a name takes only such a pad (a virtual one beside a real one).
        static readonly string Wanted = Environment.GetEnvironmentVariable("KOTH_PAD") is { Length: > 0 } w ? w : null;
        static readonly string ForcedStyle = Environment.GetEnvironmentVariable("KOTH_PAD_STYLE");

        // Detects PlayStation pads by SDL type, Sony USB vendor id (0x054c) or name.
        static PadStyle DetectStyle(Sdl sdl, GameController* c)
        {
            if (!string.IsNullOrEmpty(ForcedStyle))
                return ForcedStyle.StartsWith("ps", StringComparison.OrdinalIgnoreCase)
                       || ForcedStyle.Contains("play", StringComparison.OrdinalIgnoreCase)
                    ? PadStyle.PlayStation : PadStyle.Xbox;
            GameControllerType type = sdl.GameControllerGetType(c);
            if (type is GameControllerType.PS3 or GameControllerType.PS4 or GameControllerType.PS5)
                return PadStyle.PlayStation;
            if (sdl.GameControllerGetVendor(c) == 0x054c)
                return PadStyle.PlayStation;
            string name = sdl.GameControllerNameS(c) ?? "";
            return name.Contains("PlayStation", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("DualShock", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("DualSense", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("PS4", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("PS5", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("PS3", StringComparison.OrdinalIgnoreCase)
                ? PadStyle.PlayStation : PadStyle.Xbox;
        }

        static bool Named(Sdl sdl, int index, string part)
            => sdl.GameControllerNameForIndexS(index)?.Contains(part, StringComparison.OrdinalIgnoreCase) == true
               || sdl.JoystickNameForIndexS(index)?.Contains(part, StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>Shakes the pad if the settings allow: low drives the heavy motor, high the light one,
        /// 0 to 1 each, for so long.</summary>
        public static void Rumble(float low, float high, int milliseconds)
        {
            if (controller == null || !ClientSettings.Applied.Vibration)
                return;
            SdlWindow.SDL.GameControllerRumble(controller, (ushort)(Math.Clamp(low, 0f, 1f) * 65535f),
                                               (ushort)(Math.Clamp(high, 0f, 1f) * 65535f), (uint)milliseconds);
        }
    }
}
