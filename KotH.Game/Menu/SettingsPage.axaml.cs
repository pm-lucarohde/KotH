using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using KotH.Client.Gamepad;
using KotH.Client.Localization;
using KotH.Client.Settings;
using Stride.Core.Mathematics;
using Stride.Input;
using DlssApi = KotH.Client.Rendering.Dlss.Dlss;
using DlssMode = KotH.Client.Rendering.Dlss.DlssMode;

namespace KotH.Client.Menu
{
    /// <summary>The Settings page, in the main menu and in the pause menu: tabs Sound, Graphics and Input.
    /// The rows edit a copy of the settings in force; Apply puts it in force (ClientSettings), Revert and
    /// <see cref="Open"/> start again from them. The owner hands over keys while a key row waits (<see cref="Capture"/>).
    /// Input shows the gamepad's rows instead of the keys' while the pad is in use (Luca).</summary>
    public partial class SettingsPage : UserControl
    {
        static readonly string[] TabIds = { "Sound", "Graphics", "Input" };
        static readonly string[] TabKeys = { "settings.tab.sound", "settings.tab.graphics", "settings.tab.input" };

        /// <summary>Back was chosen.</summary>
        public event Action BackRequested;

        readonly Border[] tabs = new Border[TabKeys.Length];
        readonly TextBlock[] tabLabels = new TextBlock[TabKeys.Length];
        List<SettingRow>[] rows;
        PlayerSettings edited = ClientSettings.Applied.Clone();
        KeyRow listening;
        PadRow listeningPad;
        int tab;
        bool padShown;
        PadStyle padStyle;

        // The rows on show: the Input tab has a second list for the gamepad.
        const int PadRows = 3;
        int Shown => tab == 2 && padShown ? PadRows : tab;

        public SettingsPage()
        {
            InitializeComponent();
            for (int i = 0; i < tabs.Length; i++)
            {
                int index = i;
                Action pick = () => Choose(index);
                tabLabels[i] = new TextBlock { Text = Loc.T(TabKeys[i]) };
                tabs[i] = new Border { Classes = { "Tab", "PageTab" }, Child = tabLabels[i], Tag = pick };
                tabs[i].PointerPressed += (_, _) => pick();
                Tabs.Children.Add(tabs[i]);
            }
            BuildRows();
            Reset.Click += (_, _) =>
            {
                if (tab == 0) edited.ResetSound();
                else if (tab == 1) edited.ResetGraphics();
                else if (padShown) edited.ResetPad();
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
                listeningPad = null;
                BackRequested?.Invoke();
            };
            ApplyLanguage();
            Choose(0);
        }

        void BuildRows()
        {
            rows = new[] { SoundRows(), GraphicsRows(), InputRows(), GamepadRows() };
            foreach (List<SettingRow> list in rows)
                foreach (SettingRow row in list)
                {
                    row.Edited = () => edited;
                    row.Changed = Refresh;
                    if (row is KeyRow key)
                        key.Listen += Listen;
                    if (row is PadRow pad)
                        pad.Listen += Listen;
                }
        }

        /// <summary>Rebuilds row labels and button text for the active language.</summary>
        public void ApplyLanguage()
        {
            TitleText.Text = Loc.T("settings.title");
            Reset.Content = Loc.T("common.reset");
            Revert.Content = Loc.T("common.revert");
            Apply.Content = Loc.T("common.apply");
            Back.Content = Loc.T("common.back");
            for (int i = 0; i < tabLabels.Length; i++)
                tabLabels[i].Text = Loc.T(TabKeys[i]);
            BuildRows();
            Choose(tab);
        }

        /// <summary>Shown again: from the settings in force, changes not applied are gone.</summary>
        public void Open()
        {
            edited = ClientSettings.Applied.Clone();
            listening = null;
            listeningPad = null;
            Refresh();
        }

