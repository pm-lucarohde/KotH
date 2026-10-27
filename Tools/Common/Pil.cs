using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KotH.Tools;

/// <summary>
/// The parts of Pillow 12.3.0 the tools paint with, ported from its C code (libImaging) step
/// by step, so the pictures come out pixel for pixel as Pillow paints them. Modes L (one byte a
/// pixel) and RGB, RGBA, RGBa (four bytes a pixel, as Pillow keeps them).
/// </summary>
public sealed class PilImage
{
    public readonly string Mode;
    public readonly int Width, Height;
    public readonly byte[] Data;

    public int Pixel => Mode == "L" ? 1 : 4;

    public PilImage(string mode, int width, int height, params byte[] colour)
    {
        Mode = mode;
        Width = width;
        Height = height;
        Data = new byte[width * height * (mode == "L" ? 1 : 4)];
        if (colour.Length == 0)
            return;
        // Image.new: RGB fills its fourth byte with 255, as ImagingFill does.
        byte[] ink = Pixel == 1 ? [colour[0]] : [colour[0], colour[1], colour[2], colour.Length > 3 ? colour[3] : (byte)255];
        for (int p = 0; p < Data.Length; p += ink.Length)
            ink.CopyTo(Data, p);
    }

    PilImage(string mode, int width, int height, byte[] data, bool own)
    {
        Mode = mode;
        Width = width;
        Height = height;
        Data = data;
    }

    public PilImage Copy() => new(Mode, Width, Height, (byte[])Data.Clone(), true);

    /// <summary>Image.crop within the image.</summary>
    public PilImage Crop(int x0, int y0, int x1, int y1)
    {
        var output = new PilImage(Mode, x1 - x0, y1 - y0);
        int row = output.Width * Pixel;
        for (int y = 0; y < output.Height; y++)
            Array.Copy(Data, ((y + y0) * Width + x0) * Pixel, output.Data, y * row, row);
        return output;
    }

    /// <summary>Image.paste of an image of the same pixel size, without a mask, within the image.</summary>
    public void Paste(PilImage source, int x, int y)
    {
        int row = source.Width * Pixel;
        for (int j = 0; j < source.Height; j++)
            Array.Copy(source.Data, j * row, Data, ((y + j) * Width + x) * Pixel, row);
    }

    /// <summary>Image.composite(a, b, mask): b, with a pasted over it through the L mask (paste_mask_L).</summary>
    public static PilImage Composite(PilImage a, PilImage b, PilImage mask)
    {
        PilImage output = b.Copy();
        int n = a.Pixel;
        for (int p = 0; p < mask.Data.Length; p++)
        {
            uint m = mask.Data[p];
            for (int c = 0; c < n; c++)
            {
                ref byte o = ref output.Data[p * n + c];
                o = (byte)Div255(o * (255 - m) + a.Data[p * n + c] * m);
            }
        }
        return output;
    }

    /// <summary>Image.convert between the modes the tools use.</summary>
    public PilImage Convert(string mode)
    {
        if (mode == Mode)
            return Copy();
        var output = new PilImage(mode, Width, Height);
        byte[] i = Data, o = output.Data;
        switch (Mode + ">" + mode)
        {
            case "RGB>RGBA":
            case "RGBA>RGB":
                for (int p = 0; p < i.Length; p += 4)
                {
                    o[p] = i[p];
                    o[p + 1] = i[p + 1];
                    o[p + 2] = i[p + 2];
                    o[p + 3] = 255;
                }
                break;
            case "RGBA>RGBa":
                for (int p = 0; p < i.Length; p += 4)
                {
                    uint alpha = i[p + 3];
                    for (int c = 0; c < 3; c++)
                        o[p + c] = (byte)MulDiv255(i[p + c], alpha);
                    o[p + 3] = i[p + 3];
                }
                break;
            case "RGBa>RGBA":
                for (int p = 0; p < i.Length; p += 4)
                {
                    int alpha = i[p + 3];
                    for (int c = 0; c < 3; c++)
                        o[p + c] = alpha == 255 || alpha == 0 ? i[p + c] : Clip8(255 * i[p + c] / alpha);
                    o[p + 3] = i[p + 3];
                }
                break;
            default:
                throw new NotSupportedException($"convert {Mode} -> {mode}");
        }
        return output;
    }

    /// <summary>Image.point with a function for L: the table as Pillow builds it, clipped to 0..255.</summary>
    public PilImage Point(Func<int, int> function)
    {
        var lut = new byte[256];
        for (int v = 0; v < 256; v++)
            lut[v] = Clip8(function(v));
        var output = new PilImage(Mode, Width, Height);
        for (int p = 0; p < Data.Length; p++)
            output.Data[p] = lut[Data[p]];
        return output;
    }

