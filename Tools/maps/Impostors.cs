using System;
using System.IO;
using System.Linq;

namespace KotH.Tools.Maps
{
    /// <summary>Flat stand-ins for MountDestiny's trees far off (Pyramid's forest outside the wall): Tiles trees,
    /// each drawn from the side by a small software rasteriser (depth buffer, the leaves' alpha cut, 3 x 3 samples)
    /// into a 2 x 2 atlas, the trunk's foot Ground metres over a tile's bottom middle. Leaves.png and LeafClump.png
    /// as the crowns' materials paint them, bark a flat shaded colour; texels left clear take the nearest colour.</summary>
    public static class Impostors
    {
        public const int Tiles = 4, TilePx = 512, AtlasPx = 1024;
        public const double Size = 9.0, Ground = 0.4;    // a tile's width and height in metres; the foot over its bottom
        const int Sub = 3;                                // samples across a texel
        const double Cut = 0.5;                           // the leaves' alpha cut (Leaves.sdmat)

        /// <summary>Draws the trees (trunks of 4 and 5 m in turn) to path; returns how high the tallest reached.</summary>
        public static double Render(string textures, string path)
        {
            Png leaves = Png.Read(Path.Combine(textures, "Leaves.png")), clumps = Png.Read(Path.Combine(textures, "LeafClump.png"));
            Png barkTexture = Png.Read(Path.Combine(textures, "Bark.png"));
            var bark = Mean(barkTexture);
            var atlas = new double[AtlasPx * AtlasPx * 4];
            double tallest = 0;
            for (int t = 0; t < Tiles; t++)
            {
                var mesh = new Mesh(null!);
                Trees.Grow(mesh, new PyRandom(3100 + t), 0, 0, 0, 4 + t % 2)();
                tallest = Math.Max(tallest, mesh.Pos.Max(p => p.Y) + Ground);
                if (mesh.Pos.Any(p => Math.Abs(p.X - 0.5) > Size / 2))
                    Console.WriteLine($"warning: impostor tree {t} is wider than {Size} m");
                Draw(mesh, leaves, clumps, bark, atlas, (t % 2) * TilePx, (t / 2) * TilePx);
            }
            Bleed(atlas);
            var png = new Png(AtlasPx, AtlasPx, 4);
            for (int i = 0; i < AtlasPx * AtlasPx; i++)
                for (int k = 0; k < 4; k++)
                    png.Pixels[4 * i + k] = (byte)Math.Round(Math.Clamp(k < 3 ? ToSrgb(atlas[4 * i + k]) : atlas[4 * i + k], 0, 1) * 255);
            png.Write(path);
            return tallest;
        }

        // One tree into its tile, seen from +Z (x right, y up), nearer samples (larger z) in front.
        static void Draw(Mesh mesh, Png leaves, Png clumps, (double R, double G, double B) bark, double[] atlas, int ox, int oy)
        {
            int n = TilePx * Sub;
            var depth = new double[n * n];
            var colour = new (double R, double G, double B)[n * n];
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
                        double z = wa * mesh.Pos[ia].Z + wb * mesh.Pos[ib].Z + wc * mesh.Pos[ic].Z;
                        int i = y * n + x;
                        if (z <= depth[i])
                            continue;
                        (double R, double G, double B) c;
                        if (kind == M.Leaf || kind == M.Clump)
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
                            double shade = wa * mesh.Uv[ia].U + wb * mesh.Uv[ib].U + wc * mesh.Uv[ic].U;
                            c = (bark.R * shade, bark.G * shade, bark.B * shade);
                        }
                        depth[i] = z;
                        colour[i] = c;
                    }
            }
            // Each texel: the mean colour of its covered samples, the share covered as alpha.
            for (int y = 0; y < TilePx; y++)
                for (int x = 0; x < TilePx; x++)
                {
                    double r = 0, g = 0, b = 0;
                    int hit = 0;
                    for (int sy = 0; sy < Sub; sy++)
                        for (int sx = 0; sx < Sub; sx++)
                        {
                            int i = (y * Sub + sy) * n + x * Sub + sx;
                            if (double.IsNegativeInfinity(depth[i]))
                                continue;
                            (r, g, b, hit) = (r + colour[i].R, g + colour[i].G, b + colour[i].B, hit + 1);
                        }
                    int o = 4 * ((oy + y) * AtlasPx + ox + x);
                    if (hit > 0)
                        (atlas[o], atlas[o + 1], atlas[o + 2]) = (r / hit, g / hit, b / hit);
                    atlas[o + 3] = hit / (double)(Sub * Sub);
                }
        }

        // Clear texels take their covered neighbours' mean, ring by ring, so mips and filtering keep the crown's colour.
        static void Bleed(double[] atlas)
        {
            var known = new bool[AtlasPx * AtlasPx];
            for (int i = 0; i < known.Length; i++)
                known[i] = atlas[4 * i + 3] > 0;
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
                        double r = 0, g = 0, b = 0;
                        int k = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= AtlasPx || ny >= AtlasPx || !known[ny * AtlasPx + nx])
                                    continue;
                                int j = 4 * (ny * AtlasPx + nx);
                                (r, g, b, k) = (r + atlas[j], g + atlas[j + 1], b + atlas[j + 2], k + 1);
                            }
                        if (k == 0)
                            continue;
                        (atlas[4 * i], atlas[4 * i + 1], atlas[4 * i + 2]) = (r / k, g / k, b / k);
                        next[i] = any = true;
                    }
                known = next;
                if (!any)
                    break;
            }
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

        static (double R, double G, double B) Mean(Png png)
        {
            double r = 0, g = 0, b = 0;
            int n = png.Width * png.Height;
            for (int i = 0; i < n; i++)
            {
                r += ToLinear(png.Pixels[png.Channels * i] / 255.0);
                g += ToLinear(png.Pixels[png.Channels * i + 1] / 255.0);
                b += ToLinear(png.Pixels[png.Channels * i + 2] / 255.0);
            }
            return (r / n, g / n, b / n);
        }

        static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        static double ToSrgb(double c) => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
    }
}
