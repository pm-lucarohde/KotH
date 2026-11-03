using System;
using KotH.Client.Rendering.Dlss;
using Stride.Input;

namespace KotH.Client.Settings
{
    /// <summary>A graphics level; High is the game's own look, Ultra costs more.</summary>
    public enum Quality { Low, Medium, High, Ultra }

    /// <summary>How the game fills the screen.</summary>
    public enum WindowMode { Borderless, Windowed, Exclusive }

    /// <summary>The keys the player may move to another key (keyboard only; the arrows,
    /// the left button, the wheel, Escape, F3 and F11 stay where they are).</summary>
    public enum Binding { Forward, Back, Left, Right, Jump, Sprint, Crouch, Cane, Blaster, PushToTalk }

    /// <summary>Everything the Settings page offers. The defaults are the game as it was before
    /// the page: 80 % is today's loudness, High today's look, FOV 85, no cap, no V-Sync, FXAA.</summary>
    public sealed class PlayerSettings
    {
        // Sound, 0 to 1 (0.8 today's loudness); devices by OpenAL's name, empty the system's.
        public float Overall = 0.8f, Effects = 0.8f, Music = 0.8f, Ambient = 0.8f;
        public string OutputDevice = "", InputDevice = "";

        // Graphics. Width and Height 0: the screen's own.
        public WindowMode Window = WindowMode.Borderless;
        public int Width, Height;
        public int FpsLimit;
        public bool VSync;
        public float Fov = DefaultFov;
        public bool Dlss;
        public DlssMode DlssMode = DlssMode.Quality;
        public float DlssSharpness = 0.5f;
        public bool Fxaa = true;
        public Quality Shadows = Quality.High, Textures = Quality.High, Lighting = Quality.High,
                       EffectsQuality = Quality.High, Volumetrics = Quality.High;

        // Input.
        public float MouseSensitivity = 2f;
        public Keys[] Keys = DefaultKeys();

        public const float DefaultFov = 85f, MinFov = 60f, MaxFov = 120f;
        public const float MinSensitivity = 0.1f, MaxSensitivity = 10f;

        /// <summary>The frame caps offered, 0 for none.</summary>
        public static readonly int[] FpsLimits = { 0, 30, 60, 90, 120, 144, 165, 240 };

        public static Keys[] DefaultKeys() => new[]
        {
            Stride.Input.Keys.W, Stride.Input.Keys.S, Stride.Input.Keys.A, Stride.Input.Keys.D, Stride.Input.Keys.Space,
            Stride.Input.Keys.LeftShift, Stride.Input.Keys.E, Stride.Input.Keys.D1, Stride.Input.Keys.D2, Stride.Input.Keys.V,
        };

        public Keys Key(Binding binding) => Keys[(int)binding];

        /// <summary>A volume slider as a gain on today's value: squared, so 0.8 is 1 and 1 about +4 dB.</summary>
        public static float Gain(float slider)
        {
            float g = Math.Clamp(slider, 0f, 1f) / 0.8f;
            return g * g;
        }

        public PlayerSettings Clone()
        {
            var copy = (PlayerSettings)MemberwiseClone();
            copy.Keys = (Keys[])Keys.Clone();
            return copy;
        }

        public void ResetSound()
        {
            var d = new PlayerSettings();
            (Overall, Effects, Music, Ambient, OutputDevice, InputDevice) = (d.Overall, d.Effects, d.Music, d.Ambient, d.OutputDevice, d.InputDevice);
            Keys[(int)Binding.PushToTalk] = d.Key(Binding.PushToTalk);
        }

        public void ResetGraphics()
        {
            var d = new PlayerSettings();
            (Window, Width, Height, FpsLimit, VSync, Fov) = (d.Window, d.Width, d.Height, d.FpsLimit, d.VSync, d.Fov);
            (Dlss, DlssMode, DlssSharpness, Fxaa) = (d.Dlss, d.DlssMode, d.DlssSharpness, d.Fxaa);
            (Shadows, Textures, Lighting, EffectsQuality, Volumetrics) = (d.Shadows, d.Textures, d.Lighting, d.EffectsQuality, d.Volumetrics);
        }

        public void ResetInput()
        {
            Keys talk = Key(Binding.PushToTalk);
            MouseSensitivity = new PlayerSettings().MouseSensitivity;
            Keys = DefaultKeys();
            Keys[(int)Binding.PushToTalk] = talk;
        }

        /// <summary>Equal when they would be saved alike.</summary>
        public bool Same(PlayerSettings other) => other != null && SettingsFile.Write(this) == SettingsFile.Write(other);

        /// <summary>Puts the key on the binding; a binding that had it before loses it (None).</summary>
        public void Bind(Binding binding, Keys key)
        {
            for (int i = 0; i < Keys.Length; i++)
                if (Keys[i] == key)
                    Keys[i] = Stride.Input.Keys.None;
            Keys[(int)binding] = key;
        }
    }
}
