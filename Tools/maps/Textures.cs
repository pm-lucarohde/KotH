#!/usr/bin/env -S dotnet run
// Painted textures for map "Neu", in the style of the mushroom figures: saturated colours,
// clear shapes with a darker rim, a light plane and broad brush strokes inside every shape,
// like Mushroom.png (Luca, 22.10.2026: "not washed out", then "a touch softer and more like
// a comic"). Seeded, so every run paints the same; painted with Pillow's algorithms
// (Common/Pil.cs), so the pictures are the ones the Python script made.
//
// Writes tileable PNGs to KotH.Game/Resources/Textures/Terrain (grass, dirt, sand, snow, rock
// for TerrainTriplanar; bark for PaintedTriplanar, single leaves for LeafSplat; the sea is
// one plain colour, Luca's wish). Sizes below are in pixels of a 1024 texture, scaled by K.
//
//   dotnet run Tools/maps/Textures.cs [-- --preview]
#:include ../Common/Libm.cs
#:include ../Common/Py.cs
#:include ../Common/Png.cs
#:include ../Common/Pil.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using KotH.Tools;
using Pts = System.Collections.Generic.List<(double X, double Y)>;

const int Size = 2048;               // ground: 4 m per repeat (TerrainTriplanar), 512 px/m
const int Small = Size / 2;          // leaves and bark: 2 m per repeat (PaintedTriplanar)
const double K = Size / 1024.0;
const double Edge = 1.0 * K;         // blur of an edge: soft, but the shape stays clear
const int Seed = 22;
const double LeafFill = 0.94;        // a leaf's length in its quarter of the atlas (LeafSplat.sdsl: Fill)
const double LeafWidth = 0.43;       // its width over its length (LeafSplat.sdsl: Width)

string root = Path.GetFullPath(Path.Combine(Here(), "../.."));
string outDir = Path.Combine(root, "KotH.Game/Resources/Textures/Terrain");

// Grass and sand also give their relief (Luca: "not quite so flat"), written as NAMENormal.png.
var painters = new (string Name, Func<PyRandom, (PilImage Image, double[] Relief)> Paint)[]
{
    ("Grass", Grass), ("Dirt", Flat(Dirt)), ("Sand", Sand), ("Snow", Flat(Snow)), ("Rock", Flat(Rock)),
    ("Leaves", Flat(Leaves)), ("Bark", Flat(Bark)), ("LeafSprig", Flat(Sprig)),
};
Directory.CreateDirectory(outDir);
var painted = new List<PilImage>();
for (int n = 0; n < painters.Length; n++)
{
    var (img, relief) = painters[n].Paint(new PyRandom(Seed * 100 + n));
    img.ToPng().Write(Path.Combine(outDir, painters[n].Name + ".png"));
    Console.WriteLine($"{painters[n].Name}.png: {img.Width} x {img.Height}");
    painted.Add(img);
    if (relief != null)
    {
        NormalMap(relief).ToPng().Write(Path.Combine(outDir, painters[n].Name + "Normal.png"));
        Console.WriteLine($"{painters[n].Name}Normal.png: Relief {relief.Min() * 1000:F1} bis {relief.Max() * 1000:F1} mm");
    }
}

static Func<PyRandom, (PilImage, double[])> Flat(Func<PyRandom, PilImage> paint) => rng => (paint(rng), null);
if (args.Contains("--preview"))
{
    // Every texture tiled 2 x 2, to look for seams.
    var sheet = new PilImage("RGB", 512 * 4, 512 * 2, 0, 0, 0);
    for (int k = 0; k < painted.Count; k++)
    {
        PilImage t = painted[k].Resize(256, 256, PilImage.Filter.Lanczos);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                sheet.Paste(t, (k % 4) * 512 + i * 256, (k / 4) * 512 + j * 256);
    }
    sheet.ToPng().Write(Path.Combine(root, ".work/maps/textures.png"));
}

static int[] Mix(int[] a, int[] b, double t) => [.. Enumerable.Range(0, 3).Select(i => Py.Round(a[i] + (b[i] - a[i]) * t))];

static int[] Shade(int[] c, double k) => [.. c.Select(v => Math.Max(0, Math.Min(255, Py.Round(v * k))))];

static byte[] Ink(int[] c, int alpha = -1) => [(byte)c[0], (byte)c[1], (byte)c[2], (byte)(alpha >= 0 ? alpha : c.Length > 3 ? c[3] : 255)];

// Draws a shape at (x, y) and again across every edge it reaches over, so the texture
// tiles without seams.
static void Wrap(Action<double, double> draw, int size, double x, double y, double reach)
{
    foreach (int dx in new[] { -size, 0, size })
        foreach (int dy in new[] { -size, 0, size })
            if (-reach <= x + dx && x + dx <= size + reach && -reach <= y + dy && y + dy <= size + reach)
                draw(x + dx, y + dy);
}

// A blur that wraps round the edges like the tiling; layers blur premultiplied, else
// every edge gets a dark fringe.
static PilImage Blur(PilImage img, double radius)
{
    string mode = img.Mode;
    if (mode == "RGBA")
        img = img.Convert("RGBa");
    return img.GaussianBlurTiled(radius).Convert(mode);
}

static (PilImage, PilDraw) Layer(int size)
{
    var img = new PilImage("RGBA", size, size, 0, 0, 0, 0);
    return (img, new PilDraw(img));
}

// Soft tileable value noise from low to high (an L image).
static PilImage Clouds(PyRandom rng, int size, int cells, int low, int high)
{
    var small = new PilImage("L", cells, cells);
    small.PutData([.. Enumerable.Range(0, cells * cells).Select(_ => (int)(255 * rng.Random()))]);
    var big = new PilImage("L", cells * 3, cells * 3);
    for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
            big.Paste(small, i * cells, j * cells);
    big = big.Resize(size * 3, size * 3, PilImage.Filter.Bicubic).Crop(size, size, 2 * size, 2 * size);
    big = Blur(big, (double)size / cells / 2);
    return big.Point(v => (int)(low + (double)((high - low) * v) / 255));
}

