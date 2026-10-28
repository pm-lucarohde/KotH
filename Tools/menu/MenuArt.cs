#!/usr/bin/env -S dotnet run
// Main menu art in the figures' colours: smooth gradient faces, thin dark rim, a solid lip
// underneath, boards in a bamboo frame, a bevelled title; PNGs at twice the design size (1080
// high) with Pad of shadow room to KotH.Game/Menu/Art. Pillow's algorithms (Common/Pil.cs).
//   dotnet run Tools/menu/MenuArt.cs [-- --preview]
#:include ../Common/Libm.cs
#:include ../Common/Py.cs
#:include ../Common/Png.cs
#:include ../Common/Pil.cs
#:include ../Common/PilFont.cs
#:property AllowUnsafeBlocks=true
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using KotH.Tools;
using Pts = System.Collections.Generic.List<(double X, double Y)>;

const int Bold = 5;         // Accuratist has one thin weight: a stroke in pixels fattens the title
const int S = 2;            // pixels per design pixel
const int Pad = 24;         // design pixels round every picture for its shadow
const int Seed = 7;
// Lip heights in design pixels; MainMenuView.axaml and MenuTheme.axaml shift the text up by
// half of them (and down again on a press).
const int ButtonLip = 7, SmallLip = 6, NoteLip = 8, PressedLip = 2;
const double Corner = 0.24; // button corners as a share of the height (Luca: less round than a pill)

// The figure's colours (Mushroom.png).
int[] red = [196, 35, 31], cream = [246, 236, 222], green = [63, 143, 53], brown = [91, 58, 36];
int[] bamboo = [217, 164, 76], yellow = [248, 204, 64], white = [255, 255, 255];

string root = Path.GetFullPath(Path.Combine(Here(), "../.."));
string outDir = Path.Combine(root, "KotH.Game/Menu/Art");
string fontPath = Path.Combine(root, "KotH.Game/Menu/Fonts/Accuratist.otf");

var rng = new PyRandom(Seed);
Directory.CreateDirectory(outDir);
int[] noteFace0 = Mix(green, white, 0.12), noteFace1 = Shade(green, 0.95);
var pieces = new (string Name, Func<PilImage> Paint)[]
{
    ("Panel", () => Framed(440, 450, rng)),
    ("Wide", () => Framed(820, 560, rng)),
    ("Button", () => Button("idle")),
    ("ButtonHot", () => Button("hot")),
    ("ButtonDown", () => Button("down")),
    ("SmallButton", () => Button("idle", 230, 54, SmallLip)),
    ("SmallButtonHot", () => Button("hot", 230, 54, SmallLip)),
    ("SmallButtonDown", () => Button("down", 230, 54, SmallLip)),
    ("Note", () => Board(460, 112, (noteFace0, noteFace1), NoteLip, 22, 28)),
    ("Ribbon", () => Board(340, 58, (noteFace0, noteFace1), ButtonLip, Py.Round(58 * Corner), 34)),
    ("Logo", Logo),
};
var painted = new List<PilImage>();
foreach (var (name, paint) in pieces)
{
    PilImage img = paint();
    img.ToPng().Write(Path.Combine(outDir, name + ".png"));
    Console.WriteLine($"{name}.png {img.Width}x{img.Height}");
    painted.Add(img);
}
if (args.Contains("--preview"))
{
    var sheet = new PilImage("RGBA", 3600, 2600, 120, 170, 220, 255);
    int x = 0, y = 0, row = 0;
    foreach (PilImage img in painted)
    {
        if (x + img.Width > sheet.Width)
            (x, y, row) = (0, y + row, 0);
        sheet.AlphaComposite(img, x, y);
        x += img.Width;
        row = Math.Max(row, img.Height);
    }
    sheet.ToPng().Write(Path.Combine(root, ".work/menu/art_preview.png"));
}

static int[] Mix(int[] a, int[] b, double t) => [.. Enumerable.Range(0, 3).Select(i => Py.Round(a[i] + (b[i] - a[i]) * t))];

