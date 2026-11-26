using System;
using KotH.Client.Gamepad;
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
    public enum Binding { Forward, Back, Left, Right, Jump, Sprint, Crouch, Cane, Blaster, PushToTalk, Chat }

    /// <summary>The gamepad buttons the player may move (the sticks walk and look, Start pauses; in the
    /// menus left stick selects, A clicks, B goes back, right stick scrolls, LB/RB switch tabs).</summary>
    public enum PadAction { Jump, Sprint, Crouch, Attack, Weapon, View, PushToTalk }

    /// <summary>Everything the Settings page offers. The defaults are the game as it was before
    /// the page: 80 % is today's loudness, High today's look, FOV 85, no cap, no V-Sync, FXAA.</summary>
    public sealed class PlayerSettings
    {
        // Sound, 0 to 1 (0.8 today's loudness); devices by OpenAL's name, empty the system's.
        public float Overall = 0.8f, Effects = 0.8f, Music = 0.8f, Ambient = 0.8f, Voice = 0.8f;
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

        // Gamepad: look speed (1 = the game's own), radial dead zone (5 % default), Y invert, rumble.
        public float StickSensitivity = 1f, StickDeadZone = 0.05f;
        public bool InvertY, Vibration = true;
        public PadButton[] PadButtons = DefaultPad();

        // Hosting, from the Host page: the round's name, private or public and its flags. Never the password.
        // No region or no language: Steam's guess for this player when hosting.
        public string HostName = "";
        public bool HostPrivate, HostModded;
        public KotH.Shared.Match.MatchMode HostMode;
        public string[] HostRegions = System.Array.Empty<string>();
        public string[] HostLanguages = System.Array.Empty<string>();

        // The host's next round, from the lobby's Esc menu (Luca): start damage (< 0 the mode's own), percent
        // per hit (one for cane, spore and dash), knockback factor, bots and how many, auto start (a full lobby).
        public float HostStartPercent = -1f, HostHit = DefaultHitPercent, HostKnock = 1f;
        // Bots: off on every new server (Luca), on or off then for its lobby; never saved, unlike the count.
        public bool HostBots, HostAutoStart = true;
        public int HostBotCount = 3;

        /// <summary>The most on the host's server (Host page, Round), 0 for the mode's most (16 in 1, 2, 3, Free, else 12).
        /// At the most it follows a mode change, an own number stays (Luca), cut to what the mode takes.</summary>
        public int HostMaxPlayers;

        public int MaxPlayersOf => HostMaxPlayers <= 0 ? KotH.Shared.Match.HillMatch.MaxPlayers(HostMode)
            : Math.Clamp(HostMaxPlayers, KotH.Shared.Match.HillMatch.FewestOf(HostMode), KotH.Shared.Match.HillMatch.MaxPlayers(HostMode));

        /// <summary>The bots the host gets: never more than the room besides the host (Luca).</summary>
        public int BotCountOf => Math.Clamp(HostBotCount, 1, Math.Max(1, MaxPlayersOf - 1));

        /// <summary>Sets the most from a slider: the mode's most is stored as 0, so it follows the mode.</summary>
        public void ChooseMaxPlayers(int value)
            => HostMaxPlayers = value >= KotH.Shared.Match.HillMatch.MaxPlayers(HostMode) ? 0 : value;

        /// <summary>Every attack's own percent per hit (the body prefab's), and the menu's ranges.</summary>
        public const float DefaultHitPercent = 1.2f, MaxHitPercent = 10f, MaxStartPercent = 150f, MinKnock = 0.25f, MaxKnock = 3f;

        /// <summary>The start damage the next round gets: the host's, else the mode's.</summary>
        public float StartPercentOf => HostStartPercent < 0f ? KotH.Shared.Match.ModeRules.StartPercent(HostMode) : HostStartPercent;

        /// <summary>The round values for the host's next round.</summary>
        public KotH.Shared.Match.RoundRules RulesOf => new KotH.Shared.Match.RoundRules
        {
            Set = true, StartPercent = StartPercentOf, Melee = HostHit, Spore = HostHit, Dash = HostHit, Knock = HostKnock,
        };

        public const float DefaultFov = 85f, MinFov = 60f, MaxFov = 120f;
        public const float MinSensitivity = 0.1f, MaxSensitivity = 10f;
        public const float MinStickSensitivity = 0.2f, MaxStickSensitivity = 3f;
        public const float MinDeadZone = 0f, MaxDeadZone = 0.4f;

        /// <summary>The frame caps offered, 0 for none.</summary>
        public static readonly int[] FpsLimits = { 0, 30, 60, 90, 120, 144, 165, 240 };

        public static Keys[] DefaultKeys() => new[]
        {
            Stride.Input.Keys.W, Stride.Input.Keys.S, Stride.Input.Keys.A, Stride.Input.Keys.D, Stride.Input.Keys.Space,
            Stride.Input.Keys.LeftShift, Stride.Input.Keys.E, Stride.Input.Keys.D1, Stride.Input.Keys.D2, Stride.Input.Keys.V,
            Stride.Input.Keys.T,
        };

        public Keys Key(Binding binding) => Keys[(int)binding];

        public static PadButton[] DefaultPad() => new[]
        {
            PadButton.A, PadButton.LeftStick, PadButton.B, PadButton.RightTrigger, PadButton.Y, PadButton.RightStick,
            PadButton.LeftShoulder,
        };

        public PadButton Button(PadAction action) => PadButtons[(int)action];

        /// <summary>Puts the button on the action; an action that had it before loses it (None).</summary>
        public void Bind(PadAction action, PadButton button)
        {
            for (int i = 0; i < PadButtons.Length; i++)
                if (PadButtons[i] == button)
                    PadButtons[i] = PadButton.None;
            PadButtons[(int)action] = button;
        }

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
            copy.PadButtons = (PadButton[])PadButtons.Clone();
            copy.HostLanguages = (string[])HostLanguages.Clone();
            copy.HostRegions = (string[])HostRegions.Clone();
            return copy;
        }

        public void ResetSound()
        {
            var d = new PlayerSettings();
            (Overall, Effects, Music, Ambient, Voice) = (d.Overall, d.Effects, d.Music, d.Ambient, d.Voice);
            (OutputDevice, InputDevice) = (d.OutputDevice, d.InputDevice);
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
            MouseSensitivity = new PlayerSettings().MouseSensitivity;
            Keys = DefaultKeys();
        }

        public void ResetPad()
        {
            var d = new PlayerSettings();
            (StickSensitivity, StickDeadZone, InvertY, Vibration) = (d.StickSensitivity, d.StickDeadZone, d.InvertY, d.Vibration);
            PadButtons = DefaultPad();
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