// A ground of base colour, lighter or darker by the noise.
static PilImage Tinted(int[] @base, int[] light, int[] dark, PilImage noise)
{
    PilImage Plain(int[] c) => new("RGB", noise.Width, noise.Height, (byte)c[0], (byte)c[1], (byte)c[2]);
    PilImage img = Plain(@base), lit = Plain(light), deep = Plain(dark);
    img = PilImage.Composite(lit, img, noise.Point(v => Math.Max(0, v - 128) * 2));
    return PilImage.Composite(deep, img, noise.Point(v => Math.Max(0, 128 - v) * 2)).Convert("RGBA");
}

static Pts Blob(double cx, double cy, double r, PyRandom rng, int corners = 9, double rough = 0.25, double squash = 1.0, double turn = 0.0)
{
    var pts = new Pts();
    for (int k = 0; k < corners; k++)
    {
        double a = 2 * Math.PI * k / corners + rng.Uniform(-0.2, 0.2);
        double d = r * (1 + rng.Uniform(-rough, rough));
        double x = Libm.cos(a) * d, y = Libm.sin(a) * d * squash;
        pts.Add((cx + x * Libm.cos(turn) - y * Libm.sin(turn), cy + x * Libm.sin(turn) + y * Libm.cos(turn)));
    }
    return pts;
}

// Draws (points relative to x, y, fill) shapes in order, wrapped.
static void Polys(PilDraw d, int size, double x, double y, double reach, IReadOnlyList<(Pts Points, byte[] Fill)> shapes)
{
    Wrap((sx, sy) =>
    {
        foreach (var (pts, fill) in shapes)
            d.Polygon([.. pts.Select(p => (sx + p.X, sy + p.Y))], fill);
    }, size, x, y, reach);
}

// A tapered brush stroke along +x, turned; bend curves it sideways.
static Pts Stroke(double length, double width, double turn, double bend = 0.0)
{
    var pts = new Pts();
    for (int k = 0; k < 9; k++)
    {
        double t = k / 8.0;
        pts.Add((t * length, width * 0.5 * Py.Pow(Libm.sin(Math.PI * t), 0.6) + bend * length * Libm.sin(Math.PI * t)));
    }
    for (int k = 8; k > -1; k--)
    {
        double t = k / 8.0;
        pts.Add((t * length, -width * 0.5 * Py.Pow(Libm.sin(Math.PI * t), 0.6) + bend * length * Libm.sin(Math.PI * t)));
    }
    double c = Libm.cos(turn), s = Libm.sin(turn);
    return [.. pts.Select(p => (p.X * c - p.Y * s, p.X * s + p.Y * c))];
}

// Visible brush strokes in close tones, like the painted figures.
// With relief, every stroke is drawn again into its mask, in the ink it gives for the colour (Up, Down).
static void Brush(PilImage img, PyRandom rng, int count, int[][] colours, (double, double) length, (double, double) width,
    double turn, double jitter, double bend = 0.1, (PilDraw Draw, Func<int[], byte[]> Ink) relief = default)
{
    int size = img.Width;
    var (lay, d) = Layer(size);
    for (int i = 0; i < count; i++)
    {
        double x = rng.Uniform(0, size), y = rng.Uniform(0, size);
        double ln = rng.Uniform(length.Item1, length.Item2) * K;
        double w = rng.Uniform(width.Item1, width.Item2) * K;
        double tn = turn + rng.Uniform(-jitter, jitter);
        Pts pts = Stroke(ln, w, tn, rng.Uniform(-bend, bend));
        int[] colour = rng.Choice(colours);
        Polys(d, size, x - ln / 2, y, ln * 1.2, [(pts, Ink(colour))]);
        if (relief.Draw != null)
            Polys(relief.Draw, size, x - ln / 2, y, ln * 1.2, [(pts, relief.Ink(colour))]);
    }
    img.AlphaComposite(Blur(lay, Edge));
}

// Painted stones (x, y, radius, colour, squash, corners): dark rim, body, a hard-edged
// light plane top left; drawn in order.
static void Stones(PilImage img, PyRandom rng, List<(double X, double Y, double R, int[] Colour, double Squash, int Corners)> specs,
    double rimDark = 0.6, int lightAlpha = 200)
{
    int size = img.Width;
    var (lay, d) = Layer(size);
    foreach (var (cx, cy, r, colour, squash, corners) in specs)
    {
        double turn = squash < 0.75 ? rng.Uniform(-0.3, 0.3) : rng.Uniform(0, Math.PI);
        Pts outline = Blob(0, 0, r, rng, corners, 0.22, squash, turn);
        Pts inner = [.. outline.Select(p => (p.X * 0.88 - r * 0.03, p.Y * 0.88 - r * 0.03))];
        Pts plane = [.. outline.Select(p => (p.X * 0.55 - r * 0.18, p.Y * 0.5 - r * 0.2 * squash))];
        Polys(d, size, cx, cy, r * 1.5, [(outline, Ink(Shade(colour, rimDark), 255)), (inner, Ink(colour, 255)),
            (plane, Ink(Shade(colour, 1.18), lightAlpha))]);
    }
    img.AlphaComposite(Blur(lay, Edge));
}