static int[] Shade(int[] c, double k) => [.. c.Select(v => Math.Max(0, Math.Min(255, Py.Round(v * k))))];

static byte[] Ink(int[] c, int alpha = 255) => [(byte)c[0], (byte)c[1], (byte)c[2], (byte)alpha];

// A transparent picture of w x h design pixels plus the shadow's pad.
static PilImage Canvas(int w, int h) => new("RGBA", (w + 2 * Pad) * S, (h + 2 * Pad) * S, 0, 0, 0, 0);

static PilImage Mask((int W, int H) size) => new("L", size.W, size.H, 0);

static (int, int) SizeOf(PilImage img) => (img.Width, img.Height);

// An antialiased rounded rectangle as an L mask.
static PilImage Pill((int W, int H) size, (double X0, double Y0, double X1, double Y1) box, double radius)
{
    PilImage m = Mask(size);
    new PilDraw(m).RoundedRectangle(box.X0, box.Y0, box.X1, box.Y1, radius, [255]);
    return m.GaussianBlur(0.6 * S);
}

// Opaque vertical gradient from top (at y0) to bottom (at y1).
static PilImage Gradient((int W, int H) size, double y0, double y1, int[] top, int[] bottom)
{
    var g = new PilImage("RGBA", size.W, size.H);
    var d = new PilDraw(g);
    for (int y = 0; y < size.H; y++)
    {
        double t = Py.Min(1.0, Py.Max(0.0, (y - y0) / Py.Max(1, y1 - y0)));
        d.Line([(0, y), (size.W, y)], Ink(Mix(top, bottom, t)), 1);
    }
    return g;
}

// Composites layer onto img through mask.
static void Put(PilImage img, PilImage layer, PilImage mask)
{
    layer = layer.Copy();
    layer.PutAlpha(PilImage.Multiply(layer.GetChannel(3), mask));
    img.AlphaComposite(layer);
}

static PilImage Flat((int W, int H) size, int[] colour, int alpha = 255) =>
    new("RGBA", size.W, size.H, (byte)colour[0], (byte)colour[1], (byte)colour[2], (byte)alpha);

// A wide soft shadow plus a tight contact shadow under the picture.
static PilImage Shadow(PilImage img, PilImage mask, int dy = 6, double radius = 6, double strength = 95)
{
    var output = new PilImage("RGBA", mask.Width, mask.Height, 0, 0, 0, 0);
    foreach (var (off, blur, k) in new[] { (dy, radius, strength), (Py.FloorDiv(dy, 3), radius / 4, strength * 0.6) })
    {
        PilImage sh = Mask(SizeOf(mask));
        sh.Paste(mask, 0, off * S);
        sh = sh.GaussianBlur(blur * S).Point(v => (int)(v * k / 255));
        var lay = new PilImage("RGBA", mask.Width, mask.Height, 40, 26, 16, 0);
        lay.PutAlpha(sh);
        output.AlphaComposite(lay);
    }
    output.AlphaComposite(img);
    return output;
}