    /// <summary>Image.putdata for L.</summary>
    public void PutData(IReadOnlyList<int> values)
    {
        for (int p = 0; p < values.Count; p++)
            Data[p] = Clip8(values[p]);
    }

    /// <summary>Image.alpha_composite over the whole image (RGBA), in place.</summary>
    public void AlphaComposite(PilImage source)
    {
        const int precision = 7;
        byte[] d = Data, s = source.Data;
        for (int p = 0; p < d.Length; p += 4)
        {
            uint sa = s[p + 3];
            if (sa == 0)
                continue;
            uint blend = d[p + 3] * (255 - sa);
            uint outa255 = sa * 255 + blend;
            uint coef1 = sa * 255 * 255 * (1 << precision) / outa255;
            uint coef2 = 255 * (1 << precision) - coef1;
            for (int c = 0; c < 3; c++)
            {
                uint tmp = s[p + c] * coef1 + d[p + c] * coef2;
                d[p + c] = (byte)(ShiftForDiv255(tmp + (0x80 << precision)) >> precision);
            }
            d[p + 3] = (byte)ShiftForDiv255(outa255 + 0x80);
        }
    }

    public enum Filter { Bicubic, Lanczos }

    /// <summary>Image.resize (Resample.c, 8 bits a channel); RGBA goes through RGBa like in Pillow.</summary>
    public PilImage Resize(int width, int height, Filter filter)
    {
        if (width == Width && height == Height)
            return Copy();
        if (Mode == "RGBA")
            return Convert("RGBa").Resize(width, height, filter).Convert("RGBA");
        if (width == Width || height == Height)
            throw new NotSupportedException("resize along one axis");
        var (vBounds, vK, vSize) = Coefficients(Height, 0, Height, height, filter);
        int first = vBounds[0], last = vBounds[height * 2 - 2] + vBounds[height * 2 - 1];
        var (hBounds, hK, hSize) = Coefficients(Width, 0, Width, width, filter);
        for (int i = 0; i < height; i++)
            vBounds[i * 2] -= first;
        // Horizontal pass over the rows the vertical one needs, then the vertical pass.
        var input = new PilImage(Mode, width, last - first);
        int n = Pixel;
        Parallel.For(0, input.Height, yy =>
        {
            int inRow = (yy + first) * Width * n, outRow = yy * width * n;
            for (int xx = 0; xx < width; xx++)
            {
                int xmin = hBounds[xx * 2], xmax = hBounds[xx * 2 + 1];
                for (int c = 0; c < n; c++)
                {
                    int ss = 1 << (Precision - 1);
                    for (int x = 0; x < xmax; x++)
                        ss += Data[inRow + (x + xmin) * n + c] * hK[xx * hSize + x];
                    input.Data[outRow + xx * n + c] = ClipResample(ss);
                }
            }
        });
        var output = new PilImage(Mode, input.Width, height);
        int m = n, w = width;
        Parallel.For(0, height, yy =>
        {
            int ymin = vBounds[yy * 2], ymax = vBounds[yy * 2 + 1];
            for (int xx = 0; xx < w; xx++)
                for (int c = 0; c < m; c++)
                {
                    int ss = 1 << (Precision - 1);
                    for (int y = 0; y < ymax; y++)
                        ss += input.Data[((y + ymin) * w + xx) * m + c] * vK[yy * vSize + y];
                    output.Data[(yy * w + xx) * m + c] = ClipResample(ss);
                }
        });
        return output;
    }

    const int Precision = 32 - 8 - 2;

    static byte ClipResample(int ss) => (byte)Math.Clamp(ss >> Precision, 0, 255);

    // precompute_coeffs and normalize_coeffs_8bpc.
    static (int[] Bounds, int[] K, int Size) Coefficients(int inSize, float in0, float in1, int outSize, Filter filter)
    {
        double support0 = filter == Filter.Bicubic ? 2.0 : 3.0;
        double scale = (double)(in1 - in0) / outSize, filterscale = scale < 1.0 ? 1.0 : scale;
        double support = support0 * filterscale;
        int ksize = (int)Math.Ceiling(support) * 2 + 1;
        var kk = new double[outSize * ksize];
        var bounds = new int[outSize * 2];
        double inverse = 1.0 / filterscale;
        for (int xx = 0; xx < outSize; xx++)
        {
            double center = in0 + (xx + 0.5) * scale;
            double ww = 0.0;
            int xmin = (int)(center - support + 0.5);
            if (xmin < 0)
                xmin = 0;
            int xmax = (int)(center + support + 0.5);
            if (xmax > inSize)
                xmax = inSize;
            xmax -= xmin;
            for (int x = 0; x < xmax; x++)
            {
                double t = (x + xmin - center + 0.5) * inverse;
                double w = filter == Filter.Bicubic ? Bicubic(t) : Lanczos(t);
                kk[xx * ksize + x] = w;
                ww += w;
            }
            if (ww != 0.0)
                for (int x = 0; x < xmax; x++)
                    kk[xx * ksize + x] /= ww;
            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }
        var k = new int[kk.Length];
        for (int x = 0; x < kk.Length; x++)
            k[x] = kk[x] < 0 ? (int)(-0.5 + kk[x] * (1 << Precision)) : (int)(0.5 + kk[x] * (1 << Precision));
        return (bounds, k, ksize);
    }

