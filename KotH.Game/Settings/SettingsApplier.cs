using System;
using KotH.Client.Net;
using KotH.Client.Rendering.Dlss;
using Stride.Core;
using Stride.Engine;
using Stride.Games;

namespace KotH.Client.Settings
{
    /// <summary>Puts the game-wide settings into force: frame cap, V-Sync and DLSS, at the start and
    /// after every Apply. The command line and the test variables (--fps, KOTH_FPS, KOTH_VSYNC,
    /// --dlss, KOTH_DLSS) win over the file. Scripts read the rest themselves (ClientSettings).</summary>
    public sealed class SettingsApplier
    {
        readonly Game game;
        readonly bool headless;
        readonly int? fps;
        readonly bool? vsync;
        readonly DlssMode? dlss;
        readonly bool sharpnessFixed = Environment.GetEnvironmentVariable("KOTH_DLSS_SHARPNESS") != null;
        int version = -1;

        public SettingsApplier(Game game, bool headless)
        {
            this.game = game;
            this.headless = headless;
            LaunchOptions options = LaunchOptions.Current;
            fps = options.Fps ?? (int.TryParse(Environment.GetEnvironmentVariable("KOTH_FPS"), out int f) ? f : null);
            vsync = Environment.GetEnvironmentVariable("KOTH_VSYNC") is { } v ? v != "0" : null;
            dlss = options.Dlss;
            // Before the device exists: it is made with this present mode.
            game.GraphicsDeviceManager.SynchronizeWithVerticalRetrace = VSync(ClientSettings.Applied);
            Cap(ClientSettings.Applied);
        }

        /// <summary>Once per frame, before the scripts: applies what changed.</summary>
        public void Update()
        {
            if (version == ClientSettings.Version)
                return;
            bool first = version < 0;
            version = ClientSettings.Version;
            PlayerSettings s = ClientSettings.Applied;
            Cap(s);
            Dlss.Mode = dlss ?? (s.Dlss ? s.DlssMode : DlssMode.Off);
            if (!sharpnessFixed)
                Dlss.Sharpness = s.DlssSharpness;
            GraphicsDeviceManager manager = game.GraphicsDeviceManager;
            if (!first && manager.SynchronizeWithVerticalRetrace != VSync(s))
            {
                manager.SynchronizeWithVerticalRetrace = VSync(s);
                manager.ApplyChanges();
            }
        }

        bool VSync(PlayerSettings s) => vsync ?? s.VSync;

        // At most that many frames per second, the last millisecond spun so they come evenly;
        // 0 none. Hidden (headless), the window counts as minimised.
        void Cap(PlayerSettings s)
        {
            int limit = fps ?? s.FpsLimit;
            foreach (ThreadThrottler rate in headless ? [game.WindowMinimumUpdateRate, game.MinimizedMinimumUpdateRate] : new[] { game.WindowMinimumUpdateRate })
            {
                if (limit > 0)
                {
                    rate.SetMaxFrequency(limit);
                    rate.SetToPreciseAuto();
                }
                else
                    rate.MinimumElapsedTime = TimeSpan.Zero;
            }
        }
    }
}