static void Specks(PilImage img, PyRandom rng, int count, int[][] colours, (double, double) radius,
    (PilDraw Draw, Func<int[], byte[]> Ink) relief = default)
{
    int size = img.Width;
    var (lay, d) = Layer(size);
    for (int i = 0; i < count; i++)
    {
        double x = rng.Uniform(0, size), y = rng.Uniform(0, size);
        double r = rng.Uniform(radius.Item1, radius.Item2) * K;
        int[] colour = rng.Choice(colours);
        byte[] c = Ink(colour);
        Wrap((sx, sy) => d.Ellipse(sx - r, sy - r, sx + r, sy + r, c), size, x, y, r + 2);
        if (relief.Draw != null)
        {
            byte[] bump = relief.Ink(colour);
            Wrap((sx, sy) => relief.Draw.Ellipse(sx - r, sy - r, sx + r, sy + r, bump), size, x, y, r + 2);
        }
    }
    img.AlphaComposite(Blur(lay, Edge));
}

// A slim curved blade of grass from (root, 0) upwards, tapering to a tip.
static Pts Blade(double length, double width, double lean, double bend, double root)
{
    Pts left = [], right = [];
    for (int k = 0; k < 9; k++)
    {
        double t = k / 8.0;
        double x = root + lean * length * t + bend * length * t * t;
        double y = -length * t;
        double w = width * 0.5 * Py.Pow(1 - t, 0.8);
        left.Add((x - w, y));
        right.Add((x + w, y));
    }
    right.Reverse();
    return [.. left, .. right];
}

static (PilImage, double[]) Grass(PyRandom rng)
{
    PilImage noise = Clouds(rng, Size, 6, 70, 190);
    PilImage img = Tinted([96, 168, 62], [124, 194, 76], [66, 132, 48], noise);
    // Relief from the very shapes of the colour: the clouds as gentle swells, every clump a soft
    // mound (the lighter ones higher), every painted blade a slim ridge, the flowers small buttons.
    double[] relief = Swells(noise, 130, 60, 0.008);
    var (bumps, bd) = Layer(Size);
    // Soft clumps, darker and lighter, so the meadow reads from afar.
    var (lay, d) = Layer(Size);
    for (int i = 0; i < 70; i++)
    {
        double r = rng.Uniform(40, 90) * K;
        int[] c = rng.Choice(new[] { new[] { 70, 138, 48, 120 }, [128, 192, 78, 110] });
        double x = rng.Uniform(0, Size), y = rng.Uniform(0, Size);
        double squash = rng.Uniform(0.6, 1.0), turn = rng.Uniform(0, Math.PI);
        Pts blob = Blob(0, 0, r, rng, 12, 0.3, squash, turn);
        Polys(d, Size, x, y, r * 1.4, [(blob, Ink(c))]);
        Polys(bd, Size, x, y, r * 1.4, [(blob, Up((c[0] == 70 ? 0.6 : 1.0) * Vary(i, 1, 0.65, 1.0)))]);
    }
    img.AlphaComposite(Blur(lay, 8 * K));
    AddRelief(relief, bumps, 0.014, 10 * K);
    (bumps, bd) = Layer(Size);
    int tuft = 0;
    // Tufts like in a comic: a few slim, curved blades from one root, close to the
    // ground's tone (a shade darker, or lighter towards the tips).
    foreach (var (count, colours) in new[] { (130, new[] { new[] { 80, 148, 52 }, [86, 154, 54] }), (130, [[132, 198, 80], [146, 208, 92]]) })
    {
        (lay, d) = Layer(Size);
        for (int i = 0; i < count; i++)
        {
            byte[] c = Ink(rng.Choice(colours), 255);
            var shapes = new List<(Pts, byte[])>();
            int blades = rng.RandInt(3, 5);
            for (int b = 0; b < blades; b++)
            {
                double length = rng.Uniform(26, 46) * K;
                double lean = rng.Uniform(-0.55, 0.55) + (b - 2) * 0.18;
                double width = rng.Uniform(3.0, 4.5) * K;
                shapes.Add((Blade(length, width, lean, rng.Uniform(-0.25, 0.25), (b - 2) * 3 * K), c));
            }
            double x = rng.Uniform(0, Size), y = rng.Uniform(0, Size);
            Polys(d, Size, x, y, 50 * K, shapes);
            byte[] ridge = Up(Vary(tuft++, 2, 0.5, 1.0));
            Polys(bd, Size, x, y, 50 * K, [.. shapes.Select(s => (s.Item1, ridge))]);
        }
        img.AlphaComposite(Blur(lay, Edge));
    }
    AddRelief(relief, bumps, 0.004, 1.2 * K);
    (bumps, bd) = Layer(Size);
    // A few small flowers, white and yellow like the spots on the caps.
    (lay, d) = Layer(Size);
    for (int i = 0; i < 10; i++)
    {
        int[] petal = rng.Choice(new[] { new[] { 250, 248, 240 }, [255, 226, 92] });
        double r = rng.Uniform(3, 5) * K;
        void Draw(double fx, double fy)
        {
            for (int k = 0; k < 5; k++)
            {
                double a = 2 * Math.PI * k / 5;
                double px = fx + Libm.cos(a) * r, py = fy + Libm.sin(a) * r;
                d.Ellipse(px - r * 0.75, py - r * 0.75, px + r * 0.75, py + r * 0.75, Ink(Shade(petal, 0.8), 255));
                d.Ellipse(px - r * 0.55, py - r * 0.6, px + r * 0.5, py + r * 0.5, Ink(petal, 255));
            }
            d.Ellipse(fx - r * 0.5, fy - r * 0.5, fx + r * 0.5, fy + r * 0.5, [236, 160, 40, 255]);
            bd.Ellipse(fx - r * 1.4, fy - r * 1.4, fx + r * 1.4, fy + r * 1.4, Up(Vary(i, 3, 0.6, 1.0)));
        }
        double x = rng.Uniform(0, Size), y = rng.Uniform(0, Size);
        Wrap(Draw, Size, x, y, 12 * K);
    }
    img.AlphaComposite(Blur(lay, Edge));
    AddRelief(relief, bumps, 0.003, 1.5 * K);
    // Not only what is painted: a few soft lumps of their own, then all of it bent a little off the paint.
    for (int k = 0; k < 40; k++)
    {
        double s = Vary(k, 23, 20, 40) * K, h = Vary(k, 24, 0.003, 0.007);
        Stamp(relief, Vary(k, 25, 0, Size), Vary(k, 26, 0, Size), 3 * s, (dx, dy) => h * Math.Exp(-(dx * dx + dy * dy) / (2 * s * s)));
    }
    return (img.Convert("RGB"), Warp(relief, 4 * K, 31));
}