    static double Bicubic(double x)
    {
        const double a = -0.5;
        if (x < 0.0)
            x = -x;
        if (x < 1.0)
            return ((a + 2.0) * x - (a + 3.0)) * x * x + 1;
        if (x < 2.0)
            return (((x - 5) * x + 8) * x - 4) * a;
        return 0.0;
    }

    static double Sinc(double x)
    {
        if (x == 0.0)
            return 1.0;
        x *= Math.PI;
        return Libm.sin(x) / x;
    }

    static double Lanczos(double x) => -3.0 <= x && x < 3.0 ? Sinc(x) * Sinc(x / 3) : 0.0;

    /// <summary>ImageFilter.GaussianBlur(radius): three extended box blurs a way (BoxBlur.c).</summary>
    public PilImage GaussianBlur(double radius)
    {
        if (radius == 0)
            return Copy();
        float box = GaussianBoxRadius((float)radius, 3);
        PilImage output = Copy();
        if (box == 0)
            return output;
        int n = Pixel;
        for (int i = 0; i < 3; i++)
            BoxBlurRows(output.Data, Width, Height, n, box);
        byte[] transposed = Transpose(output.Data, Width, Height, n);
        for (int i = 0; i < 3; i++)
            BoxBlurRows(transposed, Height, Width, n, box);
        Array.Copy(Transpose(transposed, Height, Width, n), output.Data, output.Data.Length);
        return output;
    }

    /// <summary>
    /// GaussianBlur of the image tiled 3 x 3, cropped back to the middle tile. Each pass sums
    /// clamped windows; in the middle tile no window reaches the clamped ends while three passes
    /// together stay within one tile, so the same sums come out of one tile with wrapped indices.
    /// </summary>
    public PilImage GaussianBlurTiled(double radius)
    {
        float box = radius == 0 ? 0 : GaussianBoxRadius((float)radius, 3);
        if (box == 0)
            return Copy();
        if (3 * ((int)box + 1) > Math.Min(Width, Height))
        {
            var big = new PilImage(Mode, Width * 3, Height * 3);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    big.Paste(this, i * Width, j * Height);
            return big.GaussianBlur(radius).Crop(Width, Height, 2 * Width, 2 * Height);
        }
        PilImage output = Copy();
        int n = Pixel;
        for (int i = 0; i < 3; i++)
            WrappedBlurRows(output.Data, Width, Height, n, box);
        byte[] transposed = Transpose(output.Data, Width, Height, n);
        for (int i = 0; i < 3; i++)
            WrappedBlurRows(transposed, Height, Width, n, box);
        Array.Copy(Transpose(transposed, Height, Width, n), output.Data, output.Data.Length);
        return output;
    }

    static void WrappedBlurRows(byte[] data, int width, int height, int n, float floatRadius)
    {
        int radius = (int)floatRadius;
        uint ww = (uint)(16777216f / (floatRadius * 2 + 1));
        uint fw = (uint)((1 << 24) - (radius * 2 + 1) * ww) / 2;
        Parallel.For(0, height, () => new byte[width * n], (y, _, line) =>
        {
            Array.Copy(data, y * width * n, line, 0, width * n);
            int row = y * width * n;
            for (int c = 0; c < n; c++)
            {
                int At(int x) => line[(x < 0 ? x + width : x >= width ? x - width : x) * n + c];
                uint acc = 0;
                for (int k = -radius; k <= radius; k++)
                    acc += (uint)At(k);
                for (int x = 0; x < width; x++)
                {
                    uint bulk = acc * ww + (uint)(At(x - radius - 1) + At(x + radius + 1)) * fw;
                    data[row + x * n + c] = (byte)((bulk + (1 << 23)) >> 24);
                    acc += (uint)(At(x + radius + 1) - At(x - radius));
                }
            }
            return line;
        }, _ => { });
    }

    static float GaussianBoxRadius(float radius, int passes)
    {
        float sigma2 = radius * radius / passes;
        float L = (float)Math.Sqrt(12.0 * sigma2 + 1.0);
        float l = (float)Math.Floor((L - 1.0) / 2.0);
        float a = (2 * l + 1) * (l * (l + 1) - 3 * sigma2);
        a /= 6 * (sigma2 - (l + 1) * (l + 1));
        return l + a;
    }

