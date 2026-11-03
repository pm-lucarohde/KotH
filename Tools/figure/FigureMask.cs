#!/usr/bin/env -S dotnet run
// FigureTint's masks (R cap, G skin, B leaves, A 1 - gloves and boots: clear texels lose their
// colour on import) for body, lids, glove: Mushroom[Lid|Glove]Mask.png. Prints region means
// (FigureLook's defaults). Run after .work/rig/build.sh, which remakes the lids; --debug
// writes the masks over the textures to .work/figure/.
#:include ../Common/Glb.cs
#:include ../Common/Png.cs
using System;
using System.Collections.Generic;
using System.IO;
using KotH.Tools;

const int N = 1024;                  // the masks are smooth: half the body texture's size
const int Grow = 4;                  // texels the masks reach past the UV islands, for the mips
const float Brim = 0.12f;            // glTF height of the cap's rim: red below it is not cap
const float CapTriangle = 0.4f;      // share of red texels that makes a triangle part of the cap
const float Boots = -0.6f;           // glTF height below which brown is the boots (coloured as the gloves)

string root = Path.GetFullPath(Path.Combine(Here(), "../.."));
string models = Path.Combine(root, "KotH.Game/Resources/Models");
string work = Path.Combine(root, ".work/figure");
bool debug = Array.IndexOf(args, "--debug") >= 0;

Png body = Png.Read(Path.Combine(models, "Mushroom.png"));
Png lid = Png.Read(Path.Combine(models, "MushroomLid.png"));
Png glove = Png.Read(Path.Combine(models, "MushroomGlove.png"));
Glb glb = Glb.Read(Path.Combine(models, "Mushroom.glb"));

// The body's triangles in UV space, with their height and normal (glTF: +Y up).
float[] pos = null, uv = null; int[] idx = null;
foreach (var p in glb.Json["meshes"]![0]!["primitives"]!.AsArray())
    if ((string)glb.Json["materials"]![(int)p!["material"]]!["name"] == "Mushroom")
    {
        pos = glb.Floats((int)p["attributes"]!["POSITION"]);
        uv = glb.Floats((int)p["attributes"]!["TEXCOORD_0"]);
        idx = glb.Ints((int)p["indices"]);
    }
if (pos == null)
    throw new InvalidDataException("no Mushroom primitive");

// Soft classes per mask texel (the texture's 2×2 texels averaged).
var cls = new (float red, float pale, float green)[N * N];
for (int y = 0; y < N; y++)
    for (int x = 0; x < N; x++)
        cls[y * N + x] = Classes(body, x, y, N);

// Pass 1: each triangle's share of red texels. Pass 2: per texel the highest triangle's
// height and normal, and whether any covering triangle is cap.
int triangles = idx.Length / 3;
var capShare = new float[triangles];
var height = new float[N * N];
var normalY = new float[N * N];
var capArea = new float[N * N];
Array.Fill(height, float.NaN);
for (int t = 0; t < triangles; t++)
{
    int red = 0, all = 0;
    Raster(t, (x, y) => { all++; if (cls[y * N + x].red > 0.5f) red++; });
    capShare[t] = all > 0 ? red / (float)all : 0f;
}
for (int t = 0; t < triangles; t++)
{
    int a = idx[t * 3], b = idx[t * 3 + 1], c = idx[t * 3 + 2];
    float h = (pos[a * 3 + 1] + pos[b * 3 + 1] + pos[c * 3 + 1]) / 3f;
    float ny = NormalY(a, b, c);
    float cap = Smooth(CapTriangle - 0.15f, CapTriangle + 0.15f, capShare[t]) * Smooth(Brim - 0.04f, Brim + 0.04f, h);
    Raster(t, (x, y) =>
    {
        int i = y * N + x;
        capArea[i] = MathF.Max(capArea[i], cap);
        if (float.IsNaN(height[i]) || h > height[i])
        {
            height[i] = h;
            normalY[i] = ny;
        }
    });
}

var island = new bool[N * N];
for (int i = 0; i < N * N; i++)
    island[i] = !float.IsNaN(height[i]);
int[] label = Islands(island, out int islands);
// The gills: whole islands above the brim that face down on average (a texel's own normal is
// no guide: the forehead tucks under the cap as steeply as the gills curve).
float[] islandNy = Means(label, islands, normalY), islandH = Means(label, islands, height);

// The masks: cap = cap triangles above the brim (see the dots below), not on islands that are
// mostly skin (the face: brows, mouth); skin = pale outside cap triangles and gill islands
// (the dots stay white); leaves = green; boots (A) = brown below the knee, as the gloves.
var mask = new float[4][];
for (int k = 0; k < 4; k++)
    mask[k] = new float[N * N];
