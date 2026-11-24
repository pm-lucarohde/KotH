using System;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using KotH.Shared.Match;
using KotH.Shared.Net;

namespace KotH.Client.Menu
{
    /// <summary>The Customize page: a tab per figure part, glow and trail, its colour on a wheel (hue,
    /// saturation) and a brightness slider, under it the glow's switch or the trail's shape (both from
    /// level 100), Reset to the painted colour (Reset all: the whole look). Every change goes out at once
    /// (<see cref="LookChanged"/>); the menu saves the look when the page closes.</summary>
    public partial class CustomizePage : UserControl
    {
        static readonly string[] Names = { "Cap", "Skin", "Leaves", "Gloves", "Spores", "Glow", "Trail" };
        static readonly string[] GlowChoices = { "Off", "On" };
        static readonly string[] TrailChoices = { "Off", "Sparks", "Stars", "Spores" };

        /// <summary>The look after a change by the player.</summary>
        public event Action<FigureLook> LookChanged;

        /// <summary>Back was chosen.</summary>
        public event Action BackRequested;

        readonly Border[] tabs = new Border[FigureLook.Colors];
        readonly Ellipse[] dots = new Ellipse[FigureLook.Colors];
        FigureLook look = FigureLook.Default;
        int part;
        bool filling;

        public CustomizePage()
        {
            InitializeComponent();
            for (int i = 0; i < FigureLook.Colors; i++)
            {
                int index = i;
                dots[i] = new Ellipse { Width = 18, Height = 18, Stroke = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), StrokeThickness = 1.5 };
                tabs[i] = new Border
                {
                    Classes = { "Tab" }, Padding = new Avalonia.Thickness(16, 7, 20, 7),
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 9,
                        Children = { dots[i], new TextBlock { Text = Names[i], VerticalAlignment = VerticalAlignment.Center } },
                    },
                };
                tabs[i].PointerPressed += (_, _) => Choose(index);
                Tabs.Children.Add(tabs[i]);
            }
            Wheel.Picked += Slid;
            Light.ValueChanged += (_, _) => Slid();
            Reset.Click += (_, _) =>
            {
                if (!Locked)
                    Set(FigureLook.PaintedColor(part));
            };
            ResetAll.Click += (_, _) =>
            {
                FigureLook painted = FigureLook.Default;
                if (painted.Equals(look))
                    return;
                look = painted;
                Choose(part);
                LookChanged?.Invoke(look);
            };
            Back.Click += (_, _) => BackRequested?.Invoke();
            // Test aid for shots: KOTH_SHOT_MENU_PART=n opens tab n (5 glow, 6 trail).
            Choose(int.TryParse(Environment.GetEnvironmentVariable("KOTH_SHOT_MENU_PART"), out int shown) ? Math.Clamp(shown, 0, FigureLook.Colors - 1) : 0);
        }

        /// <summary>The look to show and change; sends no LookChanged.</summary>
        public FigureLook Look
        {
            get => look;
            set
            {
                look = value;
                Choose(part);
            }
        }

        void Choose(int index)
        {
            part = index;
            for (int i = 0; i < tabs.Length; i++)
            {
                tabs[i].Classes.Set("Chosen", i == part);
                dots[i].Fill = new SolidColorBrush(ToColor(look[i]));
            }
            bool locked = Locked;
            Wheel.IsEnabled = Light.IsEnabled = !locked;
            Wheel.Opacity = locked ? 0.45 : 1.0;
            ShowOptions(locked);
            uint c = look[part];
            ColorWheel.Hsv((c >> 16 & 0xFF) / 255.0, (c >> 8 & 0xFF) / 255.0, (c & 0xFF) / 255.0, Wheel.Hue,
                           out double h, out double s, out double v);
            filling = true;
            Light.Value = v;
            filling = false;
            Wheel.Show(h, s, v);
            Show();
        }

        // Glow and trail open at level 100: below it their tabs show, but nothing changes.
        bool Locked => part >= FigureLook.Glow && PlayerProgress.Level < Levels.GlowLevel;

        // Under the swatch: the glow's Off | On or the trail's shapes, the current one chosen.
        void ShowOptions(bool locked)
        {
            Options.Children.Clear();
            string[] choices = part == FigureLook.Glow ? GlowChoices : part == FigureLook.Trail ? TrailChoices : null;
            if (choices == null)
                return;
            int current = part == FigureLook.Glow ? (look.Glows ? 1 : 0) : look.TrailShape;
            for (int i = 0; i < choices.Length; i++)
            {
                int choice = i;
                var chip = new Border
                {
                    Classes = { "Tab" }, Padding = new Avalonia.Thickness(16, 6),
                    Child = new TextBlock { Text = choices[i], FontSize = 20 },
                };
                chip.Classes.Set("Chosen", i == current);
                chip.Classes.Set("Off", locked);
                if (!locked)
                    chip.PointerPressed += (_, _) => Pick(choice);
                Options.Children.Add(chip);
            }
            if (locked)
                Options.Children.Add(new TextBlock
                {
                    Classes = { "Hint" }, Text = "Opens at level " + Levels.GlowLevel, VerticalAlignment = VerticalAlignment.Center,
                });
        }

        void Pick(int choice)
        {
            if (part == FigureLook.Glow)
                look.Glows = choice == 1;
            else
                look.TrailShape = (byte)choice;
            Choose(part);
            LookChanged?.Invoke(look);
        }

        // The wheel or the slider moved: the colour from both, the wheel keeping its own pick
        // (back from the bytes a grey would lose its hue).
        void Slid()
        {
            if (filling || Locked)
                return;
            Wheel.Show(Wheel.Hue, Wheel.Saturation, Light.Value);
            ColorWheel.Rgb(Wheel.Hue, Wheel.Saturation, Light.Value, out double r, out double g, out double b);
            uint rgb = Byte(r) << 16 | Byte(g) << 8 | Byte(b);
            if (look[part] == rgb)
                return;
            look[part] = rgb;
            dots[part].Fill = new SolidColorBrush(ToColor(rgb));
            Show();
            LookChanged?.Invoke(look);
        }

        void Set(uint rgb)
        {
            if (look[part] == rgb)
                return;
            look[part] = rgb;
            Choose(part);
            LookChanged?.Invoke(look);
        }

        static uint Byte(double v) => (uint)Math.Clamp(Math.Round(v * 255.0), 0, 255);

        void Show()
        {
            uint c = look[part];
            Color color = ToColor(c);
            Swatch.Background = new SolidColorBrush(color);
            Hex.Text = LookFile.Hex(c);
            // Light text on dark colours, dark on light ones.
            double lum = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
            Hex.Foreground = new SolidColorBrush(lum > 0.55 ? Color.FromRgb(0x5B, 0x3A, 0x24) : Color.FromRgb(0xF6, 0xEC, 0xDE));
        }

        static Color ToColor(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}