    static byte[] Transpose(byte[] data, int width, int height, int n)
    {
        var output = new byte[data.Length];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
                for (int c = 0; c < n; c++)
                    output[(x * height + y) * n + c] = data[(y * width + x) * n + c];
        });
        return output;
    }

    // ImagingHorizontalBoxBlur in place, every channel alike (ImagingLineBoxBlur8/32).
    static void BoxBlurRows(byte[] data, int width, int height, int n, float floatRadius)
    {
        int radius = (int)floatRadius;
        uint ww = (uint)(16777216f / (floatRadius * 2 + 1));
        uint fw = (uint)((1 << 24) - (radius * 2 + 1) * ww) / 2;
        Parallel.For(0, height, () => new byte[width * n], (y, _, line) =>
        {
            Array.Copy(data, y * width * n, line, 0, width * n);
            for (int c = 0; c < n; c++)
                BoxBlurLine(line, data, y * width * n, c, n, width, radius, ww, fw);
            return line;
        }, _ => { });
    }

    static void BoxBlurLine(byte[] line, byte[] output, int row, int c, int n, int width, int radius, uint ww, uint fw)
    {
        int edgeA = Math.Min(radius + 1, width), edgeB = Math.Max(width - radius - 1, 0), lastx = width - 1;
        uint acc = (uint)(line[c] * (radius + 1));
        for (int x = 0; x < edgeA - 1; x++)
            acc += line[x * n + c];
        acc += (uint)(line[lastx * n + c] * (radius - edgeA + 1));
        if (edgeA <= edgeB)
        {
            for (int x = 0; x < edgeA; x++)
                Step(x, 0, x + radius, 0, x + radius + 1);
            for (int x = edgeA; x < edgeB; x++)
                Step(x, x - radius - 1, x + radius, x - radius - 1, x + radius + 1);
            for (int x = edgeB; x <= lastx; x++)
                Step(x, x - radius - 1, lastx, x - radius - 1, lastx);
        }
        else
        {
            for (int x = 0; x < edgeB; x++)
                Step(x, 0, x + radius, 0, x + radius + 1);
            for (int x = edgeB; x < edgeA; x++)
                Step(x, 0, lastx, 0, lastx);
            for (int x = edgeA; x <= lastx; x++)
                Step(x, x - radius - 1, lastx, x - radius - 1, lastx);
        }

        // MOVE_ACC, ADD_FAR and SAVE.
        void Step(int x, int subtract, int add, int left, int right)
        {
            acc += (uint)(line[add * n + c] - line[subtract * n + c]);
            uint bulk = acc * ww + (uint)(line[left * n + c] + line[right * n + c]) * fw;
            output[row + x * n + c] = (byte)((bulk + (1 << 23)) >> 24);
        }
    }

    /// <summary>The picture as a PNG to write: L, RGB or RGBA (RGBa is written as Pillow would not).</summary>
    public Png ToPng()
    {
        int channels = Mode switch { "L" => 1, "RGB" => 3, "RGBA" => 4, _ => throw new NotSupportedException(Mode) };
        var png = new Png(Width, Height, channels);
        int n = Pixel;
        for (int p = 0; p < Width * Height; p++)
            for (int c = 0; c < channels; c++)
                png.Pixels[p * channels + c] = Data[p * n + c];
        return png;
    }

    public static PilImage FromPng(Png png)
    {
        string mode = png.Channels switch { 1 => "L", 3 => "RGB", 4 => "RGBA", _ => throw new NotSupportedException() };
        var image = new PilImage(mode, png.Width, png.Height, mode == "RGB" ? [0, 0, 0, 255] : []);
        int n = image.Pixel;
        for (int p = 0; p < png.Width * png.Height; p++)
            for (int c = 0; c < png.Channels; c++)
                image.Data[p * n + c] = png.Pixels[p * png.Channels + c];
        return image;
    }

    internal static byte Clip8(int v) => (byte)(v <= 0 ? 0 : v < 256 ? v : 255);
    internal static uint ShiftForDiv255(uint a) => ((a >> 8) + a) >> 8;
    internal static uint Div255(uint a) => ShiftForDiv255(a + 128);
    internal static uint MulDiv255(uint a, uint b) => ShiftForDiv255(a * b + 128);
}

/// <summary>
/// ImageDraw on an image without blending (Draw.c, draw8/draw32): every shape writes its ink
/// as it is. Coordinates come in as Python floats and are cut to ints like _imaging.c does.
/// </summary>
public sealed class PilDraw
{
    readonly PilImage image;