for (int i = 0; i < N * N; i++)
{
    if (!island[i])
        continue;
    mask[0][i] = cls[i].red * capArea[i];
    bool gill = islandNy[label[i]] < -0.4f && islandH[label[i]] > Brim;
    mask[1][i] = gill ? 0f : cls[i].pale * (1f - capArea[i]);
    mask[2][i] = cls[i].green;
    mask[3][i] = Brown(body, i % N, i / N, N) * Smooth(Boots + 0.05f, Boots - 0.05f, height[i]);
}
var dot = new float[N * N];
foreach (int i in Dots(mask[1], Widen(mask[0], 3), island))
{
    mask[1][i] = 0f;
    dot[i] = 1f;
}
// The whole cap is recoloured but the dots' white centres: their painted shade turns with it.
float[] nearDot = Widen(dot, 3);
for (int i = 0; i < N * N; i++)
    if (island[i])
        mask[0][i] = MathF.Max(capArea[i], nearDot[i] * Smooth(Brim - 0.04f, Brim + 0.04f, height[i])) * (1f - cls[i].pale);
float[] islandSkin = Means(label, islands, mask[1]);
for (int i = 0; i < N * N; i++)
    if (island[i] && islandSkin[label[i]] > 0.5f)
        mask[0][i] = 0f;
for (int k = 0; k < 4; k++)
    mask[k] = Spread(Blur(mask[k], island), island);

// The lids: skin wherever pale (blush included), the rest (lashes, eyes, mouth) stays.
int L = lid.Width;
var lidSkin = new float[L * L];
for (int i = 0; i < L * L; i++)
    lidSkin[i] = Classes(lid, i % L, i / L, L).pale;
lidSkin = Blur(lidSkin, null, L);

// References: each region's mean colour where its mask is nearly whole.
float[] capRef = Mean(body, mask[0], N), skinRef = Mean(body, mask[1], N), leafRef = Mean(body, mask[2], N);
float[] gloveRef = Mean(glove, null, glove.Width);
Png spore = Png.Read(Path.Combine(root, "KotH.Game/Resources/Effects/Spore.png"));
Console.WriteLine($"cap    {Hex(capRef)}");
Console.WriteLine($"skin   {Hex(skinRef)}  (lids {Hex(Mean(lid, lidSkin, L))})");
Console.WriteLine($"leaves {Hex(leafRef)}");
Console.WriteLine($"gloves {Hex(gloveRef)}  (boots {Hex(Mean(body, mask[3], N))})");
Console.WriteLine($"spore texture {Hex(Mean(spore, null, spore.Width))}");

Write(Path.Combine(models, "MushroomMask.png"), N, i => (mask[0][i], mask[1][i], mask[2][i], 1f - mask[3][i]));
Write(Path.Combine(models, "MushroomLidMask.png"), L, i => (0f, lidSkin[i], 0f, 1f));
Write(Path.Combine(models, "MushroomGloveMask.png"), 4, i => (0f, 0f, 0f, 0f));
if (debug)
{
    Directory.CreateDirectory(work);
    Overlay(body, N, i => (mask[0][i], mask[1][i], mask[2][i])).Write(Path.Combine(work, "body_mask.png"));
    Overlay(lid, L, i => (0f, lidSkin[i], 0f)).Write(Path.Combine(work, "lid_mask.png"));
}
return 0;

// Red (cap; not brown, not the blush), light and unsaturated or pink (skin), green (leaves,
// as in rig.py); size = the grid's.
static (float red, float pale, float green) Classes(Png png, int x, int y, int size)
{
    int sx = png.Width / size, sy = png.Height / size;
    float r = 0f, g = 0f, b = 0f;
    for (int v = 0; v < sy; v++)
        for (int u = 0; u < sx; u++)
        {
            int i = ((y * sy + v) * png.Width + x * sx + u) * png.Channels;
            r += png.Pixels[i]; g += png.Pixels[i + 1]; b += png.Pixels[i + 2];
        }
    float n = 255f * sx * sy;
    r /= n; g /= n; b /= n;
    float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
    float s = max > 1e-4f ? (max - min) / max : 0f;
    float redness = Smooth(0.45f, 0.6f, s) * Smooth(0.15f, 0.25f, r) * Smooth(0.45f, 0.3f, MathF.Max(g, b) / MathF.Max(r, 1e-4f));
    float pale = Smooth(0.42f, 0.28f, s) * Smooth(0.5f, 0.65f, max);
    // The blush: light pink, half moved with the skin, so it stays a blush on any colour.
    float gr = g / MathF.Max(r, 1e-4f);
    pale = MathF.Max(pale, 0.5f * Smooth(0.6f, 0.75f, r) * Smooth(0.45f, 0.55f, gr) * Smooth(0.9f, 0.8f, gr) * Smooth(0.6f, 0.5f, s));
    float greenness = g > r * 1.15f && g > b * 1.2f ? Smooth(0.15f, 0.3f, s) : 0f;
    return (redness, pale, greenness);
}

