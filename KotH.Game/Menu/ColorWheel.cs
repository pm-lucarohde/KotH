using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace KotH.Client.Menu
{
    /// <summary>A round colour picker: the angle is the hue (red to the right, then yellow,
    /// green, blue anticlockwise), the distance from the middle the saturation. The disc shows
    /// the colours at the brightness the page's slider sets (<see cref="Show"/>); a ring marks the pick.</summary>
    public sealed class ColorWheel : Avalonia.Controls.Control
    {
        // The disc's picture in pixels (the overlay draws at up to 1.5x design units).
        const int Size = 512;

        double hue, saturation, brightness = 1.0;
        WriteableBitmap disc;
        double[] hues, sats;
        int[] pixels;
        bool dragging;

        /// <summary>The player moved the pick: hue (0..360) and saturation (0..1) are new.</summary>
        public event Action Picked;

        public double Hue => hue;
        public double Saturation => saturation;

        /// <summary>Shows a colour without raising Picked.</summary>
        public void Show(double h, double s, double v)
        {
            hue = h;
            saturation = s;
            if (v != brightness || disc == null)
            {
                brightness = v;
                Paint();
            }
            InvalidateVisual();
        }

        // The disc at the current brightness; its rim fades over a pixel and a half.
        void Paint()
        {
            if (hues == null)
            {
                hues = new double[Size * Size];
                sats = new double[Size * Size];
                pixels = new int[Size * Size];
                double r = Size / 2.0;
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                    {
                        double dx = x + 0.5 - r, dy = r - (y + 0.5);
                        hues[y * Size + x] = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360.0) % 360.0;
                        sats[y * Size + x] = Math.Sqrt(dx * dx + dy * dy) / (r - 2.0);
                    }
            }
            for (int i = 0; i < pixels.Length; i++)
            {
                double s = sats[i];
                double a = Math.Clamp((1.0 - s) * (Size / 2.0 - 2.0) / 1.5 + 0.5, 0.0, 1.0);
                if (a <= 0.0)
                {
                    pixels[i] = 0;
                    continue;
                }
                Rgb(hues[i], Math.Min(s, 1.0), brightness, out double cr, out double cg, out double cb);
                // Premultiplied BGRA.
                pixels[i] = (int)(a * 255.0 + 0.5) << 24 | (int)(cr * a * 255.0 + 0.5) << 16
                          | (int)(cg * a * 255.0 + 0.5) << 8 | (int)(cb * a * 255.0 + 0.5);
            }
            disc ??= new WriteableBitmap(new PixelSize(Size, Size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using ILockedFramebuffer frame = disc.Lock();
            for (int y = 0; y < Size; y++)
                Marshal.Copy(pixels, y * Size, frame.Address + y * frame.RowBytes, Size);
        }

        public override void Render(DrawingContext context)
        {
            if (disc == null)
                Paint();
            double d = Math.Min(Bounds.Width, Bounds.Height);
            var rect = new Rect((Bounds.Width - d) / 2, (Bounds.Height - d) / 2, d, d);
            context.DrawImage(disc, new Rect(0, 0, Size, Size), rect);

            double angle = hue * Math.PI / 180.0, reach = saturation * (d / 2 - 2);
            var at = new Point(rect.Center.X + Math.Cos(angle) * reach, rect.Center.Y - Math.Sin(angle) * reach);
            Rgb(hue, saturation, brightness, out double r, out double g, out double b);
            var fill = new SolidColorBrush(Color.FromRgb((byte)(r * 255 + 0.5), (byte)(g * 255 + 0.5), (byte)(b * 255 + 0.5)));
            context.DrawEllipse(fill, new Pen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), 6), at, 14, 14);
            context.DrawEllipse(null, new Pen(Brushes.White, 3.5), at, 14, 14);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            dragging = true;
            e.Pointer.Capture(this);
            Pick(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (dragging)
                Pick(e.GetPosition(this));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            dragging = false;
            e.Pointer.Capture(null);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => dragging = false;

        void Pick(Point p)
        {
            double d = Math.Min(Bounds.Width, Bounds.Height);
            double dx = p.X - Bounds.Width / 2, dy = Bounds.Height / 2 - p.Y;
            hue = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360.0) % 360.0;
            saturation = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / (d / 2 - 2), 0.0, 1.0);
            InvalidateVisual();
            Picked?.Invoke();
        }

        /// <summary>HSV (hue 0..360, saturation and value 0..1) to RGB 0..1.</summary>
        public static void Rgb(double h, double s, double v, out double r, out double g, out double b)
        {
            double c = v * s, x = c * (1 - Math.Abs(h / 60.0 % 2 - 1)), m = v - c;
            (r, g, b) = (h % 360) switch
            {
                < 60 => (c, x, 0.0),
                < 120 => (x, c, 0.0),
                < 180 => (0.0, c, x),
                < 240 => (0.0, x, c),
                < 300 => (x, 0.0, c),
                _ => (c, 0.0, x),
            };
            r += m; g += m; b += m;
        }

        /// <summary>RGB 0..1 to HSV; a grey keeps the hue it is given.</summary>
        public static void Hsv(double r, double g, double b, double keepHue, out double h, out double s, out double v)
        {
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), c = max - min;
            v = max;
            s = max > 0 ? c / max : 0;
            if (c <= 1e-9)
                h = keepHue;
            else if (max == r)
                h = (60 * ((g - b) / c) + 360) % 360;
            else if (max == g)
                h = 60 * ((b - r) / c) + 120;
            else
                h = 60 * ((r - g) / c) + 240;
        }
    }
}
