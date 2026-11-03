using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using KotH.Client.Settings;
using Stride.Core.Mathematics;
using Stride.Input;
using DlssApi = KotH.Client.Rendering.Dlss.Dlss;
using DlssMode = KotH.Client.Rendering.Dlss.DlssMode;

namespace KotH.Client.Menu
{
    /// <summary>The Settings page, in the main menu and in the pause menu: tabs Sound, Graphics and Input.
    /// The rows edit a copy of the settings in force; Apply puts it in force (ClientSettings), Revert and
    /// <see cref="Open"/> start again from them. The owner hands over keys while a key row waits (<see cref="Capture"/>).</summary>
    public partial class SettingsPage : UserControl
    {
        static readonly string[] TabNames = { "Sound", "Graphics", "Input" };

        /// <summary>Back was chosen.</summary>
        public event Action BackRequested;

        readonly Border[] tabs = new Border[TabNames.Length];
        readonly List<SettingRow>[] rows;
        PlayerSettings edited = ClientSettings.Applied.Clone();
        KeyRow listening;
        int tab;

        public SettingsPage()
        {
            InitializeComponent();
            for (int i = 0; i < tabs.Length; i++)
            {
                int index = i;
                tabs[i] = new Border { Classes = { "Tab" }, Child = new TextBlock { Text = TabNames[i] } };
                tabs[i].PointerPressed += (_, _) => Choose(index);
                Tabs.Children.Add(tabs[i]);
            }
            rows = new[] { SoundRows(), GraphicsRows(), InputRows() };
            foreach (List<SettingRow> list in rows)
                foreach (SettingRow row in list)
                {
                    row.Edited = () => edited;
                    row.Changed = Refresh;
                    if (row is KeyRow key)
                        key.Listen += Listen;
                }
            Reset.Click += (_, _) =>
            {
                if (tab == 0) edited.ResetSound();
                else if (tab == 1) edited.ResetGraphics();
                else edited.ResetInput();
                Refresh();
            };
            Revert.Click += (_, _) => Open();
            Apply.Click += (_, _) =>
            {
                ClientSettings.Apply(edited);
                Refresh();
            };
            Back.Click += (_, _) =>
            {
                listening = null;
                BackRequested?.Invoke();
            };
            Choose(0);
        }

        /// <summary>Shown again: from the settings in force, changes not applied are gone.</summary>
        public void Open()
        {
            edited = ClientSettings.Applied.Clone();
            listening = null;
            Refresh();
        }