// A board or button of w x h design pixels: a face (top and bottom colour of its gradient) on
// a lip of `lip` pixels. sunk presses the face down into the lip. gloss lays a soft sheen
// over the face's upper half.
(PilImage, PilImage) Slab(int w, int h, (int[] Top, int[] Bottom) face, int lip, int radius, int sunk = 0, int gloss = 0)
{
    PilImage img = Canvas(w, h);
    int p = Pad * S;
    var size = SizeOf(img);
    int x0 = p, x1 = p + w * S;
    int fy0 = p + sunk * S;
    int fy1 = fy0 + (h - lip) * S;
    int r = radius * S;
    PilImage under = Pill(size, (x0, p + lip * S, x1, p + h * S), r);
    int[] near = Mix(face.Bottom, brown, 0.22), far = Mix(face.Bottom, brown, 0.38);
    Put(img, Gradient(size, fy1 - r, p + h * S, near, far), under);
    PilImage top = Pill(size, (x0, fy0, x1, fy1), r);
    // The lip's upper part stays visible beside the face's round corners.
    PilImage side = Pill(size, (x0, fy0 + r, x1, p + h * S - r / 2), r);
    Put(img, Gradient(size, fy0, p + h * S, near, Mix(near, far, 0.6)), PilImage.Subtract(side, top));
    Put(img, Gradient(size, fy0, fy1, face.Top, face.Bottom), top);
    // A thin darker rim and a lighter top edge.
    PilImage inner = top.MinFilter(2 * S + 1).GaussianBlur(0.6 * S);
    Put(img, Flat(size, Shade(face.Bottom, 0.86)), PilImage.Subtract(top, inner));
    PilImage edge = PilImage.Subtract(inner, inner.Offset(0, (int)(2.5 * S)));
    Put(img, Flat(size, white, 170), edge.GaussianBlur(0.5 * S));
    if (gloss != 0)
    {
        PilImage sheen = Mask(size);
        new PilDraw(sheen).RoundedRectangle(x0 + 6 * S, fy0 + 4 * S, x1 - 6 * S, fy0 + (fy1 - fy0) * 0.48, r * 0.8, [(byte)gloss]);
        Put(img, Flat(size, white), PilImage.Multiply(sheen.GaussianBlur(2 * S), inner));
    }
    return (img, PilImage.Lighter(top, under));
}

PilImage Board(int w, int h, (int[], int[]) face, int lip, int radius, int gloss = 0)
{
    var (pic, mask) = Slab(w, h, face, lip, radius, gloss: gloss);
    return Shadow(pic, mask, 7, 6, 100);
}

// Idle cream, under the pointer yellow, pressed yellow and sunk in.
PilImage Button(string state, int w = 380, int h = 66, int lip = ButtonLip)
{
    PilImage pic, mask;
    if (state == "idle")
        (pic, mask) = Slab(w, h, (Mix(cream, white, 0.8), Mix(cream, white, 0.15)), lip, Py.Round(h * Corner), gloss: 70);
    else
        (pic, mask) = Slab(w, h, (Mix(yellow, white, 0.35), Shade(yellow, 0.97)), lip, Py.Round(h * Corner),
            state == "down" ? lip - PressedLip : 0, 80);
    return Shadow(pic, mask, state != "down" ? 4 : 2, 4, 80);
}

// A bamboo pole like the figure's cane, lit as a cylinder from above: nodes as darker rings
// with a light edge, now and then a leaf. Returns an RGBA strip of length x thick design
// pixels plus room for the leaves.
(PilImage, int) Pole(int length, int thick, PyRandom random, bool vertical)
{
    int L = length * S, T = thick * S;
    int room = (int)(thick * 1.6 * S);
    (int W, int H) size = (L + 2 * room, T + 2 * room);
    var img = new PilImage("RGBA", size.W, size.H, 0, 0, 0, 0);
    int y0 = room, x0 = room;
    PilImage Profile(double k)
    {
        var col = new PilImage("RGBA", size.W, size.H);
        var d = new PilDraw(col);
        for (int y = 0; y < T; y++)
        {
            double t = (y + 0.5) / T;
            double light = 0.66 + 0.4 * Py.Pow(Libm.sin(Math.PI * t), 0.8) - 0.14 * t;
            int[] c = Mix(Shade(bamboo, light * k), [255, 246, 214], 0.75 * Libm.exp(-Py.Pow((t - 0.3) / 0.08, 2)));
            d.Line([(0, y0 + y), (size.W, y0 + y)], Ink(c), 1);
        }
        return col;
    }
    Put(img, Profile(1.0), Pill(size, (x0, y0, x0 + L, y0 + T), T / 2));
    PilImage dark = Profile(0.74), bright = Profile(1.12);
    var leaves = new PilImage("RGBA", size.W, size.H, 0, 0, 0, 0);
    double x = x0 + random.Uniform(40, 70) * S;
    while (x < x0 + L - 25 * S)
    {
        Put(img, dark, Pill(size, (x - 3.5 * S, y0 - S, x + 3.5 * S, y0 + T + S), 2 * S));
        Put(img, bright, Pill(size, (x + 3.5 * S, y0 + S, x + 5.5 * S, y0 + T - S), S));
        if (random.Random() < 0.4)
        {
            double turn = random.Random() < 0.5 ? -random.Uniform(0.35, 0.6) : Math.PI + random.Uniform(0.35, 0.6);
            Leaf(leaves, x, y0 + T * 0.3, turn, thick * 1.5 * S, thick * 0.55 * S);
        }
        x += random.Uniform(70, 110) * S;
    }
    img.AlphaComposite(leaves);
    if (vertical)
        img = img.Rotate90();
    return (img, room);
}

