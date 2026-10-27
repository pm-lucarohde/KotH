#!/usr/bin/env -S dotnet run
// The cloud map for map "Neu": a sheet of small, soft cloudlets in rows and waves with
// blue lanes between them, like Luca's photo (altocumulus, 24.10.2026). Painted, few
// details, soft but clear edges. Writes KotH.Game/Resources/Textures/CloudMap.png,
// tileable, linear; texture x runs with the wind. Seeded, so every run paints the same:
// the numbers are numpy's (Common/NumPy.cs), the picture the one the Python script made.
//
//   red    the cloudlets: one dome per cloudlet (1 at its top, 0 at its rim); the shader
//          takes it as the height of the cloudlet over the layer's base
//   green  the large streaks and lanes, read again at a larger scale (Atmosphere)
//   blue   the coastal fog's wisps over the sea, at their own scale (Atmosphere)
//
//   dotnet run Tools/maps/Clouds.cs [-- --preview]
#:include ../Common/Libm.cs
#:include ../Common/NumPy.cs
#:include ../Common/Ziggurat.cs
#:include ../Common/Png.cs
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using KotH.Tools;

const int Size = 1024;
const uint Seed = 24;
(int X, int Y) cells = (28, 22);          // cloudlets along and across the wind per repeat
(double Min, double Max) radius = (0.55, 0.95);   // a cloudlet's radius in cells, at random
const double Across = 1.25;               // cloudlets are this much wider across the wind
const double Wave = 0.35;                 // rows bend along the wind by this many cells
const int Waves = 3;                      // times per repeat
(int X, int Y) lanes = (2, 7);            // streaks along and across the wind per repeat
const int Octaves = 3;
(int X, int Y) wisps = (3, 5);            // coastal fog banks' size: repeats per tile along and across the wind

string root = Path.GetFullPath(Path.Combine(Here(), "../.."));
string outPath = Path.Combine(root, "KotH.Game/Resources/Textures/CloudMap.png");

var rng = new NumPyRandom(Seed);
double[] red = Cloudlets(rng);
double[] green = Lanes(rng);
// Their own seed: red and green stay as they were.
double[] blue = Wisps(new NumPyRandom(Seed + 1));
var png = new Png(Size, Size, 3);
for (int p = 0; p < Size * Size; p++)
{
    png.Pixels[3 * p] = Byte(red[p]);
    png.Pixels[3 * p + 1] = Byte(green[p]);
    png.Pixels[3 * p + 2] = Byte(blue[p]);
}
png.Write(outPath);
Console.WriteLine($"geschrieben: {outPath}");
if (args.Contains("--preview"))
{
    var tile = new Png(2 * Size, 2 * Size, 1);
    for (int y = 0; y < 2 * Size; y++)
        for (int x = 0; x < 2 * Size; x++)
        {
            int p = (y % Size) * Size + x % Size;
            tile.Pixels[y * 2 * Size + x] = Byte(red[p] * Math.Clamp(green[p] * 1.6 - 0.3, 0.0, 1.0));
        }
    string path = Path.Combine(root, ".work/maps/clouds.png");
    tile.Write(path);
    Console.WriteLine($"Vorschau: {path}");
}

// np.round(v * 255).astype(uint8): half to even.
static byte Byte(double v) => (byte)Math.Round(v * 255);

// Domes round jittered points of a grid, on a torus, the highest wins.
double[] Cloudlets(NumPyRandom random)
{
    int gx = cells.X, gy = cells.Y;
    double cx = (double)Size / gx, cy = (double)Size / gy;
    double[] jitter = random.Random(gy * gx * 2);
    double[] radii = random.Random(gy * gx);
    var output = new double[Size * Size];
    for (int j = 0; j < gy; j++)
        for (int i = 0; i < gx; i++)
        {
            double py = (j + jitter[(j * gx + i) * 2 + 1]) * cy;
            // Rows of cloudlets across the wind bend into waves.
            double px = (i + jitter[(j * gx + i) * 2] + Wave * Libm.sin(2 * Math.PI * Waves * py / Size)) * cx;
            double r = (radius.Min + (radius.Max - radius.Min) * radii[j * gx + i]) * cx;
            double r2 = r * r;
            for (int y = 0; y < Size; y++)
            {
                double dy = (Remainder(y + 0.5 - py + Size / 2.0, Size) - Size / 2.0) / Across;
                for (int x = 0; x < Size; x++)
                {
                    double dx = Remainder(x + 0.5 - px + Size / 2.0, Size) - Size / 2.0;
                    double d2 = (dx * dx + dy * dy) / r2;
                    double dome = Math.Sqrt(Math.Clamp(1.0 - d2, 0.0, 1.0));
                    ref double o = ref output[y * Size + x];
                    o = Math.Max(o, dome);
                }
            }
        }
    return output;
}