// Brown (boots, belt, bag): warm, middling saturation, not light.
static float Brown(Png png, int x, int y, int size)
{
    int i = (y * png.Height / size * png.Width + x * png.Width / size) * png.Channels;
    float r = png.Pixels[i] / 255f, g = png.Pixels[i + 1] / 255f, b = png.Pixels[i + 2] / 255f;
    float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
    float s = max > 1e-4f ? (max - min) / max : 0f;
    float gr = g / MathF.Max(r, 1e-4f);
    return r >= g && g >= b ? Smooth(0.25f, 0.35f, s) * Smooth(0.3f, 0.45f, gr) * Smooth(0.85f, 0.75f, gr) * Smooth(0.75f, 0.6f, max) : 0f;
}

float NormalY(int a, int b, int c)
{
    float ux = pos[b * 3] - pos[a * 3], uy = pos[b * 3 + 1] - pos[a * 3 + 1], uz = pos[b * 3 + 2] - pos[a * 3 + 2];
    float vx = pos[c * 3] - pos[a * 3], vy = pos[c * 3 + 1] - pos[a * 3 + 1], vz = pos[c * 3 + 2] - pos[a * 3 + 2];
    float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
    float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
    return len > 1e-12f ? ny / len : 0f;
}

void Raster(int t, Action<int, int> put)
{
    int a = idx[t * 3], b = idx[t * 3 + 1], c = idx[t * 3 + 2];
    Fill(uv[a * 2] * N, uv[a * 2 + 1] * N, uv[b * 2] * N, uv[b * 2 + 1] * N, uv[c * 2] * N, uv[c * 2 + 1] * N, put);
}

// Connected texels of the UV layout: each texel's island, -1 outside.
static int[] Islands(bool[] inside, out int count)
{
    var label = new int[N * N];
    Array.Fill(label, -1);
    var stack = new Stack<int>();
    count = 0;
    for (int start = 0; start < N * N; start++)
    {
        if (label[start] >= 0 || !inside[start])
            continue;
        label[start] = count; stack.Push(start);
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            foreach (int j in (ReadOnlySpan<int>)[i - 1, i + 1, i - N, i + N])
                if (j >= 0 && j < N * N && Math.Abs(j % N - i % N) <= 1 && inside[j] && label[j] < 0)
                { label[j] = count; stack.Push(j); }
        }
        count++;
    }
    return label;
}

// Each island's mean of v.
static float[] Means(int[] label, int count, float[] v)
{
    var sum = new double[count];
    var n = new int[count];
    for (int i = 0; i < N * N; i++)
        if (label[i] >= 0)
        { sum[label[i]] += v[i]; n[label[i]]++; }
    var mean = new float[count];
    for (int k = 0; k < count; k++)
        mean[k] = (float)(sum[k] / Math.Max(1, n[k]));
    return mean;
}

// The cap's white dots: pale patches whose rim (inside the islands) is mostly cap.
static List<int> Dots(float[] pale, float[] cap, bool[] inside)
{
    var dots = new List<int>();
    var seen = new bool[N * N];
    var patch = new List<int>();
    var stack = new Stack<int>();
    for (int start = 0; start < N * N; start++)
    {
        if (seen[start] || pale[start] < 0.5f)
            continue;
        patch.Clear(); int rim = 0, red = 0;
        seen[start] = true; stack.Push(start);
        while (stack.Count > 0)
        {
            int i = stack.Pop(); patch.Add(i);
            foreach (int j in (ReadOnlySpan<int>)[i - 1, i + 1, i - N, i + N])
            {
                if (j < 0 || j >= N * N || Math.Abs(j % N - i % N) > 1 || !inside[j] || seen[j])
                    continue;
                if (pale[j] >= 0.5f)
                { seen[j] = true; stack.Push(j); }
                else
                { rim++; if (cap[j] > 0.5f) red++; }
            }
        }
        if (red > 0.5f * rim)
            dots.AddRange(patch);
    }
    return dots;
}

// Max filter over (2r+1)², so a dot's antialiased rim counts as cap.
static float[] Widen(float[] m, int r)
{
    var o = new float[N * N];
    for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float best = 0f;
            for (int v = Math.Max(0, y - r); v <= Math.Min(N - 1, y + r); v++)
                for (int u = Math.Max(0, x - r); u <= Math.Min(N - 1, x + r); u++)
                    best = MathF.Max(best, m[v * N + u]);
            o[y * N + x] = best;
        }
    return o;
}