// A clean leaf: darker edge, lighter towards its tip, a light midrib.
void Leaf(PilImage img, double x, double y, double turn, double length, double width)
{
    var size = SizeOf(img);
    PilImage shape = Mask(size);
    new PilDraw(shape).Polygon([.. LeafShape(length, width, turn).Select(p => (x + p.X, y + p.Y))], [255]);
    shape = shape.GaussianBlur(0.6 * S);
    Put(img, Flat(size, Shade(green, 0.72)), shape);
    PilImage inner = shape.MinFilter(2 * S + 1).GaussianBlur(0.5 * S);
    Put(img, Gradient(size, y - width, y + width, Mix(green, white, 0.2), green), inner);
    double vx = Libm.cos(turn), vy = Libm.sin(turn);
    new PilDraw(img).Line([(x, y), (x + vx * length * 0.8, y + vy * length * 0.8)], Ink(Mix(green, [220, 240, 160], 0.45)), S);
}

// Brown cord wound round a corner where two poles cross.
void Twine(PilImage img, double x, double y)
{
    var d = new PilDraw(img);
    int[] cord = Mix(brown, bamboo, 0.25), light = Mix(brown, [255, 230, 190], 0.4);
    for (int k = 0; k < 3; k++)
    {
        double off = (k - 1) * 4.5 * S;
        (double X, double Y) a = (x - 11 * S + off, y + 11 * S + off);
        (double X, double Y) b = (x + 11 * S + off, y - 11 * S + off);
        d.Line([a, b], Ink(Shade(cord, 0.75)), (int)(4.5 * S));
        d.Line([(a.X, a.Y - S), (b.X, b.Y - S)], Ink(cord), 3 * S);
        d.Line([(a.X + 2 * S, a.Y - 3 * S), (b.X - 2 * S, b.Y + S)], Ink(light), S);
    }
}

// A clean cream board in a frame of bamboo poles crossing and tied at the corners.
PilImage Framed(int w, int h, PyRandom random)
{
    var (pic, mask) = Slab(w, h, (Mix(cream, white, 0.45), Shade(cream, 0.97)), 0, 26);
    const int thick = 22, over = 14;
    int p = Pad * S;
    var poles = new PilImage("RGBA", pic.Width, pic.Height, 0, 0, 0, 0);
    foreach (bool top in new[] { true, false })
    {
        var (strip, room) = Pole(w + 2 * over, thick, random, false);
        int y = top ? p - thick * S / 2 : p + h * S - thick * S / 2;
        poles.AlphaComposite(strip, p - over * S - room, y - room);
    }
    foreach (bool left in new[] { true, false })
    {
        var (strip, room) = Pole(h + 2 * over, thick, random, true);
        int x = left ? p - thick * S / 2 : p + w * S - thick * S / 2;
        poles.AlphaComposite(strip, x - room, p - over * S - room);
    }
    foreach (var (cx, cy) in new[] { (p, p), (p + w * S, p), (p, p + h * S), (p + w * S, p + h * S) })
        Twine(poles, cx, cy);
    pic.AlphaComposite(poles);
    return Shadow(pic, PilImage.Lighter(mask, poles.GetChannel(3)), 7, 6, 110);
}

