using System;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using KotH.Shared.Net;

namespace KotH.Client.Menu
{
    /// <summary>The Customize page: a tab per figure part, its colour on a wheel (hue, saturation)
    /// and a brightness slider, Reset to the painted colour (Reset all: every part's). Every change goes out at once (<see cref="LookChanged"/>); the menu saves
    /// the look when the page closes.</summary>
    public partial class CustomizePage : UserControl
    {
        static readonly string[] Names = { "Cap", "Skin", "Leaves", "Gloves", "Spores" };

        /// <summary>The look after a change by the player.</summary>
        public event Action<FigureLook> LookChanged;

        /// <summary>Back was chosen.</summary>
        public event Action BackRequested;

        readonly Border[] tabs = new Border[FigureLook.Parts];
        readonly Ellipse[] dots = new Ellipse[FigureLook.Parts];
        FigureLook look = FigureLook.Default;
        int part;
        bool filling;

        public CustomizePage()
        {
            InitializeComponent();
            for (int i = 0; i < FigureLook.Parts; i++)
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
            Reset.Click += (_, _) => Set(FigureLook.PaintedColor(part));
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
            Choose(0);
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
            uint c = look[part];
            ColorWheel.Hsv((c >> 16 & 0xFF) / 255.0, (c >> 8 & 0xFF) / 255.0, (c & 0xFF) / 255.0, Wheel.Hue,
                           out double h, out double s, out double v);
            filling = true;
            Light.Value = v;
            filling = false;
            Wheel.Show(h, s, v);
            Show();
        }

        // The wheel or the slider moved: the colour from both, the wheel keeping its own pick
        // (back from the bytes a grey would lose its hue).
        void Slid()
        {
            if (filling)
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
