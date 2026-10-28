#!/usr/bin/env -S dotnet run
// KotH's own sky (Luca, 28.10.2026): a clear midday with some warmth, after Star Wars
// Battlefront (2015) - deep blue overhead, a pale, warm haze at the horizon, a small
// sharp sun with a bright halo - and a touch more colour than physics gives. Single
// scattering after Nishita (Rayleigh and Mie) with the scene's sun: its direction and
// colour come from the directional light in MainScene. Writes the HDR cube map
// KotH.Game/Resources/Sky.dds (512², RGBA half, D3D face order) and a preview.
//
// The sky also lights the map (LightVoxel.Sky): it prints the SkyIntensity for NeuVisual
// that keeps the light from above at 92 % of Stride's default sky's (Luca: 5-10 % less).
//
//   dotnet run Tools/maps/Sky.cs [-- --preview]
#:include ../Common/Png.cs
using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using KotH.Tools;

const int N = 512;                        // texels per face side
const double Background = 0.35;           // the scene's background intensity
// The deepest blue on screen (linear, 45° up and 90° round from the sun) and the colour
// beyond physics (1 = none), less at the horizon so its haze stays pale.
Vector3 deepBlue = new(0.045f, 0.19f, 0.58f);
(double Horizon, double High) saturation = (0.55, 1.12);
const double Knee = 0.7, Ceiling = 0.88;    // on screen the sky bends from Knee towards Ceiling: only the disc is white
// Haze: Mie scattering at sea level (Nishita 21e-6); more is milkier and widens the halo.
const double MieScatter = 8e-6;
const double MieG = 0.85;
const double Warmth = 0.6;                // how much of the light's colour the haze takes; the air takes none
const double SunDegrees = 3.25;           // the disc's radius
const double SunEdge = 1.6;               // its rim fades over this much, smoothly
const double SunDisc = 5.0;               // per channel in the map: white on screen up to its rim, no brighter
Vector3 ground = new(0.20f, 0.27f, 0.33f);   // below the horizon, times the horizon's brightness

string root = Path.GetFullPath(Path.Combine(Here(), "../.."));
string scenePath = Path.Combine(root, "KotH.Game/Assets/MainScene.sdscene");
string outPath = Path.Combine(root, "KotH.Game/Resources/Sky.dds");
bool preview = Array.IndexOf(args, "--preview") >= 0;

// The sun: the directional light's forward (-Z) turned by its rotation, reversed; its colour.
string scene = File.ReadAllText(scenePath);
var light = Regex.Match(scene, @"Name: Directional light.*?Rotation: \{X: (\S+), Y: (\S+), Z: (\S+), W: (\S+)\}.*?Value: \{R: (\S+), G: (\S+), B: (\S+)\}", RegexOptions.Singleline);
if (!light.Success)
    throw new InvalidDataException("Kein Directional light in MainScene.sdscene");
float F(int g) => float.Parse(light.Groups[g].Value.TrimEnd(','), CultureInfo.InvariantCulture);
var q = new Quaternion(F(1), F(2), F(3), F(4));
Vector3 toSun = -Vector3.Transform(-Vector3.UnitZ, q);
Vector3 sunColour = new(F(5), F(6), F(7));
Console.WriteLine($"Sonne ({toSun.X:F3}, {toSun.Y:F3}, {toSun.Z:F3}), {Math.Asin(toSun.Y) * 180 / Math.PI:F1}° hoch");

// Nishita's atmosphere, metres.
const double Earth = 6360e3, Top = 6420e3, Eye = 50.0;
const double RayleighHeight = 7994, MieHeight = 1200;
Vector3 rayleigh = new(5.802e-6f, 13.558e-6f, 33.1e-6f);
const int ViewSteps = 24, LightSteps = 8;

// Radiance seen along d (world, y up), Rayleigh and Mie apart, for unit sun irradiance.
(Vector3 R, Vector3 M) Scatter(Vector3 d)
{
    var o = new Vector3(0, (float)(Earth + Eye), 0);
    double far = Exit(o, d, Top);
    double ds = far / ViewSteps;
    Vector3 sumR = default, sumM = default;
    double depthR = 0, depthM = 0;
    for (int i = 0; i < ViewSteps; i++)
    {
        Vector3 p = o + d * (float)((i + 0.5) * ds);
        double h = p.Length() - Earth;
        double hr = Math.Exp(-h / RayleighHeight) * ds, hm = Math.Exp(-h / MieHeight) * ds;
        depthR += hr; depthM += hm;
        // Light reaching p from the sun; none if the earth is in the way.
        double lightFar = Exit(p, toSun, Top), dl = lightFar / LightSteps;
        double lr = 0, lm = 0;
        bool lit = true;
        for (int j = 0; j < LightSteps; j++)
        {
            double hl = (p + toSun * (float)((j + 0.5) * dl)).Length() - Earth;
            if (hl < 0) { lit = false; break; }
            lr += Math.Exp(-hl / RayleighHeight) * dl;
            lm += Math.Exp(-hl / MieHeight) * dl;
        }
        if (!lit)
            continue;
        Vector3 tau = rayleigh * (float)(depthR + lr) + new Vector3((float)(1.11 * MieScatter * (depthM + lm)));
        var att = new Vector3(MathF.Exp(-tau.X), MathF.Exp(-tau.Y), MathF.Exp(-tau.Z));
        sumR += att * (float)hr;
        sumM += att * (float)hm;
    }
    float mu = Vector3.Dot(d, toSun);
    float phaseR = 3f / (16f * MathF.PI) * (1 + mu * mu);
    double g2 = MieG * MieG;
    float phaseM = (float)(3.0 / (8.0 * Math.PI) * (1 - g2) * (1 + mu * mu) / ((2 + g2) * Math.Pow(1 + g2 - 2 * MieG * mu, 1.5)));
    return (sumR * rayleigh * phaseR, sumM * (float)MieScatter * phaseM * Vector3.Lerp(Vector3.One, sunColour, (float)Warmth));
}