// A 3×3 box inside the islands (null: everywhere), so edges between regions soften by a texel.
static float[] Blur(float[] m, bool[] inside, int size = N)
{
    var o = new float[m.Length];
    for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = y * size + x;
            if (inside != null && !inside[i])
                continue;
            float sum = 0f; int n = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int u = x + dx, v = y + dy;
                    if (u < 0 || v < 0 || u >= size || v >= size || (inside != null && !inside[v * size + u]))
                        continue;
                    sum += m[v * size + u]; n++;
                }
            o[i] = sum / n;
        }
    return o;
}

// Grows the islands by Grow texels, each new texel the mean of its covered neighbours.
static float[] Spread(float[] m, bool[] inside)
{
    var have = (bool[])inside.Clone();
    for (int step = 0; step < Grow; step++)
    {
        var next = (bool[])have.Clone();
        var o = (float[])m.Clone();
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                int i = y * N + x;
                if (have[i])
                    continue;
                float sum = 0f; int n = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int u = x + dx, v = y + dy;
                        if (u >= 0 && v >= 0 && u < N && v < N && have[v * N + u])
                        { sum += m[v * N + u]; n++; }
                    }
                if (n > 0)
                { o[i] = sum / n; next[i] = true; }
            }
        m = o; have = next;
    }
    return m;
}

// Mean colour (0..1) of the texture where the mask (at size², null: all) is above 0.9.
static float[] Mean(Png png, float[] m, int size)
{
    double r = 0, g = 0, b = 0, n = 0;
    for (int y = 0; y < png.Height; y++)
        for (int x = 0; x < png.Width; x++)
        {
            if (m != null && m[y * size / png.Height * size + x * size / png.Width] < 0.9f)
                continue;
            int i = (y * png.Width + x) * png.Channels;
            double w = png.Channels == 4 ? png.Pixels[i + 3] / 255.0 : 1.0;
            r += png.Pixels[i] * w; g += png.Pixels[i + 1] * w; b += png.Pixels[i + 2] * w; n += w;
        }
    return [(float)(r / n / 255), (float)(g / n / 255), (float)(b / n / 255)];
}

static string Hex(float[] c) => $"#{(int)MathF.Round(c[0] * 255):X2}{(int)MathF.Round(c[1] * 255):X2}{(int)MathF.Round(c[2] * 255):X2}";

static void Write(string path, int size, Func<int, (float, float, float, float)> at)
{
    var png = new Png(size, size, 4);
    for (int i = 0; i < size * size; i++)
    {
        var (r, g, b, a) = at(i);
        png.Pixels[i * 4] = Byte(r); png.Pixels[i * 4 + 1] = Byte(g);
        png.Pixels[i * 4 + 2] = Byte(b); png.Pixels[i * 4 + 3] = Byte(a);
    }
    png.Write(path);
    Console.WriteLine($"wrote {Path.GetFileName(path)}");
}

// The texture greyed, each region in a loud colour (cap blue, skin magenta, leaves yellow).
static Png Overlay(Png tex, int size, Func<int, (float, float, float)> at)
{
    var o = new Png(size, size, 3);
    for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = (y * tex.Height / size * tex.Width + x * tex.Width / size) * tex.Channels;
            float grey = (tex.Pixels[i] + tex.Pixels[i + 1] + tex.Pixels[i + 2]) / 765f * 0.6f;
            var (c, s, l) = at(y * size + x);
            float rest = 1f - MathF.Min(1f, c + s + l);
            o.Pixels[(y * size + x) * 3] = Byte(grey * rest + s + l);
            o.Pixels[(y * size + x) * 3 + 1] = Byte(grey * rest + l);
            o.Pixels[(y * size + x) * 3 + 2] = Byte(grey * rest + c + s);
        }
    return o;
}

static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

static float Smooth(float e0, float e1, float x)
{
    float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
    return t * t * (3f - 2f * t);
}

// Fills the texels whose centres lie in the triangle (texel units, y down as in the image).
static void Fill(float ax, float ay, float bx, float by, float cx, float cy, Action<int, int> put)
{
    int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
    int x1 = Math.Min(N - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
    int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
    int y1 = Math.Min(N - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
    float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
    if (MathF.Abs(area) < 1e-9f)
        return;
    for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            float w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) / area;
            float w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) / area;
            if (w0 >= -1e-4f && w1 >= -1e-4f && 1f - w0 - w1 >= -1e-4f)
                put(x, y);
        }
}

static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