// Light and dark masks of a rounded bevel width pixels wide on the mask's edge, lit from the
// top left: the blurred mask is the height, its slope towards the light the shading.
static (PilImage, PilImage) Bevel(PilImage mask, double width, double strength)
{
    PilImage height = mask.GaussianBlur(width).Point(v => Math.Max(0, Math.Min(255, (v - 128) * 2)));
    PilImage slope = height.Kernel3x3([2, 1, 0, 1, 0, -1, 0, -1, -2], 1, 128);
    PilImage lit = slope.Point(v => Math.Max(0, Math.Min(255, (int)((v - 128) * strength))));
    PilImage dark = slope.Point(v => Math.Max(0, Math.Min(255, (int)((128 - v) * strength))));
    return (PilImage.Multiply(lit, mask), PilImage.Multiply(dark, mask));
}

// Stacks the mask down and to the right, depth design pixels deep, far colour at the back,
// near at the front: the letters' solid sides.
static void Extrude(PilImage img, PilImage mask, int depth, int[] near, int[] far)
{
    int n = depth * S;
    for (int k = n; k > 0; k--)
        Put(img, Flat(SizeOf(img), Mix(near, far, (double)k / n)), mask.Offset((int)(k * 0.35), (int)(k * 1.0)));
}

// A tiny fly agaric cap (the i's dot): cream gills under a red dome with dots.
void Cap(PilDraw d, double x, double y, double r)
{
    d.Ellipse(x - r * 1.2, y - r * 0.62, x + r * 1.2, y - r * 0.12, Ink(Mix(cream, brown, 0.3)));
    d.Chord(x - r * 1.3, y - r * 1.25, x + r * 1.3, y + r * 0.45, 180, 360, Ink(Shade(red, 0.72)));
    d.Chord(x - r * 1.18, y - r * 1.12, x + r * 1.18, y + r * 0.3, 180, 360, Ink(red));
    d.Ellipse(x - r * 0.8, y - r * 0.95, x - r * 0.1, y - r * 0.5, Ink(Mix(red, [255, 200, 180], 0.35)));
    foreach (var (dx, dy, rr) in new[] { (-0.6, -0.3, 0.17), (0.05, -0.8, 0.16), (0.62, -0.38, 0.2), (0.0, -0.3, 0.12) })
    {
        double R = r * rr;
        d.Ellipse(x + dx * r - R, y + dy * r - R, x + dx * r + R, y + dy * r + R, Ink(cream));
    }
}

// A pointed leaf along +x, turned by turn radians.
static Pts LeafShape(double length, double width, double turn)
{
    var pts = new Pts();
    for (int k = 0; k < 17; k++)
    {
        double t = k / 16.0;
        pts.Add((t * length, width * 0.5 * Py.Pow(Libm.sin(Math.PI * t), 0.6)));
    }
    for (int k = 16; k > -1; k--)
    {
        double t = k / 16.0;
        pts.Add((t * length, -width * 0.5 * Py.Pow(Libm.sin(Math.PI * t), 0.6)));
    }
    double c = Libm.cos(turn), s = Libm.sin(turn);
    return [.. pts.Select(p => (p.X * c - p.Y * s, p.X * s + p.Y * c))];
}