        /// <summary>Test aid (KOTH_SHOT_SETTINGS): the tab by name.</summary>
        public void Choose(string name)
        {
            int i = Array.FindIndex(TabIds, t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
                Choose(i);
        }

        /// <summary>Gamepad LB/RB: moves to the previous or next tab, wrapping at the ends.</summary>
        public void StepTab(int delta) => Choose((tab + delta + tabs.Length) % tabs.Length);

        void Choose(int index)
        {
            tab = index;
            listening = null;
            listeningPad = null;
            padShown = Pad.Active;
            padStyle = Pad.Style;
            for (int i = 0; i < tabs.Length; i++)
                tabs[i].Classes.Set("Chosen", i == tab);
            Rows.Children.Clear();
            foreach (SettingRow row in rows[Shown])
                Rows.Children.Add(row.View);
            Scroll.Offset = default;
            Refresh();
        }

        void Refresh()
        {
            foreach (SettingRow row in rows[Shown])
                row.Show(edited);
            listening?.Waiting();
            listeningPad?.Waiting();
            bool changed = !edited.Same(ClientSettings.Applied);
            Apply.IsEnabled = Revert.IsEnabled = changed;
        }

        void Listen(KeyRow row)
        {
            listening = row;
            Refresh();
        }

        void Listen(PadRow row)
        {
            listeningPad = row;
            Refresh();
        }

        /// <summary>Whether a key or button row waits.</summary>
        public bool Listening => listening != null || listeningPad != null;

        /// <summary>Per frame from the owner, before its own keys: while a row waits, the next key
        /// goes to it (Escape cancels, the fixed keys are refused) and true says the frame's keys
        /// are taken.</summary>
        public bool Capture(InputManager input)
        {
            // The Input tab follows the device in use (and refreshes button names on hot-swap).
            if (tab == 2 && (padShown != Pad.Active || padShown && padStyle != Pad.Style))
                Choose(2);
            if (listeningPad != null)
                return CapturePad(input);
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

        // The next gamepad button goes to the waiting row; Menu (Start) and Escape cancel.
        bool CapturePad(InputManager input)
        {
            PadButton b = Pad.AnyPressed();
            if (b == PadButton.Start || KotH.Client.Player.KeyPresses.Pressed(input, Keys.Escape))
            {
                listeningPad = null;
                Refresh();
            }
            else if (b != PadButton.None)
            {
                PadRow row = listeningPad;
                listeningPad = null;
                row.Bind(b);
                Refresh();
            }
            return true;
        }

        // The keys that keep their own job: the arrows walk, Escape, F3 and F11.
        static bool Bindable(Keys key) => key is not (Keys.Escape or Keys.F3 or Keys.F11 or Keys.Up or Keys.Down
                                                      or Keys.Left or Keys.Right or Keys.None);

        static string Percent(double v) => Loc.Percent(v, "0");

        static SliderRow Volume(string key, Func<PlayerSettings, float> get, Action<PlayerSettings, float> set)
            => new SliderRow(Loc.T(key), 0, 100, 1, s => get(s) * 100.0, (s, v) => set(s, (float)(v / 100.0)), Percent);

        static readonly string[] QualityKeys = { "settings.quality.low", "settings.quality.medium", "settings.quality.high", "settings.quality.ultra" };
        static readonly Quality[] Qualities = { Quality.Low, Quality.Medium, Quality.High, Quality.Ultra };

        static ChoiceRow<Quality> Level(string key, Func<PlayerSettings, Quality> get, Action<PlayerSettings, Quality> set)
            => new ChoiceRow<Quality>(Loc.T(key), _ => Qualities, get, set, q => Loc.T(QualityKeys[(int)q]));

        static List<SettingRow> SoundRows() => new List<SettingRow>
        {
            Volume("settings.sound.overall", s => s.Overall, (s, v) => s.Overall = v),
            Volume("settings.sound.effects", s => s.Effects, (s, v) => s.Effects = v),
            Volume("settings.sound.music", s => s.Music, (s, v) => s.Music = v),
            Volume("settings.sound.ambience", s => s.Ambient, (s, v) => s.Ambient = v),
            Volume("settings.sound.voices", s => s.Voice, (s, v) => s.Voice = v),
            new HeadingRow(Loc.T("settings.sound.devices")),
            new ChoiceRow<string>(Loc.T("settings.sound.output"), _ => AudioDevices.Outputs(), s => s.OutputDevice, (s, v) => s.OutputDevice = v,
                                  AudioDevices.Text, Loc.T("settings.sound.output.note")),
            new ChoiceRow<string>(Loc.T("settings.sound.input"), _ => AudioDevices.Inputs(), s => s.InputDevice, (s, v) => s.InputDevice = v,
                                  AudioDevices.Text, Loc.T("settings.sound.input.note")),
        };

        static readonly WindowMode[] Windows = { WindowMode.Borderless, WindowMode.Windowed, WindowMode.Exclusive };
        static readonly string[] WindowKeys = { "settings.window.borderless", "settings.window.windowed", "settings.window.fullscreen" };
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
                return Loc.Ltr($"{size.X} × {size.Y}");
            Int2 native = DisplayModes.Native();
            return native == Int2.Zero ? Loc.T("settings.gfx.native") : Loc.Ltr($"{native.X} × {native.Y}");
        }

        static string DlssName(DlssMode m) => Loc.T(m switch
        {
            DlssMode.Dlaa => "settings.gfx.dlss.dlaa",
            DlssMode.Quality => "settings.gfx.dlss.quality",
            DlssMode.Balanced => "settings.gfx.dlss.balanced",
            DlssMode.Performance => "settings.gfx.dlss.performance",
            DlssMode.UltraPerformance => "settings.gfx.dlss.ultra_performance",
            _ => "settings.gfx.dlss.quality",
        });

        static List<SettingRow> GraphicsRows() => new List<SettingRow>
        {
            new ChoiceRow<WindowMode>(Loc.T("settings.gfx.window"), _ => Windows, s => s.Window, (s, v) => s.Window = v, w => Loc.T(WindowKeys[(int)w])),
            new ChoiceRow<Int2>(Loc.T("settings.gfx.resolution"), Resolutions, s => new Int2(s.Width, s.Height), (s, v) => (s.Width, s.Height) = (v.X, v.Y), Resolution),
            new ChoiceRow<int>(Loc.T("settings.gfx.fps_limit"), _ => PlayerSettings.FpsLimits, s => s.FpsLimit, (s, v) => s.FpsLimit = v,
                               n => n == 0 ? Loc.T("common.off") : Loc.F("common.fps_n", Loc.Number(n))),
            new ToggleRow(Loc.T("settings.gfx.vsync"), s => s.VSync, (s, v) => s.VSync = v),
            new SliderRow(Loc.T("settings.gfx.fov"), PlayerSettings.MinFov, PlayerSettings.MaxFov, 1, s => s.Fov, (s, v) => s.Fov = (float)v,
                          v => Loc.F("common.degrees", Loc.Decimal(v, "0"))),
            new HeadingRow(Loc.T("settings.gfx.upscaling")),
            new ChoiceRow<bool>(Loc.T("settings.gfx.dlss"), _ => Both, s => s.Dlss, (s, v) => s.Dlss = v, on => Loc.T(on ? "common.on" : "common.off"))
            {
                Locked = _ => DlssApi.Possible ? null : Loc.T("settings.gfx.not_available"),
            },
            new ChoiceRow<DlssMode>(Loc.T("settings.gfx.dlss_mode"), _ => Modes, s => s.DlssMode, (s, v) => s.DlssMode = v, DlssName)
            {
                Locked = s => Upscaled(s) ? null : Loc.T("settings.gfx.dlss_off"),
            },
            new SliderRow(Loc.T("settings.gfx.dlss_sharpness"), 0, 100, 1, s => s.DlssSharpness * 100.0, (s, v) => s.DlssSharpness = (float)(v / 100.0), Percent)
            {
                Locked = s => Upscaled(s) ? null : Loc.T("settings.gfx.dlss_off"),
            },
            new ChoiceRow<bool>(Loc.T("settings.gfx.aa"), _ => Both, s => s.Fxaa, (s, v) => s.Fxaa = v, on => Loc.T(on ? "settings.gfx.fxaa" : "common.off"))
            {
                Locked = s => Upscaled(s) ? Loc.T("settings.gfx.by_dlss") : null,
            },
            new HeadingRow(Loc.T("settings.gfx.quality")),
            Level("settings.gfx.shadows", s => s.Shadows, (s, v) => s.Shadows = v),
            Level("settings.gfx.textures", s => s.Textures, (s, v) => s.Textures = v),
            Level("settings.gfx.gi", s => s.Lighting, (s, v) => s.Lighting = v),
            Level("settings.gfx.effects", s => s.EffectsQuality, (s, v) => s.EffectsQuality = v),
            Level("settings.gfx.volumetrics", s => s.Volumetrics, (s, v) => s.Volumetrics = v),
        };

        static List<SettingRow> InputRows() => new List<SettingRow>
        {
            new SliderRow(Loc.T("settings.input.mouse_sens"), PlayerSettings.MinSensitivity, PlayerSettings.MaxSensitivity, 0.1,
                          s => s.MouseSensitivity, (s, v) => s.MouseSensitivity = (float)Math.Round(v, 1), v => Loc.Decimal(v, "0.0")),
            new HeadingRow(Loc.T("settings.input.keys")),
            new KeyRow(Loc.T("settings.input.forward"), Binding.Forward),
            new KeyRow(Loc.T("settings.input.back"), Binding.Back),
            new KeyRow(Loc.T("settings.input.left"), Binding.Left),
            new KeyRow(Loc.T("settings.input.right"), Binding.Right),
            new KeyRow(Loc.T("settings.input.jump"), Binding.Jump),
            new KeyRow(Loc.T("settings.input.sprint"), Binding.Sprint),
            new KeyRow(Loc.T("settings.input.crouch"), Binding.Crouch, Loc.T("settings.input.crouch.note")),
            new KeyRow(Loc.T("settings.input.cane"), Binding.Cane),
            new KeyRow(Loc.T("settings.input.blaster"), Binding.Blaster),
            new KeyRow(Loc.T("settings.input.chat"), Binding.Chat, Loc.T("settings.input.chat.note")),
            new KeyRow(Loc.T("settings.input.ptt"), Binding.PushToTalk, Loc.T("settings.input.ptt.note")),
        };

        static List<SettingRow> GamepadRows() => new List<SettingRow>
        {
            new SliderRow(Loc.T("settings.pad.look_sens"), PlayerSettings.MinStickSensitivity, PlayerSettings.MaxStickSensitivity, 0.1,
                          s => s.StickSensitivity, (s, v) => s.StickSensitivity = (float)Math.Round(v, 1), v => Loc.Decimal(v, "0.0")),
            new SliderRow(Loc.T("settings.pad.dead_zone"), 0, 40, 1, s => s.StickDeadZone * 100.0,
                          (s, v) => s.StickDeadZone = (float)Math.Round(v / 100.0, 2), Percent) { PadStep = 1 },
            new ToggleRow(Loc.T("settings.pad.invert"), s => s.InvertY, (s, v) => s.InvertY = v, Loc.T("settings.pad.invert.note")),
            new ToggleRow(Loc.T("settings.pad.vibration"), s => s.Vibration, (s, v) => s.Vibration = v),
            new HeadingRow(Loc.T("settings.pad.buttons"), "")
            {
                DynamicNote = () => Loc.T(Pad.Style == PadStyle.PlayStation ? "settings.pad.buttons.ps" : "settings.pad.buttons.xbox"),
            },
            new PadRow(Loc.T("settings.input.jump"), PadAction.Jump),
            new PadRow(Loc.T("settings.input.sprint"), PadAction.Sprint),
            new PadRow(Loc.T("settings.input.crouch"), PadAction.Crouch, Loc.T("settings.input.crouch.note")),
            new PadRow(Loc.T("settings.pad.attack"), PadAction.Attack),
            new PadRow(Loc.T("settings.pad.weapon"), PadAction.Weapon),
            new PadRow(Loc.T("settings.pad.camera"), PadAction.View, Loc.T("settings.pad.camera.note")),
            new PadRow(Loc.T("settings.input.ptt"), PadAction.PushToTalk, Loc.T("settings.input.ptt.note")),
        };
    }
}