        /// <summary>Test aid (KOTH_SHOT_SETTINGS): the tab by name.</summary>
        public void Choose(string name)
        {
            int i = Array.FindIndex(TabNames, t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
                Choose(i);
        }

        void Choose(int index)
        {
            tab = index;
            listening = null;
            for (int i = 0; i < tabs.Length; i++)
                tabs[i].Classes.Set("Chosen", i == tab);
            Rows.Children.Clear();
            foreach (SettingRow row in rows[tab])
                Rows.Children.Add(row.View);
            Scroll.Offset = default;
            Refresh();
        }

        void Refresh()
        {
            foreach (SettingRow row in rows[tab])
                row.Show(edited);
            listening?.Waiting();
            bool changed = !edited.Same(ClientSettings.Applied);
            Apply.IsEnabled = Revert.IsEnabled = changed;
        }

        void Listen(KeyRow row)
        {
            listening = row;
            Refresh();
        }

        /// <summary>Whether a key row waits for a key.</summary>
        public bool Listening => listening != null;

        /// <summary>Per frame from the owner, before its own keys: while a row waits, the next key
        /// goes to it (Escape cancels, the fixed keys are refused) and true says the frame's keys
        /// are taken.</summary>
        public bool Capture(InputManager input)
        {
            if (listening == null)
                return false;
            foreach (KeyEvent e in input.KeyEvents)
            {
                if (!e.IsDown || e.RepeatCount != 0 || listening == null)
                    continue;
                if (e.Key == Keys.Escape)
                    listening = null;
                else if (Bindable(e.Key))
                {
                    KeyRow row = listening;
                    listening = null;
                    row.Bind(e.Key);
                }
            }
            Refresh();
            return true;
        }

        // The keys that keep their own job: the arrows walk, Escape, F3 and F11.
        static bool Bindable(Keys key) => key is not (Keys.Escape or Keys.F3 or Keys.F11 or Keys.Up or Keys.Down
                                                      or Keys.Left or Keys.Right or Keys.None);

        static string Percent(double v) => $"{v:0} %";

        static SliderRow Volume(string name, Func<PlayerSettings, float> get, Action<PlayerSettings, float> set)
            => new SliderRow(name, 0, 100, 1, s => get(s) * 100.0, (s, v) => set(s, (float)(v / 100.0)), Percent);

        static readonly string[] QualityNames = { "Low", "Medium", "High", "Ultra" };
        static readonly Quality[] Qualities = { Quality.Low, Quality.Medium, Quality.High, Quality.Ultra };

        static ChoiceRow<Quality> Level(string name, Func<PlayerSettings, Quality> get, Action<PlayerSettings, Quality> set)
            => new ChoiceRow<Quality>(name, _ => Qualities, get, set, q => QualityNames[(int)q]);

        static List<SettingRow> SoundRows() => new List<SettingRow>
        {
            Volume("Overall volume", s => s.Overall, (s, v) => s.Overall = v),
            Volume("Sound effects", s => s.Effects, (s, v) => s.Effects = v),
            Volume("Music", s => s.Music, (s, v) => s.Music = v),
            Volume("Ambience", s => s.Ambient, (s, v) => s.Ambient = v),
            new HeadingRow("Devices"),
            new ChoiceRow<string>("Output", _ => AudioDevices.Outputs(), s => s.OutputDevice, (s, v) => s.OutputDevice = v,
                                  AudioDevices.Text, "after a restart"),
            new ChoiceRow<string>("Input", _ => AudioDevices.Inputs(), s => s.InputDevice, (s, v) => s.InputDevice = v,
                                  AudioDevices.Text, "voice chat"),
            new KeyRow("Push to talk", Binding.PushToTalk, "voice chat"),
        };

        static readonly WindowMode[] Windows = { WindowMode.Borderless, WindowMode.Windowed, WindowMode.Exclusive };
        static readonly string[] WindowNames = { "Borderless", "Windowed", "Fullscreen" };
        static readonly DlssMode[] Modes = { DlssMode.Dlaa, DlssMode.Quality, DlssMode.Balanced, DlssMode.Performance, DlssMode.UltraPerformance };
        static readonly bool[] Both = { false, true };

        // DLSS draws the scene at its own size in place of FXAA.
        static bool Upscaled(PlayerSettings s) => s.Dlss && DlssApi.Possible;

        // The screen's sizes up to its own (0: its own). Borderless draws the scene at the size and
        // stretches it over the screen, so only sizes of the screen's shape; exclusive switches the screen.
        static List<Int2> Resolutions(PlayerSettings s)
        {
            var list = new List<Int2> { Int2.Zero };
            Int2 native = DisplayModes.Native();
            foreach (Int2 size in DisplayModes.Sizes())
            {
                bool smaller = native == Int2.Zero || size.X <= native.X && size.Y <= native.Y && size != native;
                bool shaped = s.Window != WindowMode.Borderless || native == Int2.Zero
                              || Math.Abs(size.X * native.Y - size.Y * native.X) <= native.X * native.Y / 100;
                if (smaller && shaped)
                    list.Add(size);
            }
            return list;
        }

        static string Resolution(Int2 size)
        {
            if (size != Int2.Zero)
                return $"{size.X} × {size.Y}";
            Int2 native = DisplayModes.Native();
            return native == Int2.Zero ? "Native" : $"{native.X} × {native.Y}";
        }

        static string DlssName(DlssMode m) => m switch
        {
            DlssMode.Dlaa => "DLAA",
            DlssMode.UltraPerformance => "Ultra Performance",
            _ => m.ToString(),
        };

        static List<SettingRow> GraphicsRows() => new List<SettingRow>
        {
            new ChoiceRow<WindowMode>("Window", _ => Windows, s => s.Window, (s, v) => s.Window = v, w => WindowNames[(int)w]),
            new ChoiceRow<Int2>("Resolution", Resolutions, s => new Int2(s.Width, s.Height), (s, v) => (s.Width, s.Height) = (v.X, v.Y), Resolution)
            {
                Locked = s => s.Window == WindowMode.Borderless && Upscaled(s) ? "Set by DLSS" : null,
            },
            new ChoiceRow<int>("Frame rate limit", _ => PlayerSettings.FpsLimits, s => s.FpsLimit, (s, v) => s.FpsLimit = v,
                               n => n == 0 ? "Off" : $"{n} fps"),
            new ToggleRow("V-Sync", s => s.VSync, (s, v) => s.VSync = v),
            new SliderRow("Field of view", PlayerSettings.MinFov, PlayerSettings.MaxFov, 1, s => s.Fov, (s, v) => s.Fov = (float)v, v => $"{v:0}°"),
            new HeadingRow("Upscaling"),
            new ChoiceRow<bool>("NVIDIA DLSS", _ => Both, s => s.Dlss, (s, v) => s.Dlss = v, on => on ? "On" : "Off")
            {
                Locked = _ => DlssApi.Possible ? null : "Not available",
            },
            new ChoiceRow<DlssMode>("DLSS mode", _ => Modes, s => s.DlssMode, (s, v) => s.DlssMode = v, DlssName)
            {
                Locked = s => Upscaled(s) ? null : "DLSS off",
            },
            new SliderRow("DLSS sharpness", 0, 100, 1, s => s.DlssSharpness * 100.0, (s, v) => s.DlssSharpness = (float)(v / 100.0), Percent)
            {
                Locked = s => Upscaled(s) ? null : "DLSS off",
            },
            new ChoiceRow<bool>("Anti-aliasing", _ => Both, s => s.Fxaa, (s, v) => s.Fxaa = v, on => on ? "FXAA" : "Off")
            {
                Locked = s => Upscaled(s) ? "By DLSS" : null,
            },
            new HeadingRow("Quality"),
            Level("Shadows", s => s.Shadows, (s, v) => s.Shadows = v),
            Level("Textures", s => s.Textures, (s, v) => s.Textures = v),
            Level("Global illumination", s => s.Lighting, (s, v) => s.Lighting = v),
            Level("Effects", s => s.EffectsQuality, (s, v) => s.EffectsQuality = v),
            Level("Volumetric effects", s => s.Volumetrics, (s, v) => s.Volumetrics = v),
        };

        static List<SettingRow> InputRows() => new List<SettingRow>
        {
            new SliderRow("Mouse sensitivity", PlayerSettings.MinSensitivity, PlayerSettings.MaxSensitivity, 0.1,
                          s => s.MouseSensitivity, (s, v) => s.MouseSensitivity = (float)Math.Round(v, 1), v => $"{v:0.0}"),
            new HeadingRow("Keys", "the arrows walk too, left click attacks"),
            new KeyRow("Forward", Binding.Forward),
            new KeyRow("Back", Binding.Back),
            new KeyRow("Left", Binding.Left),
            new KeyRow("Right", Binding.Right),
            new KeyRow("Jump", Binding.Jump),
            new KeyRow("Sprint", Binding.Sprint),
            new KeyRow("Crouch", Binding.Crouch, "slide, dash"),
            new KeyRow("Cane", Binding.Cane),
            new KeyRow("Blaster", Binding.Blaster),
        };
    }
}
