using System;
using System.IO;
using System.Linq;

namespace KotH.Tools.Maps
{
    /// <summary>Flat stand-ins for MountDestiny's trees far off (Pyramid's forest outside the wall): Tiles trees,
    /// each drawn from the side by a small software rasteriser (depth buffer, the leaves' alpha cut, 3 x 3 samples)
    /// into a 2 x 2 atlas, the trunk's foot Ground metres over a tile's bottom middle: the colour as the crowns'
    /// and the bark's materials paint it, unlit, and the normals (x right, y up, z to the viewer) to light it by.</summary>
    public static class Impostors
    {
        public const int Tiles = 4, TilePx = 512, AtlasPx = 1024;
        public const double Size = 9.0, Ground = 0.4;    // a tile's width and height in metres; the foot over its bottom
        const int Sub = 3;                                // samples across a texel
        const double Cut = 0.5;                           // the leaves' alpha cut (Leaves.sdmat)
        const double BarkTile = 2.0;                      // PaintedTriplanar's metres per repeat of Bark.png
        const int BarkShrink = 8;                         // Bark.png box-filtered by this: about a texel's size here

        /// <summary>Draws the trees (trunks of 4 and 5 m in turn) to colour and normals; returns how high the tallest reached.</summary>
        public static double Render(string textures, string colourPath, string normalPath)
        {
            Png leaves = Png.Read(Path.Combine(textures, "Leaves.png")), clumps = Png.Read(Path.Combine(textures, "LeafClump.png"));
            Png bark = Shrink(Png.Read(Path.Combine(textures, "Bark.png")), BarkShrink);
            var colour = new double[AtlasPx * AtlasPx * 4];
            var normal = new double[AtlasPx * AtlasPx * 3];
            double tallest = 0;
            for (int t = 0; t < Tiles; t++)
            {
                var mesh = new Mesh(null!);
                Trees.Grow(mesh, new PyRandom(3100 + t), 0, 0, 0, 4 + t % 2)();
                tallest = Math.Max(tallest, mesh.Pos.Max(p => p.Y) + Ground);
                if (mesh.Pos.Any(p => Math.Abs(p.X - 0.5) > Size / 2))
                    Console.WriteLine($"warning: impostor tree {t} is wider than {Size} m");
                Draw(mesh, leaves, clumps, bark, colour, normal, (t % 2) * TilePx, (t / 2) * TilePx);
            }
            Bleed(colour, normal);
            var c = new Png(AtlasPx, AtlasPx, 4);
            var n = new Png(AtlasPx, AtlasPx, 4);
            for (int i = 0; i < AtlasPx * AtlasPx; i++)
            {
                for (int k = 0; k < 3; k++)
                {
                    c.Pixels[4 * i + k] = Byte(ToSrgb(colour[4 * i + k]));
                    n.Pixels[4 * i + k] = Byte(0.5 + 0.5 * normal[3 * i + k]);
                }
                c.Pixels[4 * i + 3] = n.Pixels[4 * i + 3] = Byte(colour[4 * i + 3]);
            }
            c.Write(colourPath);
            n.Write(normalPath);
            return tallest;
        }

        static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

