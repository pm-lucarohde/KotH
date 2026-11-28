using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using KotH.Client.Localization;

namespace KotH.Client.Hud
{
    /// <summary>The round's controls, in the parts HillBoard takes out and shows as
    /// overlays of their own. Every setter changes a control only when its value changed:
    /// a changed control redraws its part, an equal one nothing.</summary>
    public partial class HudView : UserControl
    {
        /// <summary>Rows of the best list, and their height in units of a screen 1080 high.</summary>
        public const int Rows = 4;
        public const double RowHeight = 40;

        static readonly IBrush White = Brushes.White;

        readonly Border[] backs = new Border[Rows];
        readonly TextBlock[] ranks = new TextBlock[Rows], names = new TextBlock[Rows], scores = new TextBlock[Rows];
        IBrush own = Brushes.White, ownBack = Brushes.Transparent, zone = Brushes.White;
        Color zoneColor = Colors.White;

        public HudView()
        {
            InitializeComponent();
            for (int i = 0; i < Rows; i++)
            {
                ranks[i] = new TextBlock { Classes = { "Hud", "Soft" }, FontSize = 22, Width = 30, Margin = new(0, 0, 0, 2) };
                names[i] = new TextBlock { Classes = { "Hud" }, FontSize = 24, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 0, 10, 3) };
                scores[i] = new TextBlock { Classes = { "Hud" }, FontSize = 24, Margin = new(0, 0, 0, 3) };
                Grid.SetColumn(names[i], 1);
                Grid.SetColumn(scores[i], 2);
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { ranks[i], names[i], scores[i] } };
                backs[i] = new Border { Height = RowHeight, CornerRadius = new(9), Padding = new(12, 0), Child = row };
                RankList.Children.Add(backs[i]);
            }
            ApplyLanguage();
        }

        /// <summary>Updates static HUD chip labels for the active language.</summary>
        public void ApplyLanguage()
        {
            Set(DamageLabel, Loc.T("hud.damage"));
            Set(SporesLabel, Loc.T("hud.spores"));
            Set(DashLabel, Loc.T("hud.dash"));
        }

        /// <summary>The parts, out of this control, each to be the content of an overlay.</summary>
        public void TakeParts() => Parts.Children.Clear();

        /// <summary>The own colour: the warm-up fill, the own row and the own win.</summary>
        public void Own(Color color)
        {
            own = new SolidColorBrush(color);
            ownBack = new SolidColorBrush(color, 0.2);
            Fill.Background = own;
        }

        /// <summary>The bar: what it counts, the count and the time (empty: none).</summary>
        public void Bar(string caption, string points, string time)
        {
            Set(Caption, caption);
            Set(Points, points);
            Points.IsVisible = points.Length > 0;
            Set(Time, time);
        }

        /// <summary>What the zone is doing, in the colour it is drawn in.</summary>
        public void Zone(string status, Color color)
        {
            Set(Status, status);
            if (color != zoneColor)
            {
                zoneColor = color;
                zone = new SolidColorBrush(color);
                Status.Foreground = Dot.Fill = zone;
            }
        }

        /// <summary>Row i of the best list.</summary>
        public void Rank(int i, string rank, string name, string score, bool isOwn)
        {
            Set(ranks[i], rank);
            Set(names[i], name);
            Set(scores[i], score);
            IBrush text = isOwn ? own : White, back = isOwn ? ownBack : null;
            if (names[i].Foreground != text)
                names[i].Foreground = scores[i].Foreground = text;
            if (backs[i].Background != back)
                backs[i].Background = back;
        }

        /// <summary>How many rows of the best list show; its glass fits them (8 of padding, 1.5 of rim).</summary>
        public void Ranked(int count)
        {
            double height = count * RowHeight + 19;
            if (BoardGlass.Height != height)
                BoardGlass.Height = height;
            for (int i = 0; i < Rows; i++)
                if (backs[i].IsVisible != i < count)
                    backs[i].IsVisible = i < count;
        }

        public void Hurt(string percent) => Set(Damage, percent);

        /// <summary>The spores' green as painted, before the own spore colour moves it (Spores).</summary>
        public static readonly Color SporePainted = Color.FromRgb(0xA8, 0xF0, 0x60);

        IBrush spore = new SolidColorBrush(SporePainted);

        /// <summary>The colour of the shots' spores and bar: the own spore colour (Luca).</summary>
        public void Spores(Color color)
        {
            spore = new SolidColorBrush(color);
            Next.Background = spore;
        }

        /// <summary>The blaster's shots left as filled spores, and the share towards the next.</summary>
        public void Shots(int shots, double next)
        {
            Ellipse[] dots = { Shot1, Shot2, Shot3 };
            for (int i = 0; i < dots.Length; i++)
            {
                IBrush fill = i < shots ? spore : null;
                if (dots[i].Fill != fill)
                    dots[i].Fill = fill;
                if (dots[i].Stroke != spore)
                    dots[i].Stroke = spore;
            }
            double width = 190 * System.Math.Clamp(next, 0, 1);
            if (Next.Width != width)
                Next.Width = width;
        }

        static readonly IBrush DashColor = new SolidColorBrush(Color.FromRgb(0xF0, 0xD0, 0x60));

        /// <summary>The dash: a filled dot while it is there, and the share towards it.</summary>
        public void Dash(bool ready, double next)
        {
            IBrush fill = ready ? DashColor : null;
            if (DashDot.Fill != fill)
                DashDot.Fill = fill;
            if (DashDot.Stroke != DashColor)
                DashDot.Stroke = DashColor;
            double width = 136 * System.Math.Clamp(next, 0, 1);
            if (DashNext.Width != width)
                DashNext.Width = width;
        }

        /// <summary>The winner, in the own colour when it is this screen's, and the time to the next round.</summary>
        public void Win(string title, bool isOwn, string countdown)
        {
            Set(Banner, title);
            IBrush text = isOwn ? own : White;
            if (Banner.Foreground != text)
                Banner.Foreground = text;
            Set(Countdown, countdown);
        }

        public void Say(string notice) => Set(Notice, notice);

        /// <summary>1, 2, 3, Free: the own role and its colour.</summary>
        public void ShowRole(string role, Color color)
        {
            Set(Role, role);
            if (color != roleColor)
            {
                roleColor = color;
                RoleDot.Fill = Role.Foreground = new SolidColorBrush(color);
            }
        }

        Color roleColor = Colors.White;

        /// <summary>The count in the middle (1, 2, 3, Free!) and the line below it, in a colour.</summary>
        public void ShowCall(string call, string name, Color color)
        {
            Set(Call, call);
            Set(CallName, name);
            CallName.IsVisible = name.Length > 0;
            if (color != callColor)
            {
                callColor = color;
                Call.Foreground = new SolidColorBrush(color);
            }
        }

        Color callColor = Colors.White;

        public void ShowWatch(string text) => Set(Watch, text);

        public void ShowWheelCaption(string text) => Set(WheelCaption, text);

        /// <summary>The wheel's slices from the top clockwise: a name, its share (all add to 1) and a colour.
        /// Made anew once per draw, not per frame; the picture is turned as a whole.</summary>
        public void BuildWheel(IReadOnlyList<string> names, IReadOnlyList<double> shares, IReadOnlyList<Color> colors)
        {
            const double R = 210, C = 210;
            Wheel.Children.Clear();
            double from = 0;
            for (int i = 0; i < names.Count; i++)
            {
                double sweep = shares[i] * 2 * Math.PI;
                if (sweep <= 0)
                    continue;
                var fill = new SolidColorBrush(colors[i]);
                var edge = new SolidColorBrush(Color.FromArgb(200, 16, 20, 24));
                if (sweep >= 2 * Math.PI - 1e-6)
                    Wheel.Children.Add(new Ellipse { Width = 2 * R, Height = 2 * R, Fill = fill, Stroke = edge, StrokeThickness = 3 });
                else
                {
                    var slice = new StreamGeometry();
                    using (StreamGeometryContext g = slice.Open())
                    {
                        g.BeginFigure(new Point(C, C), true);
                        g.LineTo(At(from, R));
                        g.ArcTo(At(from + sweep, R), new Size(R, R), 0, sweep > Math.PI, SweepDirection.Clockwise);
                        g.EndFigure(true);
                    }
                    Wheel.Children.Add(new Path { Data = slice, Fill = fill, Stroke = edge, StrokeThickness = 3 });
                }
                // The name along the slice's middle, read outwards.
                double mid = from + sweep / 2;
                var label = new TextBlock
                {
                    Text = names[i], Classes = { "Hud" }, FontSize = names.Count > 10 ? 17 : 21, Width = 150,
                    TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    RenderTransform = new RotateTransform(mid * 180 / Math.PI - 90),
                };
                Point at = At(mid, R * 0.6);
                Canvas.SetLeft(label, at.X - 75);
                Canvas.SetTop(label, at.Y - 14);
                Wheel.Children.Add(label);
                from += sweep;
            }

            static Point At(double angle, double radius) => new Point(C + radius * Math.Sin(angle), C - radius * Math.Cos(angle));
        }

        static void Set(TextBlock block, string text)
        {
            if (block.Text != text)
                block.Text = text;
        }
    }
}
