using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace KotH.Client.Hud
{
    /// <summary>The load screen's clock and word, in the parts LoadScreen shows as overlays.</summary>
    public partial class LoadView : UserControl
    {
        public LoadView() => InitializeComponent();

        /// <summary>The parts, out of this control, each to be the content of an overlay.</summary>
        public void TakeParts() => Parts.Children.Clear();

        public void Say(string text)
        {
            if (Words.Text != text)
                Words.Text = text;
        }
    }

    /// <summary>The clock's twelve marks, the quarters longer and lighter.</summary>
    public sealed class ClockDial : Control
    {
        static readonly IPen Quarter = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#F6ECDE")), 3.5, lineCap: PenLineCap.Round);
        static readonly IPen Hour = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#8CFFFFFF")), 2, lineCap: PenLineCap.Round);

        public override void Render(DrawingContext context)
        {
            var centre = new Point(Bounds.Width * 0.5, Bounds.Height * 0.5);
            double outer = Bounds.Width * 0.5 - 9;
            for (int i = 0; i < 12; i++)
            {
                double angle = i * Math.PI / 6, inner = outer - (i % 3 == 0 ? 9 : 5);
                var dir = new Vector(Math.Sin(angle), -Math.Cos(angle));
                context.DrawLine(i % 3 == 0 ? Quarter : Hour, centre + dir * inner, centre + dir * outer);
            }
        }
    }

    /// <summary>A hand pointing up from the middle, with a short tail below it.</summary>
    public sealed class ClockHand : Control
    {
        public double Length { get; set; }
        public double Tail { get; set; }
        public double Thickness { get; set; }
        public Color Color { get; set; }

        public override void Render(DrawingContext context)
        {
            var centre = new Point(Bounds.Width * 0.5, Bounds.Height * 0.5);
            var pen = new Pen(new SolidColorBrush(Color), Thickness, lineCap: PenLineCap.Round);
            context.DrawLine(pen, centre + new Vector(0, Tail), centre - new Vector(0, Length));
        }
    }
}