static PilImage Dirt(PyRandom rng)
{
    PilImage img = Tinted([132, 88, 52], [152, 104, 60], [104, 68, 40], Clouds(rng, Size, 5, 60, 200));
    // Clods with a dark lower rim and a light top, crisp like painted earth.
    var (lay, d) = Layer(Size);
    for (int i = 0; i < 160; i++)
    {
        double r = rng.Uniform(12, 40) * K;
        int[] c = rng.Choice(new[] { new[] { 158, 110, 64 }, [112, 72, 42], [144, 96, 56], [126, 84, 48] });
        double squash = rng.Uniform(0.55, 0.9), turn = rng.Uniform(-0.4, 0.4);
        Pts pts = Blob(0, 0, r, rng, 10, 0.3, squash, turn);
        double x = rng.Uniform(0, Size), y = rng.Uniform(0, Size);
        Polys(d, Size, x, y, r * 1.4,
            [([.. pts.Select(p => (p.X + r * 0.06, p.Y + r * 0.1))], Ink(Shade(c, 0.7), 230)),
             (pts, Ink(c, 240)),
             ([.. pts.Select(p => (p.X * 0.6 - r * 0.15, p.Y * 0.45 - r * 0.25))], Ink(Shade(c, 1.15), 200))]);
    }
    img.AlphaComposite(Blur(lay, Edge));
    Brush(img, rng, 90, [[164, 116, 70, 100], [100, 64, 36, 100]], (30, 70), (8, 16), 0.0, 0.5);
    (lay, d) = Layer(Size);
    for (int i = 0; i < 7; i++)
    {
        double x = rng.Uniform(0, Size), y = rng.Uniform(0, Size);
        double a = rng.Uniform(0, 2 * Math.PI);
        var pts = new Pts();
        for (int k = 0; k < 12; k++)
        {
            pts.Add((x, y));
            a += rng.Uniform(-0.5, 0.5);
            (x, y) = (x + Libm.cos(a) * 9 * K, y + Libm.sin(a) * 9 * K);
        }
        var (ox, oy) = pts[0];
        Wrap((sx, sy) =>
        {
            d.Line([.. pts.Select(p => (sx - ox + p.X, sy - oy + p.Y + 2 * K))], [70, 42, 24, 240], (int)(5 * K));
            d.Line([.. pts.Select(p => (sx - ox + p.X, sy - oy + p.Y))], [124, 82, 50, 255], (int)(3 * K));
        }, Size, ox, oy, 120 * K);
    }
    img.AlphaComposite(Blur(lay, Edge));
    var specs = new List<(double, double, double, int[], double, int)>();
    for (int i = 0; i < 40; i++)
    {
        int[] c = rng.Choice(new[] { new[] { 150, 132, 112 }, [170, 150, 124], [128, 100, 76] });
        double x = rng.Uniform(0, Size), y = rng.Uniform(0, Size), r = rng.Uniform(9, 20) * K;
        specs.Add((x, y, r, c, rng.Uniform(0.6, 0.9), 8));
    }
    Stones(img, rng, specs, 0.55);
    return img.Convert("RGB");
}

static (PilImage, double[]) Sand(PyRandom rng)
{
    PilImage noise = Clouds(rng, Size, 6, 80, 180);
    PilImage img = Tinted([238, 214, 148], [248, 230, 172], [218, 190, 124], noise);
    // Relief from the very shapes of the colour: the clouds as low swells, every ripple a crest
    // that falls off steeply to its trough (the shaded arc below the lit one), the light strokes
    // a little raised and the dark ones sunk, every grain a tiny bump, the shells small domes.
    double[] relief = Swells(noise, 130, 50, 0.005);
    // Ripples as a lit crest over a shaded trough, then grains and a few shells.
    var (lay, d) = Layer(Size);
    for (int i = 0; i < 60; i++)
    {
        double x = rng.Uniform(0, Size), y = rng.Uniform(0, Size);
        double w = rng.Uniform(40, 110) * K;
        Wrap((sx, sy) =>
        {
            d.Arc(sx - w, sy - w * 0.25 + 3 * K, sx + w, sy + w * 0.25 + 3 * K, 200, 340, [212, 182, 118, 150], (int)(4 * K));
            d.Arc(sx - w, sy - w * 0.25, sx + w, sy + w * 0.25, 205, 335, [255, 244, 200, 170], (int)(3 * K));
        }, Size, x, y, 120 * K);
        int ripple = i;
        Stamp(relief, x, y, w + 20 * K, (dx, dy) => Ripple(dx, dy, w, ripple));
    }
    img.AlphaComposite(Blur(lay, Edge));
    var (bumps, bd) = Layer(Size);
    int stroke = 0, grain = 0;
    Brush(img, rng, 70, [[250, 236, 186, 80], [222, 194, 130, 80]], (50, 110), (10, 22), 0.0, 0.3,
        relief: (bd, c => c[0] == 250 ? Up(Vary(stroke++, 4, 0.4, 1.0)) : Down(Vary(stroke++, 4, 0.4, 1.0))));
    AddRelief(relief, bumps, 0.002, 3 * K);
    (bumps, bd) = Layer(Size);
    Specks(img, rng, 1250, [[196, 160, 98, 200], [255, 248, 220, 200], [224, 192, 128, 200], [176, 140, 90, 180]], (1.4, 2.8), (bd, _ => Up(Vary(grain++, 5, 0.3, 1.0))));
    AddRelief(relief, bumps, 0.0012, 0.8 * K);
    var specs = new List<(double, double, double, int[], double, int)>();
    for (int i = 0; i < 6; i++)
    {
        int[] c = rng.Choice(new[] { new[] { 246, 214, 206 }, [236, 236, 228] });
        double x = rng.Uniform(0, Size), y = rng.Uniform(0, Size);
        specs.Add((x, y, rng.Uniform(5, 8) * K, c, 0.7, 6));
    }
    Stones(img, rng, specs, 0.7);
    for (int k = 0; k < specs.Count; k++)
    {
        var (x, y, r, _, squash, _) = specs[k];
        double dome = 0.003 * Vary(k, 6, 0.7, 1.3);
        Stamp(relief, x, y, r, (dx, dy) => dome * Math.Max(0, 1 - (dx * dx + dy * dy / (squash * squash)) / (r * r)));
    }
    // Not only what is painted: some fainter ripples of their own, then all of it bent a little off the paint.
    for (int k = 0; k < 30; k++)
    {
        double w = Vary(k, 20, 40, 110) * K;
        int ripple = 1000 + k;
        Stamp(relief, Vary(k, 21, 0, Size), Vary(k, 22, 0, Size), w + 20 * K, (dx, dy) => 0.5 * Ripple(dx, dy, w, ripple));
    }
    return (img.Convert("RGB"), Warp(relief, 5 * K, 30));
}

