#:include ../Common/Png.cs
// The Stride map kit's sample textures (MapKit/Stride/KotHMapKit.Game/Resources): grass and rock
// noise (tileable), planks, and a leaf cluster with alpha. Deterministic; run after changes.
//   dotnet run Tools/mapkit/KitTextures.cs
using System;
using System.IO;
using KotH.Tools;

string dir = Path.Combine(Directory.GetCurrentDirectory(), "MapKit/Stride/KotHMapKit.Game/Resources");
Directory.CreateDirectory(dir);
Noise(256, (52, 118, 40), (96, 160, 62), 8).Write(Path.Combine(dir, "Grass.png"));
Noise(256, (92, 90, 86), (150, 146, 138), 4).Write(Path.Combine(dir, "Rock.png"));
Planks().Write(Path.Combine(dir, "Planks.png"));
Leaves().Write(Path.Combine(dir, "Leaves.png"));
Console.WriteLine($"textures in {dir}");

// Tileable value noise of two octaves between two colours.
static Png Noise(int size, (int, int, int) a, (int, int, int) b, int cells)
{
    var png = new Png(size, size, 3);
    for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float t = 0.65f * Value(x, y, size, cells, 1) + 0.35f * Value(x, y, size, cells * 4, 2);
            int i = (y * size + x) * 3;
            png.Pixels[i] = (byte)(a.Item1 + (b.Item1 - a.Item1) * t);
            png.Pixels[i + 1] = (byte)(a.Item2 + (b.Item2 - a.Item2) * t);
            png.Pixels[i + 2] = (byte)(a.Item3 + (b.Item3 - a.Item3) * t);
        }
    return png;
}

static float Value(int x, int y, int size, int cells, int seed)
{
    float fx = (float)x * cells / size, fy = (float)y * cells / size;
    int x0 = (int)fx, y0 = (int)fy;
    float sx = Smooth(fx - x0), sy = Smooth(fy - y0);
    float Corner(int cx, int cy) => Hash(cx % cells, cy % cells, seed);
    float top = Corner(x0, y0) + (Corner(x0 + 1, y0) - Corner(x0, y0)) * sx;
    float bottom = Corner(x0, y0 + 1) + (Corner(x0 + 1, y0 + 1) - Corner(x0, y0 + 1)) * sx;
    return top + (bottom - top) * sy;
}

static float Smooth(float t) => t * t * (3f - 2f * t);

static float Hash(int x, int y, int seed)
{
    uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
    h = (h ^ (h >> 13)) * 1274126177u;
    return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
}

static Png Planks()
{
    var png = new Png(128, 128, 3);
    for (int y = 0; y < 128; y++)
        for (int x = 0; x < 128; x++)
        {
            int board = y / 32;
            bool seam = y % 32 == 0 || (x + board * 48) % 128 == 0;
            float grain = 0.85f + 0.15f * MathF.Sin(x * 0.35f + board * 2f + MathF.Sin(y * 0.4f) * 1.5f);
            int i = (y * 128 + x) * 3;
            png.Pixels[i] = (byte)(seam ? 70 : 168 * grain);
            png.Pixels[i + 1] = (byte)(seam ? 45 : 116 * grain);
            png.Pixels[i + 2] = (byte)(seam ? 25 : 66 * grain);
        }
    return png;
}

// Seven round leaves on a card, alpha outside.
static Png Leaves()
{
    var png = new Png(128, 128, 4);
    (float X, float Y, float R)[] leaves = { (64, 40, 26), (36, 62, 24), (92, 62, 24), (48, 92, 26), (82, 94, 26), (64, 66, 28), (64, 112, 14) };
    for (int y = 0; y < 128; y++)
        for (int x = 0; x < 128; x++)
        {
            float best = float.MaxValue;
            foreach (var l in leaves)
                best = MathF.Min(best, MathF.Sqrt((x - l.X) * (x - l.X) + (y - l.Y) * (y - l.Y)) / l.R);
            int i = (y * 128 + x) * 4;
            png.Pixels[i] = (byte)(40 + 30 * best);
            png.Pixels[i + 1] = (byte)(110 + 50 * best);
            png.Pixels[i + 2] = (byte)(30 + 10 * best);
            png.Pixels[i + 3] = (byte)(best < 1f ? 255 : 0);
        }
    return png;
}