        // One tree into its tile, seen from +Z (x right, y up), nearer samples (larger z) in front.
        static void Draw(Mesh mesh, Png leaves, Png clumps, Png bark, double[] colour, double[] normal, int ox, int oy)
        {
            // The wood's normals from its faces round each corner (the trunk's rings carry none).
            var nrm = mesh.Nrm.ToArray();
            for (int t = 0; t < mesh.Idx.Count; t += 3)
            {
                int a0 = mesh.Idx[t], b0 = mesh.Idx[t + 1], c0 = mesh.Idx[t + 2];
                if (mesh.Keys[a0].Kind is M.Leaf or M.Clump)
                    continue;
                V3 f = V3.Cross(V3.Sub(mesh.Pos[b0], mesh.Pos[a0]), V3.Sub(mesh.Pos[c0], mesh.Pos[a0]));
                foreach (int v in new[] { a0, b0, c0 })
                    if (Dot(mesh.Nrm[v], mesh.Nrm[v]) < 1e-12)
                        nrm[v] = new V3(nrm[v].X + f.X, nrm[v].Y + f.Y, nrm[v].Z + f.Z);
            }
            int n = TilePx * Sub;
            var depth = new double[n * n];
            var shaded = new (double R, double G, double B, V3 N)[n * n];
            Array.Fill(depth, double.NegativeInfinity);
            (double X, double Y) Screen(V3 p) => ((p.X - 0.5 + Size / 2) / Size * n, (1 - (p.Y + Ground) / Size) * n);
            for (int t = 0; t < mesh.Idx.Count; t += 3)
            {
                int ia = mesh.Idx[t], ib = mesh.Idx[t + 1], ic = mesh.Idx[t + 2];
                var (ax, ay) = Screen(mesh.Pos[ia]);
                var (bx, by) = Screen(mesh.Pos[ib]);
                var (cx, cy) = Screen(mesh.Pos[ic]);
                double area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
                if (Math.Abs(area) < 1e-12)
                    continue;
                int kind = mesh.Keys[ia].Kind;
                bool leaf = kind is M.Leaf or M.Clump;
                int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)))), x1 = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
                int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)))), y1 = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        double px = x + 0.5, py = y + 0.5;
                        double wb = ((px - ax) * (cy - ay) - (cx - ax) * (py - ay)) / area;
                        double wc = ((bx - ax) * (py - ay) - (px - ax) * (by - ay)) / area;
                        double wa = 1 - wb - wc;
                        if (wa < 0 || wb < 0 || wc < 0)
                            continue;
                        V3 p = Mix(mesh.Pos[ia], mesh.Pos[ib], mesh.Pos[ic], wa, wb, wc);
                        int i = y * n + x;
                        if (p.Z <= depth[i])
                            continue;
                        V3 normalHere = Unit(Mix(nrm[ia], nrm[ib], nrm[ic], wa, wb, wc));
                        (double R, double G, double B) c;
                        if (leaf)
                        {
                            double u = wa * mesh.Uv[ia].U + wb * mesh.Uv[ib].U + wc * mesh.Uv[ic].U;
                            double v = wa * mesh.Uv[ia].V + wb * mesh.Uv[ib].V + wc * mesh.Uv[ic].V;
                            var (r, g, b, a) = Sample(kind == M.Leaf ? leaves : clumps, u, v);
                            if (a < Cut)
                                continue;
                            // LeafCluster: the paint times the leaf's shade and a tint of 0.9 to 1.1.
                            var col = mesh.Col[ia];
                            double k = col.Item1 * (0.9 + 0.2 * col.Item2);
                            c = (r * k, g * k, b * k);
                        }
                        else
                        {
                            // PaintedTriplanar: Bark.png from three sides, weighted by the normal's eighth power.
                            double shade = wa * mesh.Uv[ia].U + wb * mesh.Uv[ib].U + wc * mesh.Uv[ic].U;
                            double bxw = Math.Pow(Math.Abs(normalHere.X), 8), byw = Math.Pow(Math.Abs(normalHere.Y), 8), bzw = Math.Pow(Math.Abs(normalHere.Z), 8);
                            double sum = bxw + byw + bzw;
                            var sx = Sample(bark, p.Z / BarkTile, -p.Y / BarkTile);
                            var sy = Sample(bark, p.X / BarkTile, p.Z / BarkTile);
                            var sz = Sample(bark, p.X / BarkTile, -p.Y / BarkTile);
                            c = ((bxw * sx.R + byw * sy.R + bzw * sz.R) / sum * shade, (bxw * sx.G + byw * sy.G + bzw * sz.G) / sum * shade,
                                 (bxw * sx.B + byw * sy.B + bzw * sz.B) / sum * shade);
                        }
                        depth[i] = p.Z;
                        shaded[i] = (c.R, c.G, c.B, normalHere);
                    }
            }
            // Each texel: the mean colour and normal of its covered samples, the share covered as alpha.
            for (int y = 0; y < TilePx; y++)
                for (int x = 0; x < TilePx; x++)
                {
                    double r = 0, g = 0, b = 0, nx = 0, ny = 0, nz = 0;
                    int hit = 0;
                    for (int sy = 0; sy < Sub; sy++)
                        for (int sx = 0; sx < Sub; sx++)
                        {
                            int i = (y * Sub + sy) * n + x * Sub + sx;
                            if (double.IsNegativeInfinity(depth[i]))
                                continue;
                            var s = shaded[i];
                            (r, g, b, nx, ny, nz, hit) = (r + s.R, g + s.G, b + s.B, nx + s.N.X, ny + s.N.Y, nz + s.N.Z, hit + 1);
                        }
                    int o = (oy + y) * AtlasPx + ox + x;
                    if (hit > 0)
                    {
                        (colour[4 * o], colour[4 * o + 1], colour[4 * o + 2]) = (r / hit, g / hit, b / hit);
                        V3 m = Unit(new V3(nx, ny, nz));
                        (normal[3 * o], normal[3 * o + 1], normal[3 * o + 2]) = (m.X, m.Y, m.Z);
                    }
                    colour[4 * o + 3] = hit / (double)(Sub * Sub);
                }
        }

        // Clear texels take their covered neighbours' mean colour and normal, ring by ring, so mips and filtering keep them.
        static void Bleed(double[] colour, double[] normal)
        {
            var known = new bool[AtlasPx * AtlasPx];
            for (int i = 0; i < known.Length; i++)
                known[i] = colour[4 * i + 3] > 0;
            for (int pass = 0; pass < 64; pass++)
            {
                var next = (bool[])known.Clone();
                bool any = false;
                for (int y = 0; y < AtlasPx; y++)
                    for (int x = 0; x < AtlasPx; x++)
                    {
                        int i = y * AtlasPx + x;
                        if (known[i])
                            continue;
                        double r = 0, g = 0, b = 0, nx = 0, ny = 0, nz = 0;
                        int k = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int qx = x + dx, qy = y + dy;
                                if (qx < 0 || qy < 0 || qx >= AtlasPx || qy >= AtlasPx || !known[qy * AtlasPx + qx])
                                    continue;
                                int j = qy * AtlasPx + qx;
                                (r, g, b, k) = (r + colour[4 * j], g + colour[4 * j + 1], b + colour[4 * j + 2], k + 1);
                                (nx, ny, nz) = (nx + normal[3 * j], ny + normal[3 * j + 1], nz + normal[3 * j + 2]);
                            }
                        if (k == 0)
                            continue;
                        (colour[4 * i], colour[4 * i + 1], colour[4 * i + 2]) = (r / k, g / k, b / k);
                        V3 m = Unit(new V3(nx, ny, nz));
                        (normal[3 * i], normal[3 * i + 1], normal[3 * i + 2]) = (m.X, m.Y, m.Z);
                        next[i] = any = true;
                    }
                known = next;
                if (!any)
                    break;
            }
        }

        // A texture box-filtered down by f in linear light, so the far bark is its mean grain, not noise.
        static Png Shrink(Png png, int f)
        {
            var o = new Png(png.Width / f, png.Height / f, 3);
            for (int y = 0; y < o.Height; y++)
                for (int x = 0; x < o.Width; x++)
                    for (int k = 0; k < 3; k++)
                    {
                        double s = 0;
                        for (int dy = 0; dy < f; dy++)
                            for (int dx = 0; dx < f; dx++)
                                s += ToLinear(png.Pixels[png.Channels * ((y * f + dy) * png.Width + x * f + dx) + k] / 255.0);
                        o.Pixels[3 * (y * o.Width + x) + k] = Byte(ToSrgb(s / (f * f)));
                    }
            return o;
        }

        // Bilinear, wrapping, in linear light; alpha as stored.
        static (double R, double G, double B, double A) Sample(Png png, double u, double v)
        {
            double fx = u * png.Width - 0.5, fy = v * png.Height - 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;
            double r = 0, g = 0, b = 0, a = 0;
            for (int j = 0; j < 2; j++)
                for (int i = 0; i < 2; i++)
                {
                    double w = (i == 0 ? 1 - tx : tx) * (j == 0 ? 1 - ty : ty);
                    int x = ((x0 + i) % png.Width + png.Width) % png.Width, y = ((y0 + j) % png.Height + png.Height) % png.Height;
                    int o = png.Channels * (y * png.Width + x);
                    r += w * ToLinear(png.Pixels[o] / 255.0);
                    g += w * ToLinear(png.Pixels[o + 1] / 255.0);
                    b += w * ToLinear(png.Pixels[o + 2] / 255.0);
                    a += w * (png.Channels == 4 ? png.Pixels[o + 3] / 255.0 : 1);
                }
            return (r, g, b, a);
        }

        static V3 Mix(V3 a, V3 b, V3 c, double wa, double wb, double wc) =>
            new(wa * a.X + wb * b.X + wc * c.X, wa * a.Y + wb * b.Y + wc * c.Y, wa * a.Z + wb * b.Z + wc * c.Z);
        static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        static V3 Unit(V3 v)
        {
            double l = Math.Sqrt(Dot(v, v));
            return l > 1e-12 ? new V3(v.X / l, v.Y / l, v.Z / l) : new V3(0, 0, 1);
        }
        static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        static double ToSrgb(double c) => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
    }
}