static PilImage Snow(PyRandom rng)
{
    // Calm, but painted: big drifts as crisp planes in close tones (pale blue shadow
    // below, white crest above), long soft strokes, a few sparkles.
    PilImage img = Tinted([222, 232, 246], [238, 244, 252], [204, 218, 240], Clouds(rng, Size, 4, 90, 170));
    var (lay, d) = Layer(Size);
    for (int i = 0; i < 16; i++)
    {
        double x = rng.Uniform(0, Size), y = rng.Uniform(0, Size);
        double w = rng.Uniform(140, 260) * K, h = rng.Uniform(50, 100) * K;
        Pts under = Blob(0, h * 0.25, w, rng, 14, 0.12, h / w, rng.Uniform(-0.15, 0.15));
        Pts crest = [.. under.Select(p => (p.X * 0.9, p.Y * 0.75 - h * 0.25))];
        Polys(d, Size, x, y, w * 1.3, [(under, [206, 222, 244, 150]), (crest, [246, 249, 255, 190])]);
    }
    img.AlphaComposite(Blur(lay, 1.5 * K));
    Brush(img, rng, 45, [[216, 230, 248, 90], [255, 255, 255, 110]], (120, 260), (16, 34), 0.0, 0.2, 0.05);
    Specks(img, rng, 150, [[255, 255, 255, 255], [198, 216, 244, 200]], (1.4, 2.6));
    return img.Convert("RGB");
}

static PilImage Rock(PyRandom rng)
{
    // The columns already make the blocks: the rock shows big painted planes (about 2 m)
    // in close tones, layers, a few long cracks with a lit lip, strokes, chips and lichen spots.
    PilImage img = Tinted([160, 148, 134], [180, 168, 152], [134, 122, 110], Clouds(rng, Size, 3, 60, 200));
    int[] from = [158, 146, 132], to = [176, 164, 148];
    var specs = new List<(double, double, double, int[], double, int)>();
    for (int j = 0; j < 2; j++)
        for (int i = 0; i < 2; i++)
        {
            double x = (i + 0.5 + (j % 2 != 0 ? 0.5 : 0)) * Size / 2 + rng.Uniform(-80, 80) * K;
            double y = (j + 0.5) * Size / 2 + rng.Uniform(-80, 80) * K;
            int[] mixed = Mix(from, to, rng.Random());
            int[] c = Shade(mixed, rng.Uniform(0.92, 1.06));
            double r = rng.Uniform(300, 380) * K;
            specs.Add((x, y, r, c, rng.Uniform(0.7, 0.9), 7));
        }
    for (int i = 0; i < 5; i++)
    {
        int[] mixed = Mix(from, to, rng.Random());
        int[] c = Shade(mixed, rng.Uniform(0.92, 1.06));
        double x = rng.Uniform(0, Size), y = rng.Uniform(0, Size), r = rng.Uniform(100, 180) * K;
        specs.Add((x, y, r, c, rng.Uniform(0.6, 0.85), 6));
    }
    Stones(img, rng, specs, 0.86, 120);
    var (lay, d) = Layer(Size);
    double layerY = 0.0;
    while (layerY < Size)
    {
        double h = rng.Uniform(120, 280) * K;
        byte[] tone = Ink(rng.Choice(new[] { new[] { 186, 174, 158, 60 }, [128, 116, 104, 60], [170, 158, 142, 50] }));
        int step = (int)(128 * K);
        var pts = new Pts();
        for (int x = 0; x < Size + step; x += step)
            pts.Add((x, layerY + rng.Uniform(-16, 16) * K));
        for (int x = Size; x > -step; x -= step)
            pts.Add((x, layerY + h + rng.Uniform(-16, 16) * K));
        d.Polygon(pts, tone);
        Pts top = pts.GetRange(0, Size / step + 1);
        d.Line([.. top.Select(p => (p.X, p.Y + 3 * K))], [112, 100, 90, 110], (int)(3 * K));
        d.Line(top, [214, 204, 188, 140], (int)(3 * K));
        layerY += h;
    }
    img.AlphaComposite(Blur(lay, Edge));
    Brush(img, rng, 110, [[184, 172, 156, 100], [138, 126, 114, 100], [170, 158, 142, 100]], (60, 150), (14, 28), -1.45, 0.35);
    (lay, d) = Layer(Size);
    for (int i = 0; i < 5; i++)
    {
        double x = rng.Uniform(0, Size), y = rng.Uniform(0, Size);
        double a = rng.Uniform(-1.9, -1.2);
        var pts = new Pts();
        int corners = rng.RandInt(5, 10);
        for (int k = 0; k < corners; k++)
        {
            pts.Add((x, y));
            a += rng.Uniform(-0.6, 0.6);
            (x, y) = (x + Libm.cos(a) * 30 * K, y + Libm.sin(a) * 30 * K);
        }
        var (ox, oy) = pts[0];
        Wrap((sx, sy) =>
        {
            d.Line([.. pts.Select(p => (sx - ox + p.X + 4 * K, sy - oy + p.Y + 3 * K))], [214, 204, 188, 190], (int)(6 * K));
            d.Line([.. pts.Select(p => (sx - ox + p.X, sy - oy + p.Y))], [84, 74, 66, 240], (int)(7 * K));
        }, Size, ox, oy, 320 * K);
    }
    img.AlphaComposite(Blur(lay, Edge));
    specs = [];
    for (int i = 0; i < 12; i++)
    {
        int[] c = rng.Choice(new[] { new[] { 132, 122, 110 }, [196, 186, 170], [170, 160, 146] });
        double x = rng.Uniform(0, Size), y = rng.Uniform(0, Size);
        specs.Add((x, y, rng.Uniform(8, 20) * K, c, 0.75, 7));
    }
    Stones(img, rng, specs, 0.62);
    // The lichen is laid on by TerrainTriplanar, so it never stretches.
    return img.Convert("RGB");
}