    public PilDraw(PilImage image) => this.image = image;

    struct Edge
    {
        public int D, X0, Y0, XMin, YMin, XMax, YMax;
        public float Dx;
    }

    void HLine(int x0, int y0, int x1, byte[] ink)
    {
        if (y0 < 0 || y0 >= image.Height)
            return;
        if (x0 < 0)
            x0 = 0;
        else if (x0 >= image.Width)
            return;
        if (x1 < 0)
            return;
        else if (x1 >= image.Width)
            x1 = image.Width - 1;
        if (x0 > x1)
            return;
        int n = image.Pixel;
        Span<byte> row = image.Data.AsSpan((y0 * image.Width + x0) * n, (x1 - x0 + 1) * n);
        if (n == 1)
            row.Fill(ink[0]);
        else
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(row).Fill(BitConverter.ToUInt32(ink));
    }

    void Point(int x, int y, byte[] ink)
    {
        if (x >= 0 && x < image.Width && y >= 0 && y < image.Height)
            Array.Copy(ink, 0, image.Data, (y * image.Width + x) * image.Pixel, image.Pixel);
    }

    // ROUND_UP and ROUND_DOWN of Draw.c: the positive side in the argument's type, the negative in double.
    static int RoundUp(float f) => (int)(f >= 0.0 ? Math.Floor((double)(f + 0.5f)) : -Math.Floor(Math.Abs((double)f) + 0.5f));
    static int RoundDown(float f) => (int)(f >= 0.0 ? Math.Ceiling((double)(f - 0.5f)) : -Math.Ceiling(Math.Abs((double)f) - 0.5f));
    static int RoundUp(double f) => (int)(f >= 0.0 ? Math.Floor(f + 0.5) : -Math.Floor(Math.Abs(f) + 0.5));
    static int RoundDown(double f) => (int)(f >= 0.0 ? Math.Ceiling(f - 0.5) : -Math.Ceiling(Math.Abs(f) - 0.5));
    static float RoundF(float f) => MathF.Round(f, MidpointRounding.AwayFromZero);

    static void AddEdge(ref Edge e, int x0, int y0, int x1, int y1)
    {
        if (x0 <= x1) { e.XMin = x0; e.XMax = x1; }
        else { e.XMin = x1; e.XMax = x0; }
        if (y0 <= y1) { e.YMin = y0; e.YMax = y1; }
        else { e.YMin = y1; e.YMax = y0; }
        if (y0 == y1)
        {
            e.D = 0;
            e.Dx = 0.0f;
        }
        else
        {
            e.Dx = (float)(x1 - x0) / (y1 - y0);
            e.D = y0 == e.YMin ? 1 : -1;
        }
        e.X0 = x0;
        e.Y0 = y0;
    }

    static float At(in Edge e, int y) => (float)(y - e.Y0) * e.Dx + e.X0;

    // polygon_generic without alpha (hline32/hline8).
    void PolygonGeneric(Edge[] e, int n, byte[] ink)
    {
        if (n <= 0)
            return;
        var table = new List<int>();
        int ymin = image.Height - 1, ymax = 0;
        for (int i = 0; i < n; i++)
        {
            if (ymin > e[i].YMin)
                ymin = e[i].YMin;
            if (ymax < e[i].YMax)
                ymax = e[i].YMax;
            if (e[i].YMin == e[i].YMax)
            {
                HLine(e[i].XMin, e[i].YMin, e[i].XMax, ink);
                continue;
            }
            table.Add(i);
        }
        if (ymin < 0)
            ymin = 0;
        if (ymax > image.Height)
            ymax = image.Height;
        var xx = new float[table.Count * 2];
        for (; ymin <= ymax; ymin++)
        {
            int j = 0;
            for (int i = 0; i < table.Count; i++)
            {
                ref Edge current = ref e[table[i]];
                if (ymin < current.YMin || ymin > current.YMax)
                    continue;
                xx[j++] = At(current, ymin);
                if (ymin == current.YMax && ymin < ymax)
                {
                    xx[j] = xx[j - 1];
                    j++;
                }
                else if ((ymin == current.YMin || ymin == current.YMax) && current.Dx != 0)
                {
                    // Connect discontiguous corners.
                    for (int k = 0; k < i; k++)
                    {
                        ref Edge other = ref e[table[k]];
                        if ((ymin != other.YMin && ymin != other.YMax) || other.Dx == 0)
                            continue;
                        if (RoundF(xx[j - 1]) != RoundF(At(other, ymin)))
                            continue;
                        int offset = ymin == current.YMax ? -1 : 1;
                        float adjacent = At(current, ymin + offset);
                        if (ymin + offset >= other.YMin && ymin + offset <= other.YMax)
                        {
                            float adjacentOther = At(other, ymin + offset);
                            if (xx[j - 1] > adjacent + 1 && xx[j - 1] > adjacentOther + 1)
                                xx[j - 1] = RoundF((float)Math.Max((double)adjacent, adjacentOther)) + 1;
                            else if (xx[j - 1] < adjacent - 1 && xx[j - 1] < adjacentOther - 1)
                                xx[j - 1] = RoundF((float)Math.Min((double)adjacent, adjacentOther)) - 1;
                            break;
                        }
                    }
                }
            }
            Array.Sort(xx, 0, j);
            for (int i = 1; i < j; i += 2)
                HLine(RoundUp(xx[i - 1]), ymin, RoundDown(xx[i]), ink);
        }
    }