// "King of the Hill": fat cream letters with a bevelled face, a brown rim and a deep brown
// extrusion, the dots of both i as little caps, "of the" small on a green leaf of its own depth.
PilImage Logo()
{
    const int w = 680, h = 300;
    PilImage img = Canvas(w, h);
    var size = SizeOf(img);
    int p = Pad * S;
    var big = new PilFont(fontPath, 135 * S);
    var small = new PilFont(fontPath, 50 * S);
    (string Word, int X, int Y)[] words = [("Kıng", p + 50 * S, p + 4 * S), ("Hıll", p + 260 * S, p + 144 * S)];

    PilImage text = Mask(size);
    var td = new PilDraw(text);
    foreach (var (word, x, y) in words)
        big.Draw(td, x, y, word, [255], Bold);
    text = text.GaussianBlur(0.5 * S);
    PilImage rim = text.MaxFilter(2 * 6 * S + 1).GaussianBlur(0.7 * S);

    // The leaf behind "Hıll", with "of the" on it.
    int lx = p + 46 * S, ly = p + 204 * S;
    PilImage leaf = Mask(size);
    new PilDraw(leaf).Polygon([.. LeafShape(196 * S, 64 * S, -0.08).Select(q => (lx + q.X, ly + q.Y))], [255]);
    leaf = leaf.GaussianBlur(0.7 * S);
    Extrude(img, leaf, 7, Shade(green, 0.62), Shade(green, 0.42));
    Put(img, Gradient(size, ly - 30 * S, ly + 30 * S, Mix(green, white, 0.18), Shade(green, 0.92)), leaf);
    var (lit, dark) = Bevel(leaf, 3 * S, 2.0);
    Put(img, Flat(size, white, 150), lit);
    Put(img, Flat(size, Shade(green, 0.5), 150), dark);
    var d = new PilDraw(img);
    int tx = lx + 42 * S, ty = ly - 31 * S;
    small.Draw(d, tx + S, ty + 3 * S, "of the", Ink(Shade(green, 0.5)), S);
    small.Draw(d, tx, ty, "of the", Ink(cream), S);

    // The letters' body: extrusion, then rim, then face.
    Extrude(img, rim, 16, Shade(brown, 0.85), Shade(brown, 0.48));
    Put(img, Flat(size, brown), rim);
    (lit, _) = Bevel(rim, 2.5 * S, 1.6);
    Put(img, Flat(size, Mix(brown, white, 0.45), 160), PilImage.Subtract(lit, text));

    var face = new PilImage("RGBA", size.Item1, size.Item2);
    var fd = new PilDraw(face);
    foreach (var (word, x, y) in words)
    {
        var box = big.GetBBox(word.Replace('g', 'n'));
        int x0 = (int)box.Left + x, y0 = (int)box.Top + y, x1 = (int)box.Right + x;
        int y1 = (int)big.GetBBox(word).Bottom + y;
        for (int yy = y0 - 4 * S; yy < y1 + 8 * S; yy++)
        {
            double t = Py.Min(1, Py.Max(0, (double)(yy - y0) / (y1 - y0)));
            fd.Line([(x0 - 8 * S, yy), (x1 + 8 * S, yy)], Ink(Mix([255, 252, 244], [240, 206, 146], Py.Pow(t, 1.3))), 1);
        }
    }
    Put(img, face, text);
    (lit, dark) = Bevel(text, 4 * S, 2.6);
    Put(img, Flat(size, white, 230), lit);
    Put(img, Flat(size, [150, 96, 52], 170), dark);

    foreach (var (word, x, y) in words)
    {
        // The dotless i's top: put the cap a little above its stem.
        string pre = word[..word.IndexOf('ı')];
        double x0 = x + big.GetLength(pre);
        double x1 = x0 + big.GetLength("ı");
        double stemTop = y + big.GetBBox("ı", Bold).Top;
        double cx = (x0 + x1) / 2, cy = stemTop + 2 * S, r = 24 * S;
        PilImage stalk = Mask(size);
        new PilDraw(stalk).Chord(cx - r * 1.3, cy - r * 1.25, cx + r * 1.3, cy + r * 0.45, 180, 360, [255]);
        Extrude(img, stalk.GaussianBlur(0.6 * S), 5, Shade(red, 0.55), Shade(red, 0.38));
        Cap(d, cx, cy, r);
    }
    return Shadow(img, img.GetChannel(3), 7, 6, 105);
}

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path);