// Four single leaves in a 2 x 2 atlas, one per quarter, lying along x. LeafSplat places
// every leaf on the crown and cuts its outline itself (LeafWidth, the same curve), so it
// only takes the colours from here.
static PilImage Leaves(PyRandom rng)
{
    int size = Small, quarter = size / 2;
    // The ground between is clear, its colour the dark rim's, so filtering leaves no bright fringe.
    var img = new PilImage("RGBA", size, size, 40, 96, 36, 0);
    var (lay, d) = Layer(size);
    double length = quarter * LeafFill, width = length * LeafWidth;
    var half = new Pts();
    for (int k = 0; k < 49; k++)
    {
        double t = k / 48.0;
        half.Add((t * length, width * 0.5 * Py.Pow(Libm.sin(Math.PI * t), 0.8)));
    }
    Pts outline = [.. half, .. Enumerable.Reverse(half).Select(p => (p.X, -p.Y))];
    // Like the leaves round the mushroom's collar: dark rim, body with a lit half, a dark
    // middle vein and side veins.
    for (int n = 0; n < 4; n++)
    {
        double ox = (n % 2) * quarter + (quarter - length) / 2;
        double oy = (n / 2) * quarter + quarter / 2.0;
        int[] body = Mix([58, 128, 46], [110, 178, 66], n / 3.0 * 0.8 + rng.Random() * 0.2);
        int[] vein = Shade(body, 0.62);
        (double, double) At(double px, double py) => (ox + px, oy + py);
        d.Polygon([.. outline.Select(p => At(p.X, p.Y))], Ink(Shade(body, 0.55), 255));
        d.Polygon([.. outline.Select(p => At(p.X * 0.94 + length * 0.03, p.Y * 0.82))], Ink(body, 255));
        d.Polygon([.. half.Select(p => At(p.X * 0.94 + length * 0.03, p.Y * 0.82)), At(length * 0.97, 0)], Ink(Shade(body, 1.18), 255));
        d.Line([At(length * 0.06, 0), At(length * 0.9, 0)], Ink(vein, 255), Math.Max(1, (int)(6 * K)));
        for (int k = 1; k < 4; k++)
        {
            double s = length * (0.12 + 0.2 * k);
            foreach (int side in new[] { 1, -1 })
                d.Line([At(s, 0), At(s + length * 0.1, side * width * 0.24)], Ink(vein, 220), Math.Max(1, (int)(4 * K)));
        }
    }
    img.AlphaComposite(Blur(lay, Edge * 3));
    return img;
}