// Tileable streaks along the wind: smooth noise on whole-number lattices.
double[] Lanes(NumPyRandom random)
{
    var output = new double[Size * Size];
    double total = 0.0;
    for (int o = 0; o < Octaves; o++)
    {
        int fx = lanes.X << o, fy = lanes.Y << o;
        double[] grid = random.Random(fy * fx);
        var i0 = new int[Size]; var i1 = new int[Size]; var su = new double[Size];
        var j0 = new int[Size]; var j1 = new int[Size]; var sv = new double[Size];
        for (int k = 0; k < Size; k++)
        {
            double u = (double)k / Size * fx, v = (double)k / Size * fy;
            i0[k] = (int)Math.Floor(u);
            j0[k] = (int)Math.Floor(v);
            double a = u - i0[k], b = v - j0[k];
            su[k] = a * a * (3 - 2 * a);
            sv[k] = b * b * (3 - 2 * b);
            i1[k] = (i0[k] + 1) % fx;
            j1[k] = (j0[k] + 1) % fy;
        }
        double weight = Math.Pow(0.5, o);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                double a = grid[j0[y] * fx + i0[x]] * (1 - su[x]) + grid[j0[y] * fx + i1[x]] * su[x];
                double b = grid[j1[y] * fx + i0[x]] * (1 - su[x]) + grid[j1[y] * fx + i1[x]] * su[x];
                output[y * Size + x] += weight * (a * (1 - sv[y]) + b * sv[y]);
            }
        total += weight;
    }
    for (int p = 0; p < output.Length; p++)
        output[p] /= total;
    return Stretch(output, 5, 95);
}

// Soft fog banks: white noise filtered in the frequency domain, so it tiles and has
// no lattice; longer along the wind than across.
double[] Wisps(NumPyRandom random)
{
    double[] k = NumPyFft.FftFreqTimesN(Size);
    var noise = new Complex[Size * Size];
    for (int p = 0; p < noise.Length; p++)
        noise[p] = new Complex(random.StandardNormal(), 0);
    Complex[] spectrum = NumPyFft.Fft2(noise, Size, true);
    for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            double kx = k[x] / wisps.X, ky = k[y] / wisps.Y;
            double amplitude = y == 0 && x == 0 ? 0.0 : 1.0 / Libm.pow(1.0 + kx * kx + ky * ky, 1.4);
            // numpy's complex times real: the real side as amplitude + 0j.
            Complex s = spectrum[y * Size + x];
            spectrum[y * Size + x] = new Complex(s.Real * amplitude - s.Imaginary * 0.0, s.Real * 0.0 + s.Imaginary * amplitude);
        }
    Complex[] field = NumPyFft.Fft2(spectrum, Size, false);
    return Stretch(field.Select(c => c.Real).ToArray(), 3, 97);
}

// Between the two percentiles to 0..1, clipped.
static double[] Stretch(double[] values, double low, double high)
{
    double lo = NumPyStats.Percentile(values, low), hi = NumPyStats.Percentile(values, high);
    return values.Select(v => Math.Clamp((v - lo) / (hi - lo), 0.0, 1.0)).ToArray();
}

// numpy's float remainder: the sign of the divisor, as Python's %.
static double Remainder(double a, double b)
{
    double mod = a % b;
    if (mod != 0)
    {
        if ((b < 0) != (mod < 0))
            mod += b;
    }
    else
        mod = b < 0 ? -0.0 : 0.0;
    return mod;
}

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path);