    /// <summary>ImageDraw.polygon(points, fill=ink).</summary>
    public void Polygon(IReadOnlyList<(double X, double Y)> points, byte[] ink)
    {
        int count = points.Count;
        var xy = new int[count * 2];
        for (int i = 0; i < count; i++)
        {
            xy[i * 2] = (int)points[i].X;
            xy[i * 2 + 1] = (int)points[i].Y;
        }
        var e = new Edge[count];
        int n = 0, k;
        for (k = 0; k < count - 1; k++)
        {
            int x0 = xy[k * 2], y0 = xy[k * 2 + 1], x1 = xy[k * 2 + 2], y1 = xy[k * 2 + 3];
            if (y0 == y1 && k != 0 && y0 == xy[k * 2 - 1])
            {
                // A horizontal line right after another one.
                if (x1 > x0 && x0 > xy[k * 2 - 2])
                {
                    e[n - 1].XMax = x1;
                    continue;
                }
                if (x1 < x0 && x0 < xy[k * 2 - 2])
                {
                    e[n - 1].XMin = x1;
                    continue;
                }
            }
            AddEdge(ref e[n++], x0, y0, x1, y1);
        }
        if (xy[k * 2] != xy[0] || xy[k * 2 + 1] != xy[1])
            AddEdge(ref e[n++], xy[k * 2], xy[k * 2 + 1], xy[0], xy[1]);
        PolygonGeneric(e, n, ink);
    }

    /// <summary>ImageDraw.line(points, fill=ink, width): every segment as a wide line.</summary>
    public void Line(IReadOnlyList<(double X, double Y)> points, byte[] ink, int width)
    {
        if (width == 1)
            throw new NotSupportedException("line width 1");
        for (int i = 0; i < points.Count - 1; i++)
            WideLine((int)points[i].X, (int)points[i].Y, (int)points[i + 1].X, (int)points[i + 1].Y, ink, width);
    }

    void WideLine(int x0, int y0, int x1, int y1, byte[] ink, int width)
    {
        int dx = x1 - x0, dy = y1 - y0;
        if (dx == 0 && dy == 0)
        {
            Point(x0, y0, ink);
            return;
        }
        double big = Libm.hypot(dx, dy);
        double small = (width - 1) / 2.0;
        double ratioMax = RoundUp(small) / big, ratioMin = RoundDown(small) / big;
        int dxmin = RoundDown(ratioMin * dy), dxmax = RoundDown(ratioMax * dy);
        int dymin = RoundDown(ratioMin * dx), dymax = RoundDown(ratioMax * dx);
        int[,] v = { { x0 - dxmin, y0 + dymax }, { x1 - dxmin, y1 + dymax }, { x1 + dxmax, y1 - dymin }, { x0 + dxmax, y0 - dymin } };
        var e = new Edge[4];
        for (int i = 0; i < 4; i++)
            AddEdge(ref e[i], v[i, 0], v[i, 1], v[(i + 1) % 4, 0], v[(i + 1) % 4, 1]);
        PolygonGeneric(e, 4, ink);
    }

    /// <summary>ImageDraw.ellipse(box, fill=ink).</summary>
    public void Ellipse(double x0, double y0, double x1, double y1, byte[] ink)
    {
        int ix0 = (int)x0, iy0 = (int)y0;
        int a = (int)x1 - ix0, b = (int)y1 - iy0;
        if (a < 0 || b < 0)
            return;
        var st = new EllipseState(a, b, a + b);
        while (st.Next(out int X0, out int Y, out int X1))
            HLine(ix0 + (X0 + a) / 2, iy0 + (Y + b) / 2, ix0 + (X1 + a) / 2, ink);
    }

