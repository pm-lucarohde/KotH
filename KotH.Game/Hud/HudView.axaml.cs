using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace KotH.Client.Hud
{
    /// <summary>The round's controls, in the parts HillBoard takes out and shows as
    /// overlays of their own. Every setter changes a control only when its value changed:
    /// a changed control redraws its part, an equal one nothing.</summary>
    public partial class HudView : UserControl
    {
        /// <summary>Rows of the best list, and their height in units of a screen 1080 high.</summary>
        public const int Rows = 4;
        public const double RowHeight = 36;

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

        static readonly IBrush Spore = new SolidColorBrush(Color.FromRgb(0xA8, 0xF0, 0x60));

        /// <summary>The blaster's shots left as filled spores, and the share towards the next.</summary>
        public void Shots(int shots, double next)
        {
            Ellipse[] dots = { Shot1, Shot2, Shot3 };
            for (int i = 0; i < dots.Length; i++)
            {
                IBrush fill = i < shots ? Spore : null;
                if (dots[i].Fill != fill)
                    dots[i].Fill = fill;
                if (dots[i].Stroke != Spore)
                    dots[i].Stroke = Spore;
            }
            double width = 170 * System.Math.Clamp(next, 0, 1);
            if (Next.Width != width)
                Next.Width = width;
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

        static void Set(TextBlock block, string text)
        {
            if (block.Text != text)
                block.Text = text;
        }
    }
}