// The far root of |o + t d| = radius (o inside the sphere).
static double Exit(Vector3 o, Vector3 d, double radius)
{
    double b = Vector3.Dot(o, d), c = o.LengthSquared() - radius * radius;
    return -b + Math.Sqrt(b * b - c);
}

// The sky in direction d without the disc: below the horizon the horizon's colour fades to ground.
Vector3 Sky(Vector3 d)
{
    if (d.Y >= 0)
    {
        var (r, m) = Scatter(d);
        return r + m;
    }
    var flat = Vector3.Normalize(new Vector3(d.X, 0.0005f, d.Z));
    var (hr, hm) = Scatter(flat);
    float t = MathF.Min(1f, -d.Y / 0.12f);
    Vector3 horizon = hr + hm;
    float bright = Vector3.Dot(horizon, new Vector3(0.2126f, 0.7152f, 0.0722f));
    return Vector3.Lerp(horizon, ground * bright, t * t * (3 - 2 * t));
}

// Exposure: the deepest blue on screen as asked; then the colour lifted round the grey.
var side = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toSun));
Vector3 deep = Sky(Vector3.Normalize(side + Vector3.UnitY));
float exposure = (float)(deepBlue.Z / Background / deep.Z);
Vector3 Grade(Vector3 c, Vector3 d)
{
    c *= exposure;
    float grey = Vector3.Dot(c, new Vector3(0.2126f, 0.7152f, 0.0722f));
    float t = Math.Clamp(d.Y / 0.25f, 0f, 1f);
    double lift = saturation.Horizon + (saturation.High - saturation.Horizon) * t;
    c = Vector3.Max(Vector3.Zero, new Vector3(grey) + (c - new Vector3(grey)) * (float)lift);
    float top = MathF.Max(c.X, MathF.Max(c.Y, c.Z)) * (float)Background;
    if (top <= Knee)
        return c;
    float bent = (float)(Knee + (Ceiling - Knee) * (1 - Math.Exp(-(top - Knee) / (Ceiling - Knee))));
    return c * (bent / top);
}

double sunCos = Math.Cos((SunDegrees + SunEdge) * Math.PI / 180);
var cube = new Vector3[6 * N * N];
Parallel.For(0, 6 * N, row =>
{
    int f = row / N, j = row % N;
    for (int i = 0; i < N; i++)
    {
        // Stride samples the cube at (x, y, -z) of the world direction.
        Vector3 c = Vector3.Normalize(Direction(f, i, j, N));
        var d = new Vector3(c.X, c.Y, -c.Z);
        Vector3 colour = Grade(Sky(d), d);
        float mu = Vector3.Dot(d, toSun);
        if (mu > sunCos)
        {
            double angle = Math.Acos(Math.Min(1f, mu)) * 180 / Math.PI;
            double disc = Math.Clamp((SunDegrees + SunEdge / 2 - angle) / SunEdge, 0, 1);
            disc = disc * disc * (3 - 2 * disc);
            double limb = 1 - 0.4 * Math.Pow(angle / SunDegrees, 2);
            colour += sunColour * (float)(SunDisc * disc * Math.Max(limb, 0.6));
        }
        cube[(f * N + j) * N + i] = colour;
    }
});

// DDS with a DX10 header: one mip, cube, R16G16B16A16_FLOAT.
var dds = new byte[148 + cube.Length * 8];
"DDS "u8.CopyTo(dds);
int[] head = [124, 0x2100F, N, N, N * 8, 1, 1];
for (int i = 0; i < head.Length; i++)
    BitConverter.TryWriteBytes(dds.AsSpan(4 + 4 * i), head[i]);
BitConverter.TryWriteBytes(dds.AsSpan(76), 32);
BitConverter.TryWriteBytes(dds.AsSpan(80), 4);
"DX10"u8.CopyTo(dds.AsSpan(84));
BitConverter.TryWriteBytes(dds.AsSpan(108), 0x1008);
BitConverter.TryWriteBytes(dds.AsSpan(112), 0xFE00);
int[] dx10 = [10, 3, 4, 1, 0];
for (int i = 0; i < dx10.Length; i++)
    BitConverter.TryWriteBytes(dds.AsSpan(128 + 4 * i), dx10[i]);