    /// <summary>ImageDraw.arc(box, start, end, fill=ink, width).</summary>
    public void Arc(double x0, double y0, double x1, double y1, float start, float end, byte[] ink, int width)
    {
        int ix0 = (int)x0, iy0 = (int)y0, ix1 = (int)x1, iy1 = (int)y1;
        NormalizeAngles(ref start, ref end);
        if (start + 360 == end)
            throw new NotSupportedException("full arc");
        if (start == end)
            return;
        int a = ix1 - ix0, b = iy1 - iy0;
        if (a < 0 || b < 0)
            return;
        var st = new ArcState(a, b, width, start, end);
        while (st.Next(out int X0, out int Y, out int X1))
            HLine(ix0 + (X0 + a) / 2, iy0 + (Y + b) / 2, ix0 + (X1 + a) / 2, ink);
    }

    static void NormalizeAngles(ref float al, ref float ar)
    {
        if (ar - al >= 360)
        {
            al = 0;
            ar = 360;
        }
        else
        {
            al = (float)Libm.fmod(al < 0 ? 360 - Libm.fmod(-al, 360) : al, 360);
            ar = (float)(al + Libm.fmod(ar < al ? 360 - Libm.fmod(al - ar, 360) : ar - al, 360));
        }
    }

    // quarter_* of Draw.c: a quarter of the ellipse on the grid of step 2.
    sealed class QuarterState
    {
        int cx, cy, ex, ey;
        long a2, b2, a2b2;
        bool finished;

        public QuarterState(int a, int b)
        {
            if (a < 0 || b < 0)
            {
                finished = true;
                return;
            }
            cx = a;
            cy = b % 2;
            ex = a % 2;
            ey = b;
            a2 = (long)a * a;
            b2 = (long)b * b;
            a2b2 = a2 * b2;
        }

        long Delta(long x, long y) => Math.Abs(a2 * y * y + b2 * x * x - a2b2);

        public bool Next(out int x, out int y)
        {
            x = y = 0;
            if (finished)
                return false;
            x = cx;
            y = cy;
            if (cx == ex && cy == ey)
                finished = true;
            else
            {
                int nx = cx, ny = cy + 2;
                long ndelta = Delta(nx, ny);
                if (nx > 1)
                {
                    long newdelta = Delta(cx - 2, cy + 2);
                    if (ndelta > newdelta)
                    {
                        nx = cx - 2;
                        ny = cy + 2;
                        ndelta = newdelta;
                    }
                    newdelta = Delta(cx - 2, cy);
                    if (ndelta > newdelta)
                    {
                        nx = cx - 2;
                        ny = cy;
                    }
                }
                cx = nx;
                cy = ny;
            }
            return true;
        }
    }

    // ellipse_* of Draw.c: horizontal segments of the ring between two ellipses.
    sealed class EllipseState
    {
        readonly QuarterState outer, inner;
        int py, pl, pr;
        readonly int[] cy = new int[4], cl = new int[4], cr = new int[4];
        int count;
        bool finished;
        readonly int leftmost;

        public EllipseState(int a, int b, int w)
        {
            leftmost = a % 2;
            outer = new QuarterState(a, b);
            if (w < 1 || !outer.Next(out pr, out py))
                finished = true;
            else
            {
                inner = new QuarterState(a - 2 * (w - 1), b - 2 * (w - 1));
                pl = leftmost;
            }
        }

        public bool Next(out int x0, out int y0, out int x1)
        {
            if (count == 0)
            {
                x0 = y0 = x1 = 0;
                if (finished)
                    return false;
                int y = py, l = pl, r = pr;
                int cx = 0, ccy = 0;
                bool next;
                while ((next = outer.Next(out cx, out ccy)) && ccy <= y) { }
                if (!next)
                    finished = true;
                else
                {
                    pr = cx;
                    py = ccy;
                }
                while ((next = inner.Next(out cx, out ccy)) && ccy <= y)
                    l = cx;
                pl = !next ? leftmost : cx;
                if ((l > 0 || l < r) && y > 0)
                    Push(l == 0 ? 2 : l, y, r);
                if (y > 0)
                    Push(-r, y, -l);
                if (l > 0 || l < r)
                    Push(l == 0 ? 2 : l, -y, r);
                Push(-r, -y, -l);
            }
            --count;
            x0 = cl[count];
            y0 = cy[count];
            x1 = cr[count];
            return true;
        }

        void Push(int l, int y, int r)
        {
            cl[count] = l;
            cy[count] = y;
            cr[count] = r;
            count++;
        }
    }

    sealed class ClipNode
    {
        public bool IsClip, IsAnd;
        public double A, B, C;
        public ClipNode L, R;

        public void Transpose()
        {
            if (IsClip)
                (A, B) = (B, A);
            L?.Transpose();
            R?.Transpose();
        }
    }