// A twig with leaves for the cards that stand out of the crowns (Luca, after the "bushy
// leaves" resource packs): base at the bottom middle, tip at the top, leaves alternating
// and smaller to the tip; painted like Leaves().
static PilImage Sprig(PyRandom rng)
{
    int size = Small;
    var img = new PilImage("RGBA", size, size, 40, 96, 36, 0);
    var (lay, d) = Layer(size);
    Pts twig = [.. Enumerable.Range(0, 25).Select(k => k / 24.0).Select(t => (size * (0.5 + 0.06 * Libm.sin(3.0 * t)), size * (0.96 - 0.78 * t)))];
    for (int k = 0; k < 24; k++)
    {
        int w = Math.Max(2, (int)((16 - 10 * k / 24.0) * K));
        d.Line([twig[k], twig[k + 1]], [92, 64, 36, 255], w);
    }
    void Leaf(double x, double y, double angle, double length, int[] body)
    {
        double width = length * LeafWidth;
        Pts half = [.. Enumerable.Range(0, 33).Select(k => k / 32.0).Select(t => (t * length, width * 0.5 * Py.Pow(Libm.sin(Math.PI * t), 0.8)))];
        Pts outline = [.. half, .. Enumerable.Reverse(half).Select(p => (p.X, -p.Y))];
        double ca = Libm.cos(angle), sa = Libm.sin(angle);
        (double, double) At(double px, double py) => (x + px * ca - py * sa, y + px * sa + py * ca);
        d.Polygon([.. outline.Select(p => At(p.X, p.Y))], Ink(Shade(body, 0.55), 255));
        d.Polygon([.. outline.Select(p => At(p.X * 0.94 + length * 0.03, p.Y * 0.82))], Ink(body, 255));
        d.Polygon([.. half.Select(p => At(p.X * 0.94 + length * 0.03, p.Y * 0.82)), At(length * 0.97, 0)], Ink(Shade(body, 1.18), 255));
        d.Line([At(length * 0.06, 0), At(length * 0.9, 0)], Ink(Shade(body, 0.62), 255), Math.Max(1, (int)(5 * K)));
    }
    const int count = 9;
    for (int k = 0; k < count; k++)
    {
        double t = 0.18 + 0.78 * k / (count - 1);
        var (x, y) = twig[(int)(t * 24)];
        int side = k % 2 != 0 ? 1 : -1;
        // Up the twig and out to its side; smaller towards the tip.
        double angle = -Math.PI / 2 + side * rng.Uniform(0.55, 0.95);
        double length = size * (0.3 - 0.12 * t) * rng.Uniform(0.9, 1.1);
        int[] body = Mix([58, 128, 46], [110, 178, 66], rng.Random());
        Leaf(x, y, angle, length, body);
    }
    var (tx, ty) = twig[^1];
    double tipAngle = -Math.PI / 2 + rng.Uniform(-0.2, 0.2);
    Leaf(tx, ty + size * 0.02, tipAngle, size * 0.14, Mix([58, 128, 46], [110, 178, 66], rng.Random()));
    img.AlphaComposite(Blur(lay, Edge * 3));
    return img;
}

static PilImage Bark(PyRandom rng)
{
    int size = Small;
    PilImage img = Tinted([122, 82, 48], [144, 98, 58], [96, 62, 38], Clouds(rng, size, 4, 70, 190));
    // Long vertical strokes and dark grooves with a lit lip, like the painted wood.
    var (lay, d) = Layer(size);
    for (int i = 0; i < 210; i++)
    {
        double x = rng.Uniform(0, size), y = rng.Uniform(0, size);
        double h = rng.Uniform(40, 140) * K, w = rng.Uniform(2, 6) * K;
        int[] c = rng.Choice(new[] { new[] { 160, 112, 68, 170 }, [100, 64, 38, 180], [176, 128, 80, 140] });
        double turn = Math.PI / 2 + rng.Uniform(-0.05, 0.05);
        Polys(d, size, x, y, h * 1.2, [(Stroke(h * 2, w * 2, turn, rng.Uniform(-0.03, 0.03)), Ink(c))]);
    }
    img.AlphaComposite(Blur(lay, Edge));
    (lay, d) = Layer(size);
    for (int i = 0; i < 30; i++)
    {
        double x = rng.Uniform(0, size), y = rng.Uniform(0, size);
        double h = rng.Uniform(60, 180) * K, w = rng.Uniform(2.5, 4.5) * K;
        double turn = Math.PI / 2 + rng.Uniform(-0.04, 0.04);
        Pts groove = Stroke(h, w, turn, rng.Uniform(-0.03, 0.03));
        Polys(d, size, x, y, h * 1.2, [([.. groove.Select(p => (p.X + w * 0.8, p.Y))], [170, 122, 76, 200]), (groove, [62, 38, 22, 240])]);
    }
    img.AlphaComposite(Blur(lay, Edge));
    (lay, d) = Layer(size);
    for (int i = 0; i < 2; i++)
    {
        double x = rng.Uniform(0, size), y = rng.Uniform(0, size);
        Wrap((sx, sy) =>
        {
            d.Ellipse(sx - 13 * K, sy - 19 * K, sx + 13 * K, sy + 19 * K, [62, 38, 22, 255]);
            d.Ellipse(sx - 8 * K, sy - 13 * K, sx + 8 * K, sy + 13 * K, [142, 98, 58, 255]);
            d.Ellipse(sx - 4 * K, sy - 7 * K, sx + 4 * K, sy + 7 * K, [96, 62, 36, 255]);
        }, size, x, y, 30 * K);
    }
    img.AlphaComposite(Blur(lay, Edge));
    return img.Convert("RGB");
}

// Relief of a ground texture: height in metres per pixel (Size px are 4 m), tiling.
// Shapes are stamped wrapped round the edges; NormalMap turns it into the normal map.
static void Stamp(double[] relief, double x, double y, double reach, Func<double, double, double> height)
{
    for (int py = (int)Math.Floor(y - reach); py <= (int)Math.Ceiling(y + reach); py++)
    {
        int row = (py % Size + Size) % Size * Size;
        for (int px = (int)Math.Floor(x - reach); px <= (int)Math.Ceiling(x + reach); px++)
            relief[row + (px % Size + Size) % Size] += height(px + 0.5 - x, py + 0.5 - y);
    }
}

// The cloud noise (an L image) as swells: middle is level, ±spread reaches ±metres.
static double[] Swells(PilImage noise, int middle, int spread, double metres)
{
    var relief = new double[Size * Size];
    for (int i = 0; i < relief.Length; i++)
        relief[i] = (noise.Data[i] - middle) / (double)spread * metres;
    return relief;
}

// Inks for a relief mask: red raises, green sinks, by the given share of the full height.
static byte[] Up(double share) => [(byte)Math.Round(255 * Math.Min(1, share)), 0, 0, 255];

static byte[] Down(double share) => [0, (byte)Math.Round(255 * Math.Min(1, share)), 0, 255];