for (int i = 0; i < cube.Length; i++)
{
    int b = 148 + i * 8;
    BitConverter.TryWriteBytes(dds.AsSpan(b), (Half)cube[i].X);
    BitConverter.TryWriteBytes(dds.AsSpan(b + 2), (Half)cube[i].Y);
    BitConverter.TryWriteBytes(dds.AsSpan(b + 4), (Half)cube[i].Z);
    BitConverter.TryWriteBytes(dds.AsSpan(b + 6), (Half)1f);
}
File.WriteAllBytes(outPath, dds);
Console.WriteLine($"Geschrieben: {outPath}");

// Light from above (cosine-weighted, normal up) against Stride's default sky's at SkyIntensity 0.26.
Vector3 up = Irradiance(cube, N);
var luma = new Vector3(0.2126f, 0.7152f, 0.0722f);
double defaultUp = Vector3.Dot(new Vector3(1.42f, 2.58f, 4.25f), luma);
Console.WriteLine($"Licht von oben: ({up.X:F2}, {up.Y:F2}, {up.Z:F2}); SkyIntensity für 92 %: {0.26 * 0.92 * defaultUp / Vector3.Dot(up, luma):F3}");

if (preview)
{
    // Equirectangular, as on screen (background intensity, sRGB, clipped).
    const int W = 1024, H = 512;
    var png = new Png(W, H, 3);
    Parallel.For(0, H, y =>
    {
        for (int x = 0; x < W; x++)
        {
            double lon = (x + 0.5) / W * 2 * Math.PI - Math.PI, lat = Math.PI / 2 - (y + 0.5) / H * Math.PI;
            var d = new Vector3((float)(Math.Cos(lat) * Math.Sin(lon)), (float)Math.Sin(lat), (float)(-Math.Cos(lat) * Math.Cos(lon)));
            Vector3 c = Lookup(cube, N, new Vector3(d.X, d.Y, -d.Z)) * (float)Background;
            int k = (y * W + x) * 3;
            png.Pixels[k] = Srgb(c.X); png.Pixels[k + 1] = Srgb(c.Y); png.Pixels[k + 2] = Srgb(c.Z);
        }
    });
    string previewPath = Path.Combine(root, ".work/sky/preview.png");
    Directory.CreateDirectory(Path.GetDirectoryName(previewPath));
    png.Write(previewPath);
    Console.WriteLine($"Vorschau: {previewPath} (Mitte = -Z)");
}

static byte Srgb(float v)
{
    v = Math.Clamp(v, 0f, 1f);
    double s = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
    return (byte)Math.Round(s * 255);
}

// Texel centre (i, j) of face f to its cube direction, D3D layout (+X, -X, +Y, -Y, +Z, -Z).
static Vector3 Direction(int f, double i, double j, int n)
{
    float s = (float)(2 * (i + 0.5) / n - 1), t = (float)(2 * (j + 0.5) / n - 1);
    return f switch
    {
        0 => new(1, -t, -s), 1 => new(-1, -t, s), 2 => new(s, 1, t),
        3 => new(s, -1, -t), 4 => new(s, -t, 1), _ => new(-s, -t, -1),
    };
}

// The nearest texel in cube direction d.
static Vector3 Lookup(Vector3[] cube, int n, Vector3 d)
{
    Vector3 a = Vector3.Abs(d);
    int f; float s, t;
    if (a.X >= a.Y && a.X >= a.Z) { f = d.X > 0 ? 0 : 1; s = (d.X > 0 ? -d.Z : d.Z) / a.X; t = -d.Y / a.X; }
    else if (a.Y >= a.Z) { f = d.Y > 0 ? 2 : 3; s = d.X / a.Y; t = (d.Y > 0 ? d.Z : -d.Z) / a.Y; }
    else { f = d.Z > 0 ? 4 : 5; s = (d.Z > 0 ? d.X : -d.X) / a.Z; t = -d.Y / a.Z; }
    int i = Math.Clamp((int)((s + 1) / 2 * n), 0, n - 1), j = Math.Clamp((int)((t + 1) / 2 * n), 0, n - 1);
    return cube[(f * n + j) * n + i];
}

// Cosine-weighted light onto a surface facing up: the sum over texels of colour, cosine and solid angle.
static Vector3 Irradiance(Vector3[] cube, int n)
{
    Vector3 sum = default;
    for (int f = 0; f < 6; f++)
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                Vector3 c = Direction(f, i, j, n);
                float len = c.Length(), cos = c.Y / len;
                if (cos <= 0)
                    continue;
                float solid = 4f / (n * n) / (len * len * len);
                sum += cube[(f * n + j) * n + i] * cos * solid;
            }
    return sum;
}

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path);