    // clip_tree_do_clip: the clipped pieces of a segment as (x, type) events, sorted.
    static List<(int X, int Type)> DoClip(ClipNode root, int x0, int y, int x1)
    {
        if (root == null)
            return [(x0, 1), (x1, -1)];
        if (root.IsClip)
        {
            const double eps = 1e-9;
            double A = root.A, B = root.B, C = root.C;
            if (Math.Abs(A) < eps)
            {
                if (B * y + C < -eps)
                {
                    x0 = 1;
                    x1 = 0;
                }
            }
            else
            {
                double ix = -(B * y + C) / A;
                if (A * x0 + B * y + C < eps)
                    x0 = (int)Math.Round(Math.Max((double)x0, ix), MidpointRounding.AwayFromZero);
                if (A * x1 + B * y + C < eps)
                    x1 = (int)Math.Round(Math.Min((double)x1, ix), MidpointRounding.AwayFromZero);
            }
            return x0 <= x1 ? [(x0, 1), (x1, -1)] : [];
        }
        List<(int X, int Type)> l1 = DoClip(root.L, x0, y, x1), l2 = DoClip(root.R, x0, y, x1);
        var output = new List<(int X, int Type)>();
        int i1 = 0, i2 = 0, k1 = 0, k2 = 0;
        while (i1 < l1.Count || i2 < l2.Count)
        {
            (int X, int Type) t;
            if (i2 == l2.Count || (i1 < l1.Count && (l1[i1].X < l2[i2].X || (l1[i1].X == l2[i2].X && l1[i1].Type > l2[i2].Type))))
            {
                t = l1[i1++];
                k1 += t.Type;
            }
            else
            {
                t = l2[i2++];
                k2 += t.Type;
            }
            bool tailOpen = output.Count > 0 && output[^1].Type == 1;
            bool keep = !root.IsAnd
                ? (t.Type == 1 && !tailOpen) || (t.Type == -1 && k1 == 0 && k2 == 0)
                : (t.Type == 1 && !tailOpen && k1 > 0 && k2 > 0) || (t.Type == -1 && tailOpen && (k1 == 0 || k2 == 0));
            if (keep)
                output.Add(t);
        }
        return output;
    }

    // arc_init and clip_ellipse_next: an arc with caps orthogonal to the curve.
    sealed class ArcState
    {
        readonly EllipseState st;
        readonly ClipNode root;
        readonly Queue<int> head = new();
        int y;

        public ArcState(int a, int b, int w, float al, float ar)
        {
            if (a < b)
            {
                var transposed = new ArcState(b, a, w, 90 - ar, 90 - al);
                st = new EllipseState(a, b, w);
                root = transposed.root;
                root?.Transpose();
                return;
            }
            st = new EllipseState(a, b, w);
            NormalizeAngles(ref al, ref ar);
            if (ar == al + 360)
                return;
            var lc = new ClipNode
            {
                IsClip = true,
                A = -a * Libm.sin(al * Math.PI / 180.0),
                B = b * Libm.cos(al * Math.PI / 180.0),
                C = (a * a - b * b) * Libm.sin(al * Math.PI / 90.0) / 2.0,
            };
            var rc = new ClipNode
            {
                IsClip = true,
                A = a * Libm.sin(ar * Math.PI / 180.0),
                B = -b * Libm.cos(ar * Math.PI / 180.0),
                C = (b * b - a * a) * Libm.sin(ar * Math.PI / 90.0) / 2.0,
            };
            if (Libm.fmod(al, 180) == 0 || Libm.fmod(ar, 180) == 0)
                root = new ClipNode { L = lc, R = rc, IsAnd = ar - al < 180 };
            else if (((int)(al / 180) + (int)(ar / 180)) % 2 == 1)
                root = new ClipNode
                {
                    L = new ClipNode { IsAnd = true, L = new ClipNode { IsClip = true, B = (int)(al / 180) % 2 == 0 ? 1 : -1 }, R = lc },
                    R = new ClipNode { IsAnd = true, L = new ClipNode { IsClip = true, B = (int)(ar / 180) % 2 == 0 ? 1 : -1 }, R = rc },
                };
            else
            {
                bool and = ar - al < 180;
                root = new ClipNode
                {
                    IsAnd = and,
                    L = new ClipNode { IsAnd = and, L = lc, R = rc },
                    R = new ClipNode { IsClip = true, B = ar < 180 || ar > 540 ? 1 : -1 },
                };
            }
        }

        public bool Next(out int x0, out int y0, out int x1)
        {
            while (head.Count == 0 && st.Next(out int s0, out int sy, out int s1))
            {
                foreach (var (x, _) in DoClip(root, s0, sy, s1))
                    head.Enqueue(x);
                y = sy;
            }
            x0 = x1 = 0;
            y0 = y;
            if (head.Count == 0)
                return false;
            x0 = head.Dequeue();
            x1 = head.Dequeue();
            return true;
        }
    }
}