// Adds a mask drawn with Up and Down (an RGBA layer) to the relief, softened by a
// Gaussian of sigma pixels that wraps round the edges like the tiling.
static void AddRelief(double[] relief, PilImage mask, double metres, double sigma)
{
    var share = new double[Size * Size];
    for (int i = 0; i < share.Length; i++)
        share[i] = (mask.Data[4 * i] - mask.Data[4 * i + 1]) / 255.0;
    int reach = (int)Math.Ceiling(3 * sigma);
    double[] kernel = [.. Enumerable.Range(-reach, 2 * reach + 1).Select(k => Math.Exp(-k * k / (2 * sigma * sigma)))];
    double sum = kernel.Sum();
    var across = new double[Size * Size];
    for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            double v = 0;
            for (int k = -reach; k <= reach; k++)
                v += kernel[k + reach] * share[y * Size + ((x + k) % Size + Size) % Size];
            across[y * Size + x] = v / sum;
        }
    for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            double v = 0;
            for (int k = -reach; k <= reach; k++)
                v += kernel[k + reach] * across[((y + k) % Size + Size) % Size * Size + x];
            relief[y * Size + x] += metres * v / sum;
        }
}

// A ripple along the lit arc of Sand (half axes w and w/4, its upper side): rising gently
// from above, falling steeply below into a shallow trough; fading out towards its ends.
// Every ripple k its own (Luca: "not so sterile"): height, slopes, a crest that wavers a little
// about the painted line, higher towards one end.
static double Ripple(double dx, double dy, double w, int k)
{
    double t = Math.Abs(dx) / w;
    if (t >= 0.9)
        return 0;
    double waver = Vary(k, 7, 0, 1.5 * K) * Math.Sin(dx / w * Vary(k, 8, 4, 9) + Vary(k, 9, 0, 2 * Math.PI));
    double crest = -0.25 * w * Math.Sqrt(1 - t * t) + waver;
    double d = dy - crest, fade = (1 - Math.Pow(t / 0.9, 4)) * (1 + Vary(k, 10, -0.3, 0.3) * dx / w);
    double windward = Vary(k, 11, 5, 9) * K, lee = Vary(k, 12, 2.2, 3.8) * K;
    double rise = d < 0 ? Math.Exp(-d * d / (2 * windward * windward)) : Math.Exp(-d * d / (2 * lee * lee));
    double trough = Math.Exp(-(d - 5 * K) * (d - 5 * K) / (2 * 3 * K * 3 * K));
    return fade * Vary(k, 13, 0.6, 1.4) * (0.008 * rise - 0.002 * trough);
}

// The relief bent by a soft displacement of up to about reach pixels (sines that fit the
// tile a whole number of times, so it still tiles): its shapes no longer sit exactly on the paint.
static double[] Warp(double[] relief, double reach, int salt)
{
    var waves = Enumerable.Range(0, 6).Select(k => (Fx: (int)Vary(k, salt, 2, 7), Fy: (int)Vary(k, salt + 1, -6, 7),
        Phase: Vary(k, salt + 2, 0, 2 * Math.PI), Size: reach / 3 * Vary(k, salt + 3, 0.6, 1.4))).ToArray();
    var bent = new double[Size * Size];
    for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            double sx = x, sy = y;
            for (int k = 0; k < waves.Length; k++)
            {
                double a = 2 * Math.PI * (waves[k].Fx * x + waves[k].Fy * y) / Size + waves[k].Phase;
                if (k % 2 == 0)
                    sx += waves[k].Size * Math.Sin(a);
                else
                    sy += waves[k].Size * Math.Sin(a);
            }
            int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
            double tx = sx - x0, ty = sy - y0;
            double At(int px, int py) => relief[(py % Size + Size) % Size * Size + (px % Size + Size) % Size];
            bent[y * Size + x] = (At(x0, y0) * (1 - tx) + At(x0 + 1, y0) * tx) * (1 - ty) + (At(x0, y0 + 1) * (1 - tx) + At(x0 + 1, y0 + 1) * tx) * ty;
        }
    return bent;
}

// A fixed draw in [low, high) for shape k and a purpose (salt): varies the relief shape by
// shape without touching the painter's random numbers, so the colour stays as it was.
static double Vary(int k, int salt, double low, double high)
{
    uint h = (uint)k * 2654435761u ^ (uint)salt * 2246822519u;
    h ^= h >> 15;
    h *= 0x2c1b3c6du;
    h ^= h >> 12;
    h *= 0x297a2d39u;
    h ^= h >> 15;
    return low + (high - low) * (h / 4294967296.0);
}

// The normal map of a relief: x along the texture's u, y along v (down the rows), z up; 0.5 is level.
// Written at half the size: the relief is soft, and the ground reads it on every pixel.
static PilImage NormalMap(double[] relief)
{
    var img = new PilImage("RGB", Size, Size, 128, 128, 255);
    double perPixel = Size / 4.0 / 2;   // metres per pixel inverted, over the two-pixel central difference
    for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            double gx = (relief[y * Size + (x + 1) % Size] - relief[y * Size + (x + Size - 1) % Size]) * perPixel;
            double gy = (relief[(y + 1) % Size * Size + x] - relief[(y + Size - 1) % Size * Size + x]) * perPixel;
            double n = Math.Sqrt(gx * gx + gy * gy + 1);
            int p = 4 * (y * Size + x);
            img.Data[p] = (byte)Math.Round((-gx / n * 0.5 + 0.5) * 255);
            img.Data[p + 1] = (byte)Math.Round((-gy / n * 0.5 + 0.5) * 255);
            img.Data[p + 2] = (byte)Math.Round((1 / n * 0.5 + 0.5) * 255);
        }
    return img.Resize(Size / 2, Size / 2, PilImage.Filter.Bicubic);
}

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path);
