using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace KotH.Tools.Maps;

// The block mountain of map "Neu" (Mountain.cs runs it): the terrain, its mesh and what
// is written from it. Ported from mountain.py, operation for operation: the floats are
// Python's (Py.Sum, Py.Hypot, Py.Pow, libm), so Mountain.glb, Sea.glb and NeuTerrain.bin
// come out as the script made them.

public readonly record struct Rgb(double R, double G, double B)
{
    public double this[int i] => i == 0 ? R : i == 1 ? G : B;
}

public readonly record struct V3(double X, double Y, double Z)
{
    public double this[int i] => i == 0 ? X : i == 1 ? Y : Z;
    public static V3 Sub(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static double Dist(V3 a, V3 b) => Math.Sqrt(Py.Sum([Py.Pow(a.X - b.X, 2), Py.Pow(a.Y - b.Y, 2), Py.Pow(a.Z - b.Z, 2)]));
}

// Fields rather than constants: the .work scripts set them by mountain.py's names (NAME=VALUE).
public static class M
{
    public static int Seed = 7;
    public static int Half = 133;               // cells from the centre to the edge of the grid
    public static double Island = 119.5;        // coast radius, varied by CoastWaves
    public static readonly (double M, int Cells)[] CoastWaves = [(16.0, 3), (11.0, 6), (6.0, 13), (2.5, 29), (1.0, 67)];
    public static double CoastMax = 126.0;      // the GI volume ends at 128 m
    public static double Beach = 7.0;           // sand this far in from the coast, varied by BeachWaves (Luca: less round)
    public static readonly (double M, int Cells)[] BeachWaves = [(4.5, 4), (3.0, 9), (1.5, 23)];
    public static double BeachMin = 3.5;
    public static double MeadowMin = 12.0;      // the meadow reaches at least this far out past the foot (spawns, trees)
    public static double Soft = 3.0;            // metres over which those limits ease in
    public static double OldIsland = 117.0, OldCoast = 4.0, OldBeach = 5.0;   // the round island the trees were planted on
    public static double Mountain = 95.0;       // foot of the mountain, varied by Foot
    public static double Foot = 4.0;
    public static int GrassY = 2;               // top of the meadow
    public static int SandY = 1;
    public static int SummitY = 123;            // top of the summit plateau (Luca)
    public static int Plateau = 4;              // plateau: 2 * Plateau + 2 cells across, centred on the origin
    public static double TopBlend = 2.0;        // the slope eases into the plateau over this many metres down
    public static double TopRun = 4.0;          // and this many metres across (Luca: 2 m and 4 m)
    public static double TopFade = 12.0;        // ridges and columns grow in over this many metres below the plateau
    public static double Profile = 1.1;         // height = (1 - r / foot) ^ Profile
    public static double Ridges = 9.0;          // depth of the ridges running down the slope
    public static double RidgeSpacing = 3.0;    // metres between ridges around the mountain
    public static double RidgeLength = 13.0;    // metres a ridge runs down before it changes
    public static int Column = 2;               // the slope's columns are this many blocks wide round the mountain
    public static int ColumnDown = 1;           // and this many deep down it (Luca: tops to stand on)
    public static double Columns = 2.0;         // fine columns between the ridges, as in the video
    public static double ColumnSpacing = 1.3;
    public static double ColumnLength = 5.0;
    public static double Bumps = 4.0;           // larger lumps
    public static double Jitter = 0.15;         // share of columns a block higher or lower at random
    public static double Snowline = 0.55;       // share of the summit height, varied by SnowNoise
    public static double SnowNoise = 9.0;
    public static double Caps = 8.0;            // snow caps on tops this far below the snow line
    public static double South = Math.PI / 2;   // +Z, where the meadow with the spawns is
    public static int Sectors = 12;             // directions round the foot the climb is checked from
    public static int MinSectors = 9;           // the summit must be reached from this many of them
    public static int Trees = 36;
    public static double SpawnGap = 7.0;        // first spawn row this far out from the foot in the south
    public static double TreeGap = 7.0;
    public static int Chunk = 4;                // collision chunks, metres across
    public static int Under = 0;                // the coast is drawn down to here, just under the sea
    public static double Simplify = 0.02;       // the simplified mesh stays this close (metres) to every original face
    public static double SimplifyColour = 0.03; // and merges only vertices this close in paint weights and shade
    public static double SimplifyNormal = 0.95; // and with vertex normals at least this parallel
    public static double SeaUnder = 1.0;        // the sea reaches at least this far in under the coast line
    public static double EdgeIn = 0.35;         // the beach's edges follow their lines, not the blocks (Luca: too blocky):
    public static double EdgeOut = 0.3;         // an edge's top this far inside the line, its foot this far outside
    public static double BankIn = 0.7;          // the same for the bank from the meadow down to the sand, wider
    public static double BankOut = 0.7;         // (Luca: the drop was too steep)
    public static double EdgeRound = 0.85;      // share of the way from the blocks to the line (Luca)
    public static double SeaReach = 30000.0;    // the sea's half width, beyond the camera's far plane
    public static double SeaGrowth = 1.5;       // each ring of the outer sea's tiles this much wider than the last

    // Colours in sRGB.
    public static readonly Rgb Stone = new(0.55, 0.51, 0.45);
    public static readonly Rgb Snow = new(0.93, 0.95, 0.97);
    public static readonly Rgb Grass = new(0.37, 0.55, 0.23);
    public static readonly Rgb Dirt = new(0.47, 0.35, 0.23);
    public static readonly Rgb Sand = new(0.86, 0.81, 0.60);
    public static readonly Rgb Wood = new(0.42, 0.31, 0.18);
    public static readonly Rgb Leaves = new(0.24, 0.42, 0.16);
    public static double AoStep = 0.16;         // darker per solid block over half of a corner's eight (Minecraft's steps)
    public static double Smooth = 0.7;          // Surface Nets strength: 0 cubes, 1 as round as NoCubes
    public static double SlopeLimit = 50.0;     // the player motor's, to report how much ground one stands on
    public static double TrunkRadius = 0.5;     // trunks are round (Luca), as thick as the block was
    public static double TrunkFoot = 0.55;      // and this thick at the ground, widening over TrunkFlare like roots
    public static double TrunkFlare = 0.5;
    public static int TrunkSides = 16;
    public static double CrownClear = 2.3;      // the leaves stay this high over the ground: heads pass under them
    // Trees after Luca's picture (28.10.2026): the trunk forks into limbs that fork again,
    // round clumps of leaves on their tips. Only trunk and main limbs are solid (Luca:
    // one walks through the crowns).
    public static readonly (int Min, int Max) Limbs = (3, 4);
    public static readonly (double Min, double Max) LimbTilt = (0.5, 0.75);       // radians from upright
    public static readonly (double Min, double Max) LimbLength = (1.7, 2.2);
    public static readonly (double Min, double Max) BranchLength = (1.1, 1.5);
    public static readonly (double Root, double Tip) LimbRadius = (0.3, 0.19), BranchRadius = (0.17, 0.08);
    public static readonly (double Min, double Max) ClumpRadius = (1.1, 1.45);     // across; 0.8 of it up
    // Every clump of leaves (Luca: a mix, fuller and denser): cards with painted clumps
    // (LeafClump.png) fill it inside, single leaves from Leaves.png make its outside, now
    // and then a twig card stands out.
    public static double LeafDensity = 24;      // single leaves per square metre of a clump's surface
    public static readonly (double Min, double Max) LeafLength = (0.32, 0.46);
    public static readonly (double Min, double Max) LeafDepth = (0.62, 1.0);       // share of the clump's radius
    public static double CardDensity = 1.6;     // clump cards per square metre
    public static readonly (double Min, double Max) CardSize = (1.0, 1.25);
    public static readonly (double Min, double Max) CardDepth = (0.3, 0.75);
    public static double SprigDensity = 1.46;   // twig cards (Luca: the twig texture now and then, 200 a tree)
    public static readonly (double Min, double Max) SprigLength = (0.75, 1.0);
    public static readonly (double Min, double Max) SprigDepth = (0.75, 0.95);

    // What a block is; -1 is air.
    public const int Air = -1, Ground = 0, Trunk = 2, Leaf = 3, Clump = 4;

    // The ground's surfaces in the order of TerrainTriplanar's weights; rock is the rest.
    public static readonly (Rgb Top, Rgb Side)[] Paints = [(Grass, Dirt), (Sand, Sand), (Snow, Snow), (Snow, Stone), (Stone, Stone)];

    /// <summary>A random value in [0, 1) for a lattice point, the same every run.</summary>
    public static double Lattice(long seed, params long[] k)
    {
        uint h = (uint)seed;
        foreach (long v in k)
        {
            h = (h ^ (uint)v) * 0x9E3779B1u;
            h ^= h >> 15;
            h *= 0x85EBCA77u;
            h ^= h >> 13;
        }
        return h / 4294967296.0;
    }

    /// <summary>Smooth value noise in [-1, 1]; wraps every 'wrap' cells in u if given.</summary>
    public static double Noise(long seed, double u, double v, int wrap = 0)
    {
        long iu = (long)Math.Floor(u), iv = (long)Math.Floor(v);
        double fu = u - iu, fv = v - iv;
        double su = fu * fu * (3 - 2 * fu), sv = fv * fv * (3 - 2 * fv);
        double At(long a, long b) => Lattice(seed, wrap != 0 ? Py.Mod(a, (long)wrap) : a, b) * 2 - 1;
        double top = At(iu, iv) + (At(iu + 1, iv) - At(iu, iv)) * su;
        double bottom = At(iu, iv + 1) + (At(iu + 1, iv + 1) - At(iu, iv + 1)) * su;
        return top + (bottom - top) * sv;
    }

    /// <summary>Noise around a circle, seamless.</summary>
    public static double Ring(long seed, double angle, int cells) => Noise(seed, angle / (2 * Math.PI) * cells, 0.5, cells);

    /// <summary>max(a, b), eased over Soft metres: a limit leaves no straight stretch.</summary>
    public static double SoftMax(double a, double b)
    {
        double h = Py.Max(Soft - Math.Abs(a - b), 0.0) / Soft;
        return Py.Max(a, b) + h * h * Soft / 4;
    }

    /// <summary>Smooth value noise in [-1, 1] in three dimensions.</summary>
    public static double Noise3(long seed, double u, double v, double w)
    {
        long iu = (long)Math.Floor(u), iv = (long)Math.Floor(v), iw = (long)Math.Floor(w);
        double[] f = [u - iu, v - iv, w - iw];
        double[] s = f.Select(t => t * t * (3 - 2 * t)).ToArray();
        double total = 0.0;
        for (int a = 0; a < 2; a++)
            for (int b = 0; b < 2; b++)
                for (int c = 0; c < 2; c++)
                {
                    double weight = (a != 0 ? s[0] : 1 - s[0]) * (b != 0 ? s[1] : 1 - s[1]) * (c != 0 ? s[2] : 1 - s[2]);
                    total += weight * (Lattice(seed, iu + a, iv + b, iw + c) * 2 - 1);
                }
        return total;
    }
}

public sealed class Terrain
{
    public readonly int Seed;
    public readonly OrderedDictionary<(int X, int Z), int> Height = new();   // top y of the column
    public readonly Dictionary<(int X, int Z), (Rgb Top, Rgb Side)> Material = new();
    public readonly Dictionary<(int X, int Z), double> Tint = new();
    public readonly HashSet<(int X, int Y, int Z)> Trees = new();            // leaf blocks
    public readonly List<(int X, int Z, int Top, List<(int X, int Y, int Z)> Blocks, int N)> Crowns = new();
    public readonly HashSet<(int X, int Y, int Z)> Wood = new();
    public int Dropped;                                                      // trees the new coast left too close to the sand
    public int Raised;

    public Terrain(int seed)
    {
        Seed = seed;
        Build();
    }

    // Cell (x, z) spans x..x+1 and z..z+1; its centre is +0.5.
    public (double R, double A) Polar(double x, double z)
    {
        double cx = x + 0.5, cz = z + 0.5;
        return (Py.Hypot(cx, cz), Py.Mod(Libm.atan2(cz, cx), 2 * Math.PI));
    }

    public double Foot(double angle) => M.Mountain + M.Foot * M.Ring(Seed + 1, angle, 7);

    public double Coast(double angle)
    {
        double r = M.Island + Py.Sum(M.CoastWaves.Select((w, k) => w.M * M.Ring(Seed + 20 + k, angle, w.Cells)));
        return -M.SoftMax(-M.SoftMax(r, Foot(angle) + M.MeadowMin + Beach(angle)), -M.CoastMax);
    }

    public double Beach(double angle)
    {
        double w = M.Beach + Py.Sum(M.BeachWaves.Select((b, k) => b.M * M.Ring(Seed + 30 + k, angle, b.Cells)));
        return M.SoftMax(w, M.BeachMin);
    }

    public double OldMeadowEdge(double angle) => M.OldIsland + M.OldCoast * M.Ring(Seed + 2, angle, 9) - M.OldBeach;

    int? HeightAt(int x, int z) => Height.TryGetValue((x, z), out int h) ? h : null;

    /// <summary>
    /// A ground corner on the beach's inner edge (grass to sand) or on the coast (sand to
    /// sea) moved from the centre EdgeRound of the way onto that edge's line, top a little
    /// inside, foot a little outside: the edge runs smoother than the block steps.
    /// </summary>
    public V3 Rounded((int X, int Y, int Z) corner, V3 p)
    {
        var (cx, cy, cz) = corner;
        if (cy > M.GrassY)
            return p;
        int?[] columns = [HeightAt(cx - 1, cz - 1), HeightAt(cx - 1, cz), HeightAt(cx, cz - 1), HeightAt(cx, cz)];
        if (!columns.Contains(M.SandY))
            return p;
        bool top;
        if (columns.Contains(null))
            top = cy >= M.SandY;
        else if (columns.Contains(M.GrassY) && columns.Max() == M.GrassY)
            top = cy >= M.GrassY;
        else
            return p;
        double r = Py.Hypot(p.X, p.Z);
        double a = Py.Mod(Libm.atan2(p.Z, p.X), 2 * Math.PI);
        bool bank = !columns.Contains(null);
        double line = Coast(a) - (bank ? Beach(a) : 0.0);
        double target = top ? line - (bank ? M.BankIn : M.EdgeIn) : line + (bank ? M.BankOut : M.EdgeOut);
        double k = (r + M.EdgeRound * (target - r)) / r;
        return new V3(p.X * k, p.Y, p.Z * k);
    }

    void Build()
    {
        int s = Seed;
        for (int x = -M.Half; x < M.Half; x++)
            for (int z = -M.Half; z < M.Half; z++)
            {
                var (r, a) = Polar(x, z);
                double coast = Coast(a);
                if (r > coast)
                    continue;
                double foot = Foot(a);
                if (r > coast - Beach(a))
                    Height[(x, z)] = M.SandY;
                else if (r >= foot)
                    Height[(x, z)] = M.GrassY;
                else if (Py.Max(Math.Abs(x + 0.5), Math.Abs(z + 0.5)) <= M.Plateau + 0.5)
                    Height[(x, z)] = M.SummitY;
                else
                {
                    // The slope is shaped per group of cells, Column wide round the mountain
                    // and ColumnDown deep down it: wide tops to stand on, short steps up.
                    int w, d;
                    if (Math.Abs(z + 0.5) >= Math.Abs(x + 0.5))
                        (w, d) = (M.Column, M.ColumnDown);
                    else
                        (w, d) = (M.ColumnDown, M.Column);
                    int gx = Py.FloorDiv(x, w) * w, gz = Py.FloorDiv(z, d) * d;
                    (r, a) = Polar(gx + (w - 1) / 2.0, gz + (d - 1) / 2.0);
                    // Measured from the square plateau's edge, so the slope meets it all round.
                    int edge = M.Plateau + 1;
                    double cx = gx + w / 2.0, cz = gz + d / 2.0;
                    double dist = Py.Hypot(Py.Max(0.0, Math.Abs(cx) - edge), Py.Max(0.0, Math.Abs(cz) - edge));
                    // The first TopRun metres drop TopBlend metres on a cubic that starts
                    // flat and meets the main profile with its slope; ridges fade in below.
                    double rest = M.SummitY - M.GrassY - M.TopBlend, run = foot - edge - M.TopRun;
                    double t, below;
                    if (dist < M.TopRun)
                    {
                        t = 0.0;
                        double u = dist / M.TopRun;
                        double b = rest * M.Profile * M.TopRun / run - 2 * M.TopBlend;
                        below = Py.Max(0.0, (M.TopBlend - b) * u * u + b * u * u * u);
                    }
                    else
                    {
                        t = Py.Min(1.0, (dist - M.TopRun) / run);
                        below = M.TopBlend + rest * (1 - Py.Pow(1 - t, M.Profile));
                    }
                    double fade = Py.Min(1.0, below / M.TopFade);
                    fade = fade * fade * (3 - 2 * fade);
                    double h = M.SummitY - below;
                    // Ridges: around the mountain short, down the slope long.
                    double around = a * foot / M.RidgeSpacing;
                    int cells = Math.Max(1, Py.Round(2 * Math.PI * foot / M.RidgeSpacing));
                    double rough = 0.6 + 0.4 * (1 - t);
                    double lumps = M.Ridges * rough * M.Noise(s + 3, around, r / M.RidgeLength, cells);
                    int fine = Math.Max(1, Py.Round(2 * Math.PI * foot / M.ColumnSpacing));
                    lumps += M.Columns * M.Noise(s + 10, a * foot / M.ColumnSpacing, r / M.ColumnLength, fine);
                    lumps += M.Bumps * M.Noise(s + 4, gx / 11.0, gz / 11.0);
                    double jitter = M.Lattice(s + 11, gx, gz);
                    lumps += jitter < M.Jitter / 2 ? -1 : jitter > 1 - M.Jitter / 2 ? 1 : 0;
                    // Bumps never climb back up past half the depth: no towers by the plateau.
                    Height[(x, z)] = Math.Max(M.GrassY, Math.Min(M.SummitY, Py.Round(h + Py.Min(below / 2, fade * lumps))));
                }
            }
        FillSlots();
        DrainTraps();
        Paint();
        Plant();
    }

    // Slots one or two blocks wide between walls two blocks higher are raised to the
    // lower wall: too narrow for a body, they would trap it.
    void FillSlots()
    {
        for (int round = 0; round < 6; round++)
        {
            bool changed = false;
            foreach (var (x, z) in Height.Keys.Order().ToList())
                foreach (var (dx, dz) in new[] { (1, 0), (0, 1) })
                    for (int w = 1; w <= 2; w++)
                    {
                        var cells = Enumerable.Range(0, w).Select(i => (x + i * dx, z + i * dz)).ToList();
                        (int, int) before = (x - dx, z - dz), after = (x + w * dx, z + w * dz);
                        if (cells.Any(c => !Height.ContainsKey(c)))
                            continue;
                        if (!Height.ContainsKey(before) || !Height.ContainsKey(after))
                            continue;
                        int top = cells.Max(c => Height[c]);
                        int wall = Math.Min(Height[before], Height[after]);
                        if (wall - top >= 2)
                        {
                            foreach (var c in cells)
                                Height[c] = wall;
                            changed = true;
                        }
                    }
            if (!changed)
                break;
        }
    }

    static readonly (int, int)[] Sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    // Every column from which the beach can be reached: up at most two blocks at a
    // time (a double jump), down any.
    HashSet<(int, int)> Escapable()
    {
        var output = Height.Where(e => e.Value <= M.SandY).Select(e => e.Key).ToHashSet();
        var todo = new Queue<(int, int)>(output.Order());
        while (todo.Count > 0)
        {
            var (x, z) = todo.Dequeue();
            int h = Height[(x, z)];
            foreach (var (dx, dz) in Sides)
            {
                var b = (x + dx, z + dz);
                if (Height.TryGetValue(b, out int hb) && !output.Contains(b) && h - hb <= 2)
                {
                    output.Add(b);
                    todo.Enqueue(b);
                }
            }
        }
        return output;
    }

    // Raises every pit nobody gets out of to one block under the lowest rim that leads
    // out, until there is none.
    void DrainTraps()
    {
        Raised = 0;
        for (int round = 0; round < 200; round++)
        {
            var output = Escapable();
            var edge = new List<((int, int) Cell, int Y)>();
            foreach (var ((x, z), h) in Height)
            {
                if (output.Contains((x, z)))
                    continue;
                var rims = Sides.Select(s => (x + s.Item1, z + s.Item2)).Where(output.Contains).Select(b => Height[b]).ToList();
                if (rims.Count > 0)
                    edge.Add(((x, z), rims.Min() - 1));
            }
            if (edge.Count == 0)
                return;
            foreach (var (c, y) in edge)
                if (y > Height[c])
                {
                    Height[c] = y;
                    Raised++;
                }
        }
        throw new Exception("Abbruch: Fallen lassen sich nicht auffüllen");
    }

    void Paint()
    {
        int s = Seed;
        double snowline = M.Snowline * M.SummitY;
        foreach (var ((x, z), h) in Height)
        {
            var (r, a) = Polar(x, z);
            if (h <= M.SandY)
            {
                Material[(x, z)] = (M.Sand, M.Sand);
                Tint[(x, z)] = 1.0;
            }
            else if (h == M.GrassY && r >= Foot(a))
            {
                Material[(x, z)] = (M.Grass, M.Dirt);
                Tint[(x, z)] = 1.0;
            }
            else
            {
                double line = snowline + M.SnowNoise * M.Noise(s + 5, x / 9.0, z / 9.0);
                if (h >= line)
                    Material[(x, z)] = (M.Snow, M.Snow);
                else if (h >= line - M.Caps && M.Lattice(s + 6, x, z) < 0.35)
                    Material[(x, z)] = (M.Snow, M.Stone);
                else
                    Material[(x, z)] = (M.Stone, M.Stone);
                Tint[(x, z)] = 0.93 + 0.07 * M.Lattice(s + 7, x, z) + 0.04 * M.Noise(s + 8, x / 5.0, z / 5.0);
            }
        }
    }

    // Trees on the meadow, never by the spawns.
    void Plant()
    {
        var rng = new PyRandom(Seed + 9);
        var spots = new List<(int X, int Z)>();
        // Chosen from the old round island's meadow, so the trees stand where they did.
        var meadow = Height.Keys.Where(c =>
        {
            var (r, a) = Polar(c.X, c.Z);
            return Foot(a) <= r && r <= OldMeadowEdge(a);
        }).Order().ToList();
        int tries = 0;
        double spawn = Foot(M.South) + M.SpawnGap;
        while (spots.Count < M.Trees && tries < 20000)
        {
            tries++;
            var (x, z) = rng.Choice(meadow);
            var (r, a) = Polar(x, z);
            if (r < Foot(a) + 4 || r > OldMeadowEdge(a) - 3)
                continue;
            if (-10 <= x && x <= 10 && spawn - 8 <= z && z <= spawn + 14)     // around the spawns
                continue;
            if (spots.Any(sp => Py.Hypot(x - sp.X, z - sp.Z) < M.TreeGap))
                continue;
            spots.Add((x, z));
        }
        for (int n = 0; n < spots.Count; n++)
        {
            var (x, z) = spots[n];
            int trunk = rng.RandInt(4, 6);
            int bottom = Height[(x, z)];
            int top = bottom + trunk;
            var wood = Enumerable.Range(bottom, top - bottom).Select(y => (x, y, z)).ToHashSet();
            var blocks = new List<(int, int, int)>();
            foreach (var (y, reach) in new[] { (top - 2, 2), (top - 1, 2), (top, 1), (top + 1, 1) })
                for (int dx = -reach; dx <= reach; dx++)
                    for (int dz = -reach; dz <= reach; dz++)
                    {
                        bool corner = Math.Abs(dx) == reach && Math.Abs(dz) == reach;
                        if (corner && (y >= top || rng.Random() < 0.5))
                            continue;
                        if (y == top + 1 && Math.Abs(dx) + Math.Abs(dz) > 1)
                            continue;
                        if (!wood.Contains((x + dx, y, z + dz)))
                            blocks.Add((x + dx, y, z + dz));
                    }
            // Built in full either way, so the trees after it stay the same.
            var (rr, aa) = Polar(x, z);
            if (rr > Coast(aa) - Beach(aa) - 3)
            {
                Dropped++;
                continue;
            }
            Crowns.Add((x, z, top, blocks, n));
            Wood.UnionWith(wood);
            Trees.UnionWith(blocks);
        }
    }

    public int Block(int x, int y, int z)
    {
        if (Height.TryGetValue((x, z), out int h) && y < h)
            return M.Ground;
        if (Wood.Contains((x, y, z)))
            return M.Trunk;
        return M.Air;
    }
}

public static class Climb
{
    static readonly (int, int)[] Sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    // Breadth-first over the columns: up 'limit' blocks at most (one: a jump, two: a double
    // jump), down any. With 'near', the meadow counts only that close to the first start.
    public static HashSet<(int, int)> Climbable(Terrain t, List<(int X, int Z)> starts, int limit, double near = 0)
    {
        var seen = new HashSet<(int, int)>(starts);
        var todo = new Queue<(int X, int Z)>(starts);
        while (todo.Count > 0)
        {
            var (x, z) = todo.Dequeue();
            int h = t.Height[(x, z)];
            foreach (var (dx, dz) in Sides)
            {
                var n = (X: x + dx, Z: z + dz);
                if (seen.Contains(n) || !t.Height.TryGetValue(n, out int hn) || hn - h > limit)
                    continue;
                if (near != 0 && hn <= M.GrassY && Py.Hypot(n.X - starts[0].X, n.Z - starts[0].Z) > near)
                    continue;
                seen.Add(n);
                todo.Enqueue(n);
            }
        }
        return seen;
    }

    // Seconds from 'start' to the summit along the quickest columns: a metre on foot
    // 0.2 s, a jump up 0.65 s, a double jump 1 s; none if no way.
    public static double? FastestClimb(Terrain t, (int X, int Z) start)
    {
        var dist = new Dictionary<(int, int), double> { [start] = 0.0 };
        var todo = new PriorityQueue<(double D, (int X, int Z) C), (double, int, int)>();
        todo.Enqueue((0.0, start), (0.0, start.X, start.Z));
        while (todo.Count > 0)
        {
            var (d, c) = todo.Dequeue();
            if (d > dist[c])
                continue;
            if (t.Height[c] == M.SummitY)
                return d;
            foreach (var (dx, dz) in Sides)
            {
                var n = (X: c.X + dx, Z: c.Z + dz);
                if (!t.Height.TryGetValue(n, out int hn) || hn - t.Height[c] > 2)
                    continue;
                int up = hn - t.Height[c];
                double nd = d + (up <= 0 ? 0.2 : up == 1 ? 0.65 : 1.0);
                if (nd < dist.GetValueOrDefault(n, 1e9))
                {
                    dist[n] = nd;
                    todo.Enqueue((nd, n), (nd, n.X, n.Z));
                }
            }
        }
        return null;
    }

    // No paths are laid: the slope itself has to let one up. Reports from how many of
    // Sectors directions round the foot the summit is reached, how much of the mountain
    // one gets onto, and the quickest way up.
    public static ((bool, double)[] Shares, int Sectors, double? Quickest) Check(Terrain t)
    {
        var summit = t.Height.Where(e => e.Value == M.SummitY).Select(e => e.Key).ToHashSet();
        var mountain = t.Height.Keys.Where(c => { var (r, a) = t.Polar(c.X, c.Z); return r < t.Foot(a); }).ToList();
        var onMountain = mountain.ToHashSet();
        var meadow = t.Height.Where(e => e.Value <= M.GrassY && !onMountain.Contains(e.Key)).Select(e => e.Key).ToList();
        var shares = new (bool, double)[3];
        foreach (int limit in new[] { 1, 2 })
        {
            var seen = Climbable(t, meadow, limit);
            shares[limit] = (summit.Overlaps(seen), (double)mountain.Count(seen.Contains) / mountain.Count);
        }
        int sectors = 0;
        for (int k = 0; k < M.Sectors; k++)
        {
            double a = M.South + k * 2 * Math.PI / M.Sectors;
            double r = t.Foot(a) + 2;
            var start = ((int)Math.Floor(r * Libm.cos(a)), (int)Math.Floor(r * Libm.sin(a)));
            if (summit.Overlaps(Climbable(t, [start], 2, 8)))
                sectors++;
        }
        var spawn = (0, (int)Math.Floor(t.Foot(M.South) + M.SpawnGap));
        return (shares, sectors, FastestClimb(t, spawn));
    }
}

/// <summary>
/// Surface Nets as in NoCubes: every block face between solid and air is a quad, but its
/// corners move off the block corners towards the mean of the surface crossings round them,
/// by Smooth (0: cubes, 1: NoCubes). Corners are shared per kind of block, so ground and
/// leaves never blend.
/// </summary>
public sealed class Mesh
{
    public readonly Terrain T;
    public List<V3> Pos = new(), Nrm = new();
    public List<(double, double, double, double)> Col = new();
    public List<(double U, double V)> Uv = new();
    public List<int> Idx = new();
    public List<((int X, int Y, int Z)? Corner, int Kind)> Keys = new();   // per vertex (corner, kind)
    readonly Dictionary<((int, int, int), int), int> index = new();
    readonly Dictionary<(int, int, int), V3> offsets = new();
    public List<(int, int, int)> Tris = new();     // vertex triples wound into the solid, for the collision
    public HashSet<int> Unsolid = new();            // triangles (by number) only to be seen, not in the collision

    // The dual cell round a block corner: its 8 blocks and the 12 edges between blocks next to each other.
    public static readonly (int, int, int)[] Cell =
        [(0, 0, 0), (0, 0, 1), (0, 1, 0), (0, 1, 1), (1, 0, 0), (1, 0, 1), (1, 1, 0), (1, 1, 1)];
    public static readonly ((int, int, int) A, (int, int, int) B)[] Edges =
        Cell.SelectMany(a => Cell.Where(b => a.CompareTo(b) < 0 && Math.Abs(a.Item1 - b.Item1) + Math.Abs(a.Item2 - b.Item2) + Math.Abs(a.Item3 - b.Item3) == 1)
                                 .Select(b => (a, b))).ToArray();

    // The six directions with their face plane: normal, then the two edge axes u and v
    // so that u x v = normal (corners counter-clockwise from outside).
    static readonly Dictionary<(int, int, int), ((int, int, int) U, (int, int, int) V)> Faces = new()
    {
        [(0, 1, 0)] = ((0, 0, 1), (1, 0, 0)),
        [(0, -1, 0)] = ((1, 0, 0), (0, 0, 1)),
        [(1, 0, 0)] = ((0, 1, 0), (0, 0, 1)),
        [(-1, 0, 0)] = ((0, 0, 1), (0, 1, 0)),
        [(0, 0, 1)] = ((1, 0, 0), (0, 1, 0)),
        [(0, 0, -1)] = ((0, 1, 0), (1, 0, 0)),
    };

    public Mesh(Terrain t) => T = t;

    int[] Kinds(int x, int y, int z) => Cell.Select(c => T.Block(x - 1 + c.Item1, y - 1 + c.Item2, z - 1 + c.Item3)).ToArray();

    // The mean of the crossings on the dual cell's edges (their middles, the density is
    // solid or not) from corner (X, Y, Z); none by a trunk.
    V3 Offset(int x, int y, int z)
    {
        var key = (x, y, z);
        if (!offsets.TryGetValue(key, out V3 o))
        {
            int[] kinds = Kinds(x, y, z);
            double sx = 0, sy = 0, sz = 0;
            int n = 0;
            if (!kinds.Contains(M.Trunk))
                foreach (var (a, b) in Edges)
                    if ((kinds[Array.IndexOf(Cell, a)] == M.Air) != (kinds[Array.IndexOf(Cell, b)] == M.Air))
                    {
                        sx += (a.Item1 + b.Item1) / 2.0 - 0.5;
                        sy += (a.Item2 + b.Item2) / 2.0 - 0.5;
                        sz += (a.Item3 + b.Item3) / 2.0 - 0.5;
                        n++;
                    }
            o = n != 0 ? new V3(sx / n, sy / n, sz / n) : new V3(0.0, 0.0, 0.0);
            offsets[key] = o;
        }
        return o;
    }

    int Vertex((int X, int Y, int Z) corner, int kind)
    {
        var key = (corner, kind);
        if (!index.TryGetValue(key, out int i))
        {
            V3 o = Offset(corner.X, corner.Y, corner.Z);
            i = index[key] = Pos.Count;
            var p = new V3(corner.X + M.Smooth * o.X, corner.Y + M.Smooth * o.Y, corner.Z + M.Smooth * o.Z);
            Pos.Add(kind == M.Ground ? T.Rounded(corner, p) : p);
            Nrm.Add(new V3(0.0, 0.0, 0.0));
            Keys.Add((corner, kind));
        }
        return i;
    }

    void AddNormal(int i, V3 n) => Nrm[i] = new V3(Nrm[i].X + n.X, Nrm[i].Y + n.Y, Nrm[i].Z + n.Z);

    /// <summary>The face of block (x, y, z) towards n, split along the shorter diagonal.</summary>
    public void Face(int x, int y, int z, (int, int, int) n, int kind, bool solid = true)
    {
        var (u, v) = Faces[n];
        double[] centre = [x + 0.5 + 0.5 * n.Item1, y + 0.5 + 0.5 * n.Item2, z + 0.5 + 0.5 * n.Item3];
        int[] uu = [u.Item1, u.Item2, u.Item3], vv = [v.Item1, v.Item2, v.Item3];
        var q = new List<int>();
        foreach (var (su, sv) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
        {
            int C(int i) => Py.Round(centre[i] + 0.5 * su * uu[i] + 0.5 * sv * vv[i]);
            q.Add(Vertex((C(0), C(1), C(2)), kind));
        }
        var p = q.Select(i => Pos[i]).ToArray();
        (int, int, int)[] tris = V3.Dist(p[0], p[2]) <= V3.Dist(p[1], p[3])
            ? [(q[0], q[1], q[2]), (q[0], q[2], q[3])]
            : [(q[0], q[1], q[3]), (q[1], q[2], q[3])];
        foreach (var (a, b, c) in tris)
        {
            // Area-weighted normals, shared by every face round a vertex.
            V3 nn = V3.Cross(V3.Sub(Pos[b], Pos[a]), V3.Sub(Pos[c], Pos[a]));
            AddNormal(a, nn);
            AddNormal(b, nn);
            AddNormal(c, nn);
            if (!solid)
                Unsolid.Add(Idx.Count / 3);
            Idx.AddRange([a, b, c]);
        }
    }

    public static V3 Unit(V3 n)
    {
        double length = Math.Sqrt(Py.Pow(n.X, 2) + Py.Pow(n.Y, 2) + Py.Pow(n.Z, 2));
        if (length == 0)
            length = 1.0;
        return new V3(n.X / length, n.Y / length, n.Z / length);
    }

    /// <summary>Normals to unit length, then what the shader paints at every vertex.</summary>
    public void Finish()
    {
        for (int i = 0; i < Keys.Count; i++)
        {
            Nrm[i] = Unit(Nrm[i]);
            var (weights, shade) = Paint(Keys[i].Corner.Value, Keys[i].Kind);
            Col.Add(weights);
            Uv.Add((shade, 0.0));
        }
    }

    // The ground's weights for TerrainTriplanar (COLOR_0: grass, sand, snow, snow cap;
    // the rest is rock) and the shade (TEXCOORD_0.x).
    ((double, double, double, double), double) Paint((int X, int Y, int Z) corner, int kind)
    {
        var (cx, cy, cz) = corner;
        double tint = 1.0;
        var weights = (0.0, 0.0, 0.0, 0.0);
        if (kind == M.Ground)
        {
            // The column whose top is at this corner, else the highest wall.
            var columns = new List<(int H, (int, int) C)>();
            foreach (int i in new[] { 0, 1 })
                foreach (int k in new[] { 0, 1 })
                {
                    var c = (cx - 1 + i, cz - 1 + k);
                    columns.Add((T.Height.TryGetValue(c, out int h) ? h : -99, c));
                }
            var tops = columns.Where(e => e.H == cy).Select(e => e.C).ToList();
            var column = tops.Count > 0 ? tops[0] : columns.Aggregate((m, e) => e.CompareTo(m) > 0 ? e : m).C;
            int surface = Array.IndexOf(M.Paints, T.Material[column]);
            weights = (surface == 0 ? 1.0 : 0.0, surface == 1 ? 1.0 : 0.0, surface == 2 ? 1.0 : 0.0, surface == 3 ? 1.0 : 0.0);
            if (T.Material[column].Top == M.Stone || T.Material[column].Top == M.Snow)
                tint = 0.95 + 0.05 * M.Lattice(T.Seed + 12, cx, cy, cz) + 0.03 * M.Noise(T.Seed + 8, cx / 5.0, cz / 5.0);
        }
        // Soft AO as with smooth lighting: darker the more of the cell is solid.
        int solid = Kinds(cx, cy, cz).Count(k => k != M.Air);
        return (weights, tint * (1.0 - M.AoStep * Math.Max(0, solid - 4)));
    }

    public void AddVertex(V3 p, V3 n, (double, double, double, double) col, (double, double) uv, int kind)
    {
        Pos.Add(p);
        Nrm.Add(n);
        Col.Add(col);
        Uv.Add(uv);
        Keys.Add((null, kind));
    }

    public static Mesh Build(Terrain t)
    {
        var mesh = new Mesh(t);
        // Ground: the top of every column and its sides down to a lower neighbour, or to
        // one block under the sea at the coast.
        foreach (var (x, z) in t.Height.Keys.Order().ToList())
        {
            int h = t.Height[(x, z)];
            int above = t.Block(x, h, z);
            if (above == M.Air || above == M.Trunk)
                mesh.Face(x, h - 1, z, (0, 1, 0), M.Ground, above == M.Air);
            foreach (var n in new[] { (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1) })
            {
                int nb = t.Height.TryGetValue((x + n.Item1, z + n.Item3), out int hn) ? hn : M.Under;
                for (int y = Math.Max(nb, M.Under); y < h; y++)
                    if (t.Block(x + n.Item1, y, z + n.Item3) == M.Air)
                        mesh.Face(x, y, z, n, M.Ground);
            }
        }
        mesh.Finish();
        var crowns = new List<Action>();
        foreach (var (x, z, top, _, n) in t.Crowns)
            crowns.Add(Trees.Grow(mesh, new PyRandom((long)t.Seed * 1000 + n), x, z, t.Height[(x, z)], top));
        Simplifier.Run(mesh);
        // The leaves after the simplifier and last: they keep their corners and stay out of the collision.
        foreach (var leaves in crowns)
            leaves();
        return mesh;
    }
}

public static class Trees
{
    // A round trunk of TrunkSides from under the ground at base up to the fork at top,
    // widened at the foot like roots and closed above by a dome the limbs grow out of.
    // Shaded as the blocks' corners were: darker where the ground meets it.
    public static void RoundTrunk(Mesh mesh, int x, int z, int bottom, double top)
    {
        double cx = x + 0.5, cz = z + 0.5;
        double dark = 1.0 - M.AoStep;
        var heights = new List<(double Y, double R, double Shade)> { (bottom - 0.3, M.TrunkFoot, dark), (bottom, M.TrunkFoot, dark) };
        foreach (double s in new[] { 0.25, 0.55, 1.0 })
        {
            // Spreads fastest at the ground and runs smoothly into the round.
            double w = Py.Pow(1.0 - s, 2);
            heights.Add((bottom + M.TrunkFlare * s, w * M.TrunkFoot + (1 - w) * M.TrunkRadius, 1.0 - M.AoStep * w));
        }
        heights.AddRange([(top - 0.5, M.TrunkRadius, 1.0), (top, M.TrunkRadius * 0.94, 1.0), (top + 0.25, M.TrunkRadius * 0.72, 0.95), (top + 0.42, M.TrunkRadius * 0.36, 0.9), (top + 0.48, 0.03, 0.9)]);
        int first = mesh.Pos.Count;
        foreach (var (y, r, shade) in heights)
            for (int k = 0; k < M.TrunkSides; k++)
            {
                double a = 2 * Math.PI * k / M.TrunkSides;
                mesh.AddVertex(new V3(cx + r * Libm.cos(a), y, cz + r * Libm.sin(a)), new V3(0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 0.0), (shade, 0.0), M.Trunk);
            }
        for (int ring = 0; ring < heights.Count - 1; ring++)
            for (int k = 0; k < M.TrunkSides; k++)
            {
                int a = first + ring * M.TrunkSides + k;
                int b = first + ring * M.TrunkSides + (k + 1) % M.TrunkSides;
                // Wound as the blocks' faces: counter-clockwise from outside.
                foreach (var tri in new[] { (a, b + M.TrunkSides, b), (a, a + M.TrunkSides, b + M.TrunkSides) })
                {
                    V3 p0 = mesh.Pos[tri.Item1], p1 = mesh.Pos[tri.Item2], p2 = mesh.Pos[tri.Item3];
                    V3 n = V3.Cross(V3.Sub(p1, p0), V3.Sub(p2, p0));
                    foreach (int i in new[] { tri.Item1, tri.Item2, tri.Item3 })
                        mesh.Nrm[i] = new V3(mesh.Nrm[i].X + n.X, mesh.Nrm[i].Y + n.Y, mesh.Nrm[i].Z + n.Z);
                    mesh.Idx.AddRange([tri.Item1, tri.Item2, tri.Item3]);
                }
            }
        for (int i = first; i < mesh.Pos.Count; i++)
            mesh.Nrm[i] = Mesh.Unit(mesh.Nrm[i]);
    }

    static V3 Add(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    static V3 Scale(V3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static V3 Unit(V3 v) => Mesh.Unit(v);

    // Out of the upright by tilt, round towards angle a.
    static V3 Leaning(double tilt, double a) => new(Libm.sin(tilt) * Libm.cos(a), Libm.cos(tilt), Libm.sin(tilt) * Libm.sin(a));

    static V3 RandomUnit(PyRandom rng)
    {
        while (true)
        {
            var v = new V3(rng.Gauss(0, 1), rng.Gauss(0, 1), rng.Gauss(0, 1));
            double length = Math.Sqrt(Dot(v, v));
            if (length > 1e-6)
                return Scale(v, 1 / length);
        }
    }

    /// <summary>
    /// A limb as a tapering tube along the curve from p0 over p1 to p2 (a quadratic Bezier),
    /// its root sunk into what it grows from, its tip closed by a short cone. Returns the
    /// tip and the direction there. Not solid ones are only to be seen.
    /// </summary>
    static (V3 At, V3 Dir) Limb(Mesh mesh, V3 p0, V3 p1, V3 p2, double r0, double r1, bool solid)
    {
        const int sides = 10, rings = 7;
        V3 Point(double t) => Add(Add(Scale(p0, (1 - t) * (1 - t)), Scale(p1, 2 * t * (1 - t))), Scale(p2, t * t));
        V3 Tangent(double t) => Unit(Add(Scale(Sub(p1, p0), 2 * (1 - t)), Scale(Sub(p2, p1), 2 * t)));
        // A frame carried along the curve, so the rings do not twist.
        V3 along = Tangent(0);
        V3 side = Unit(V3.Cross(along, Math.Abs(along.Y) < 0.9 ? new V3(0, 1, 0) : new V3(1, 0, 0)));
        int first = mesh.Pos.Count;
        for (int k = 0; k <= rings; k++)
        {
            double t = (double)k / rings;
            V3 c = Point(t), d = Tangent(t);
            side = Unit(Sub(side, Scale(d, Dot(side, d))));
            V3 up = V3.Cross(d, side);
            double r = r0 + (r1 - r0) * t;
            for (int j = 0; j < sides; j++)
            {
                double a = 2 * Math.PI * j / sides;
                V3 n = Add(Scale(side, Libm.cos(a)), Scale(up, Libm.sin(a)));
                mesh.AddVertex(Add(c, Scale(n, r)), n, (0.0, 0.0, 0.0, 0.0), (0.92, 0.0), M.Trunk);
            }
        }
        V3 tipDir = Tangent(1);
        mesh.AddVertex(Add(p2, Scale(tipDir, r1 * 0.8)), tipDir, (0.0, 0.0, 0.0, 0.0), (0.92, 0.0), M.Trunk);
        int tip = mesh.Pos.Count - 1;
        void Tri(int a, int b, int c)
        {
            // Counter-clockwise from outside: the face's normal along the corners'.
            V3 n = V3.Cross(V3.Sub(mesh.Pos[b], mesh.Pos[a]), V3.Sub(mesh.Pos[c], mesh.Pos[a]));
            if (Dot(n, Add(Add(mesh.Nrm[a], mesh.Nrm[b]), mesh.Nrm[c])) < 0)
                (b, c) = (c, b);
            if (!solid)
                mesh.Unsolid.Add(mesh.Idx.Count / 3);
            mesh.Idx.AddRange([a, b, c]);
        }
        for (int k = 0; k < rings; k++)
            for (int j = 0; j < sides; j++)
            {
                int a = first + k * sides + j, b = first + k * sides + (j + 1) % sides;
                Tri(a, b, b + sides);
                Tri(a, b + sides, a + sides);
            }
        for (int j = 0; j < sides; j++)
            Tri(first + rings * sides + j, first + rings * sides + (j + 1) % sides, tip);
        return (p2, tipDir);
    }

    static V3 Sub(V3 a, V3 b) => V3.Sub(a, b);

    /// <summary>
    /// A tree at column (x, z): trunk up to the fork, Limbs main limbs leaning out all round
    /// and bending up, two thinner ones from each tip; a clump of leaves on every tip and one
    /// on top. Builds the wood now; returns what adds the leaves, after the simplifier.
    /// </summary>
    public static Action Grow(Mesh mesh, PyRandom rng, int x, int z, int bottom, int top)
    {
        double cx = x + 0.5, cz = z + 0.5;
        double fork = bottom + 1.4 + 0.3 * (top - bottom);
        RoundTrunk(mesh, x, z, bottom, fork);
        var clumps = new List<(V3 C, double R)>();
        int count = rng.RandInt(M.Limbs.Min, M.Limbs.Max);
        double turn = rng.Uniform(0.0, 2 * Math.PI);
        for (int i = 0; i < count; i++)
        {
            double a = turn + 2 * Math.PI * i / count + rng.Uniform(-0.3, 0.3);
            double tilt = rng.Uniform(M.LimbTilt.Min, M.LimbTilt.Max);
            double length = rng.Uniform(M.LimbLength.Min, M.LimbLength.Max);
            var root = new V3(cx, fork - 0.15, cz);
            // Leaning out more at the root, bending up towards the tip.
            V3 bend = Add(root, Scale(Leaning(tilt + 0.2, a), length * 0.5));
            V3 end = Add(root, Scale(Leaning(tilt, a), length));
            var (at, dir) = Limb(mesh, root, bend, end, M.LimbRadius.Root, M.LimbRadius.Tip, true);
            clumps.Add((Add(at, new V3(0, 0.25, 0)), rng.Uniform(M.ClumpRadius.Min, M.ClumpRadius.Max)));
            foreach (int s in new[] { -1, 1 })
            {
                double a2 = a + s * rng.Uniform(0.45, 0.85);
                double tilt2 = rng.Uniform(0.5, 0.95);
                double length2 = rng.Uniform(M.BranchLength.Min, M.BranchLength.Max);
                V3 start = Sub(at, Scale(dir, 0.15));
                V3 mid = Add(start, Scale(Leaning(tilt2 + 0.15, a2), length2 * 0.5));
                V3 tip = Add(start, Scale(Leaning(tilt2, a2), length2));
                var (tat, tdir) = Limb(mesh, start, mid, tip, M.BranchRadius.Root, M.BranchRadius.Tip, false);
                clumps.Add((Add(tat, Scale(tdir, 0.3)), rng.Uniform(M.ClumpRadius.Min, M.ClumpRadius.Max)));
            }
        }
        double highest = clumps.Max(c => c.C.Y);
        clumps.Add((new V3(cx + rng.Uniform(-0.4, 0.4), highest + 0.45, cz + rng.Uniform(-0.4, 0.4)), rng.Uniform(1.3, 1.5)));
        // No clump hangs lower than CrownClear (plus its own height) over the ground.
        clumps = [.. clumps.Select(c => (new V3(c.C.X, Math.Max(c.C.Y, bottom + M.CrownClear + 0.8 * c.R), c.C.Z), c.R))];
        long seed = rng.RandRange(1L << 30);
        return () => Leaves(mesh, new PyRandom(seed), clumps);
    }

    // Leaves.png: four leaves in a 2 x 2 atlas of 1024 px, along x, LeafFill of their quarter
    // long, LeafWidth of that wide (Textures.cs); a quad takes its leaf with a little margin.
    // LeafClump.png: three painted clumps and a twig (tip up) in its quarters.
    const double AtlasSize = 1024, LeafPx = 512 * 0.94, LeafHalfPx = 512 * 0.94 * 0.43 / 2 + 10, LeafMarginPx = 4;
    const int ClumpCard = 0, SprigCard = 1, SingleLeaf = 2;

    /// <summary>
    /// The leaves of a crown, in every clump (an ellipsoid 0.8 as high as wide): cards with
    /// painted clumps inside, here and there a twig card standing out (Luca), single leaves
    /// in the outer shell; none deep inside another clump. Lit as the clump and the crown
    /// are round (their directions out, a little the leaf's own), darker deeper in and lower
    /// down. Every quad wound both ways (Stride turns a back face's normal round). COLOR_0:
    /// shade, tint, sway (0 where it is held, 1 at the free end), phase.
    /// </summary>
    static void Leaves(Mesh mesh, PyRandom rng, List<(V3 C, double R)> clumps)
    {
        var centre = Scale(clumps.Aggregate(new V3(0, 0, 0), (s, c) => Add(s, c.C)), 1.0 / clumps.Count);
        double low = clumps.Min(c => c.C.Y - 0.8 * c.R), high = clumps.Max(c => c.C.Y + 0.8 * c.R);
        foreach (var (c, r) in clumps)
        foreach (int kind in new[] { ClumpCard, SprigCard, SingleLeaf })
        {
            var radii = new V3(r, 0.8 * r, r);
            double area = 4 * Math.PI * r * r * 0.87;
            int n = (int)Math.Round(area * (kind == ClumpCard ? M.CardDensity : kind == SprigCard ? M.SprigDensity : M.LeafDensity));
            var (inner, outer) = kind == ClumpCard ? M.CardDepth : kind == SprigCard ? M.SprigDepth : M.LeafDepth;
            for (int i = 0; i < n; i++)
            {
                V3 dir = RandomUnit(rng);
                double u = rng.Random();
                // The single leaves crowd towards the outside.
                double depth = kind == SingleLeaf ? outer - (outer - inner) * u * u : inner + (outer - inner) * u;
                var p = new V3(c.X + dir.X * radii.X * depth, c.Y + dir.Y * radii.Y * depth, c.Z + dir.Z * radii.Z * depth);
                V3 spin = RandomUnit(rng);
                double turn = rng.Uniform(-0.6, 0.6);
                var (smallest, largest) = kind == ClumpCard ? M.CardSize : kind == SprigCard ? M.SprigLength : M.LeafLength;
                double size = rng.Uniform(smallest, largest);
                int quarter = (int)rng.RandRange(kind == SingleLeaf ? 4 : 3);
                bool mirror = rng.Random() < 0.5;
                double tint = rng.Random(), phase = rng.Random();
                // Hidden inside another clump: left out.
                if (clumps.Any(o => o.C != c && Math.Pow((p.X - o.C.X) / o.R, 2) + Math.Pow((p.Y - o.C.Y) / (0.8 * o.R), 2) + Math.Pow((p.Z - o.C.Z) / o.R, 2) < 0.6))
                    continue;
                V3 outward = Unit(new V3(dir.X / radii.X, dir.Y / radii.Y, dir.Z / radii.Z));
                V3 crownOut = Unit(new V3((p.X - centre.X) / 1.0, (p.Y - centre.Y) / 0.7, (p.Z - centre.Z) / 1.0));
                V3 normal = Unit(Add(Add(Scale(outward, 0.45), Scale(crownOut, 0.3)), Scale(Unit(Add(outward, Scale(spin, 0.5))), 0.25)));
                double height = Math.Clamp((p.Y - low) / (high - low), 0, 1);
                double inside = Math.Clamp((depth - M.CardDepth.Min) / (M.LeafDepth.Max - M.CardDepth.Min), 0, 1);
                double shade = (0.55 + 0.45 * inside) * (0.68 + 0.32 * height);
                V3 face, along, across;
                (double U, double V)[] uvs;
                double length, width;
                V3 start;
                if (kind == ClumpCard)
                {
                    // A card facing out, turned at random about its face.
                    face = Unit(Add(outward, Scale(spin, 0.5)));
                    V3 any = Math.Abs(face.Y) < 0.9 ? new V3(0, 1, 0) : new V3(1, 0, 0);
                    V3 a0 = Unit(V3.Cross(face, any)), b0 = V3.Cross(face, a0);
                    double ang = rng.Uniform(0, 2 * Math.PI);
                    along = Add(Scale(a0, Libm.cos(ang)), Scale(b0, Libm.sin(ang)));
                    across = V3.Cross(face, along);
                    double u0 = (quarter % 2) * 0.5 + 2 / AtlasSize, v0 = (quarter / 2) * 0.5 + 2 / AtlasSize, w = 0.5 - 4 / AtlasSize;
                    uvs = [(u0, v0), (u0 + w, v0), (u0 + w, v0 + w), (u0, v0 + w)];
                    length = width = size;
                    start = Sub(p, Scale(along, length / 2));
                }
                else if (kind == SprigCard)
                {
                    // Out of the clump and to a side, a third of it inside; its base (v 1) held.
                    along = Unit(Add(outward, Scale(spin, 0.9)));
                    across = Unit(V3.Cross(along, RandomUnit(rng)));
                    face = V3.Cross(along, across);
                    double u0 = 0.5 + 2 / AtlasSize, w = 0.5 - 4 / AtlasSize;
                    if (mirror)
                        uvs = [(u0 + w, 0.5 + w), (u0 + w, 0.5 + 2 / AtlasSize), (u0, 0.5 + 2 / AtlasSize), (u0, 0.5 + w)];
                    else
                        uvs = [(u0, 0.5 + w), (u0, 0.5 + 2 / AtlasSize), (u0 + w, 0.5 + 2 / AtlasSize), (u0 + w, 0.5 + w)];
                    length = size;
                    width = 0.8 * size;
                    start = Sub(p, Scale(along, length / 3));
                }
                else
                {
                    // Stem in, tip out and to a side; its face turned towards the outside.
                    along = Unit(Add(Scale(outward, 0.7), Scale(spin, 0.7)));
                    V3 flat = Sub(outward, Scale(along, Dot(outward, along)));
                    face = Dot(flat, flat) > 1e-6 ? Unit(flat) : Unit(V3.Cross(along, spin));
                    face = Add(Scale(face, Libm.cos(turn)), Scale(V3.Cross(along, face), Libm.sin(turn)));
                    across = V3.Cross(face, along);
                    double ox = (quarter % 2) * 512 + (512 - LeafPx) / 2, oy = (quarter / 2) * 512 + 256;
                    double u0 = (ox - LeafMarginPx) / AtlasSize, u1 = (ox + LeafPx + LeafMarginPx) / AtlasSize;
                    double va = (oy - LeafHalfPx) / AtlasSize, vb = (oy + LeafHalfPx) / AtlasSize;
                    if (mirror)
                        (va, vb) = (vb, va);
                    uvs = [(u0, va), (u1, va), (u1, vb), (u0, vb)];
                    length = size * (LeafPx + 2 * LeafMarginPx) / LeafPx;
                    width = length * 2 * LeafHalfPx / (LeafPx + 2 * LeafMarginPx);
                    start = Sub(p, Scale(along, length * 0.3));
                }
                V3[] quad =
                [
                    Add(start, Scale(across, -width / 2)), Add(Add(start, Scale(along, length)), Scale(across, -width / 2)),
                    Add(Add(start, Scale(along, length)), Scale(across, width / 2)), Add(start, Scale(across, width / 2)),
                ];
                int at = mesh.Pos.Count;
                for (int k = 0; k < 4; k++)
                {
                    double sway = kind == ClumpCard ? 1.0 : (k == 1 || k == 2 ? 1.0 : 0.0);
                    mesh.AddVertex(quad[k], normal, (shade, tint, sway, phase), uvs[k], kind == SingleLeaf ? M.Leaf : M.Clump);
                }
                foreach (var (a, b, d) in new[] { (at, at + 1, at + 2), (at, at + 2, at + 3), (at, at + 2, at + 1), (at, at + 3, at + 2) })
                    mesh.Idx.AddRange([a, b, d]);
            }
        }
    }
}

public static class Simplifier
{
    // The plane through a, b, c as the ten terms of its error quadric.
    static double[] Quadric(V3 a, V3 b, V3 c)
    {
        V3 n = V3.Cross(V3.Sub(b, a), V3.Sub(c, a));
        double length = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
        if (length == 0)
            return new double[10];
        double x = n.X / length, y = n.Y / length, z = n.Z / length;
        double d = -(x * a.X + y * a.Y + z * a.Z);
        return [x * x, x * y, x * z, x * d, y * y, y * z, y * d, z * z, z * d, d * d];
    }

    static double Error(double[] q, V3 p)
    {
        double x = p.X, y = p.Y, z = p.Z;
        return q[0] * x * x + 2 * q[1] * x * y + 2 * q[2] * x * z + 2 * q[3] * x + q[4] * y * y
            + 2 * q[5] * y * z + 2 * q[6] * y + q[7] * z * z + 2 * q[8] * z + q[9];
    }

    /// <summary>
    /// Fewer triangles where they change nothing: half-edge collapses (a vertex moves onto a
    /// neighbour, so every kept vertex keeps its place, colour and normal) by the planes they
    /// bend, while every original face stays within Simplify. Borders stay; the collision gets
    /// the same faces.
    /// </summary>
    public static void Run(Mesh mesh)
    {
        var pos = mesh.Pos;
        var col = mesh.Col;
        var nrm = mesh.Nrm;
        var uv = mesh.Uv;
        var faces = new List<int[]>();
        for (int i = 0; i < mesh.Idx.Count; i += 3)
            faces.Add([mesh.Idx[i], mesh.Idx[i + 1], mesh.Idx[i + 2]]);
        var around = pos.Select(_ => new HashSet<int>()).ToList();
        var quad = pos.Select(_ => new double[10]).ToList();
        var edges = new Dictionary<(int, int), int>();
        for (int f = 0; f < faces.Count; f++)
        {
            int a = faces[f][0], b = faces[f][1], c = faces[f][2];
            double[] q = Quadric(pos[a], pos[b], pos[c]);
            foreach (int v in new[] { a, b, c })
            {
                around[v].Add(f);
                quad[v] = [.. Enumerable.Range(0, 10).Select(k => quad[v][k] + q[k])];
            }
            foreach (var (e0, e1) in new[] { (a, b), (b, c), (c, a) })
            {
                var key = (Math.Min(e0, e1), Math.Max(e0, e1));
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }
        var border = new HashSet<int>();
        foreach (var ((a, b), n) in edges)
            if (n != 2)
            {
                border.Add(a);
                border.Add(b);
            }
        // Faces only to be seen keep their corners: no collision face shrinks under them.
        foreach (int f in mesh.Unsolid)
            border.UnionWith(faces[f]);
        var alive = Enumerable.Repeat(true, faces.Count).ToArray();
        var gone = new bool[pos.Count];
        var version = new int[pos.Count];
        double limit = M.Simplify * M.Simplify;
        var heap = new PriorityQueue<(double, int, int, int, int), (double, int, int, int, int)>();

        HashSet<int> Neighbours(int v)
        {
            var set = new HashSet<int>();
            foreach (int f in around[v])
                set.UnionWith(faces[f]);
            set.Remove(v);
            return set;
        }

        bool Similar(int u, int v)
        {
            var (a0, a1, a2, a3) = col[u];
            var (b0, b1, b2, b3) = col[v];
            double worst = Py.Max(Py.Max(Py.Max(Math.Abs(a0 - b0), Math.Abs(a1 - b1)), Math.Abs(a2 - b2)), Math.Abs(a3 - b3));
            return worst <= M.SimplifyColour
                && Math.Abs(uv[u].U - uv[v].U) <= M.SimplifyColour
                && Py.Sum([nrm[u].X * nrm[v].X, nrm[u].Y * nrm[v].Y, nrm[u].Z * nrm[v].Z]) >= M.SimplifyNormal;
        }

        void Offer(int u, int v)
        {
            // u goes only if it matches every neighbour: then no stretched face carries a
            // far colour or normal over where it was.
            if (border.Contains(u) || !Neighbours(u).All(w => Similar(u, w)))
                return;
            double cost = Error([.. Enumerable.Range(0, 10).Select(k => quad[u][k] + quad[v][k])], pos[v]);
            if (cost <= limit)
            {
                var item = (cost, u, v, version[u], version[v]);
                heap.Enqueue(item, item);
            }
        }

        foreach (var (a, b) in edges.Keys.Order().ToList())
        {
            Offer(a, b);
            Offer(b, a);
        }
        while (heap.Count > 0)
        {
            var (_, u, v, vu, vv) = heap.Dequeue();
            if (gone[u] || gone[v] || version[u] != vu || version[v] != vv)
                continue;
            var shared = around[u].Where(f => faces[f].Contains(v)).ToList();
            if (shared.Count != 2)
                continue;
            var common = Neighbours(u);
            common.IntersectWith(Neighbours(v));
            if (common.Count != 2)
                continue;
            // No face round u may fold over or shrink to nothing when u moves to v.
            bool ok = true;
            foreach (int f in around[u])
            {
                if (faces[f].Contains(v))
                    continue;
                V3[] old = [.. faces[f].Select(w => pos[w])];
                V3[] moved = [.. faces[f].Select(w => w == u ? pos[v] : pos[w])];
                V3 n0 = V3.Cross(V3.Sub(old[1], old[0]), V3.Sub(old[2], old[0]));
                V3 n1 = V3.Cross(V3.Sub(moved[1], moved[0]), V3.Sub(moved[2], moved[0]));
                double l0 = Math.Sqrt(Py.Sum([n0.X * n0.X, n0.Y * n0.Y, n0.Z * n0.Z]));
                double l1 = Math.Sqrt(Py.Sum([n1.X * n1.X, n1.Y * n1.Y, n1.Z * n1.Z]));
                if (l1 < 1e-6 || Py.Sum([n0.X * n1.X, n0.Y * n1.Y, n0.Z * n1.Z]) < 0.5 * l0 * l1)
                {
                    ok = false;
                    break;
                }
            }
            if (!ok)
                continue;
            foreach (int f in shared)
            {
                alive[f] = false;
                foreach (int w in faces[f])
                    around[w].Remove(f);
            }
            foreach (int f in around[u])
            {
                faces[f] = [.. faces[f].Select(w => w == u ? v : w)];
                around[v].Add(f);
            }
            around[u] = new HashSet<int>();
            gone[u] = true;
            quad[v] = [.. Enumerable.Range(0, 10).Select(k => quad[u][k] + quad[v][k])];
            version[v]++;
            foreach (int w in Neighbours(v).Order().ToList())
            {
                Offer(v, w);
                Offer(w, v);
            }
        }
        // Only the vertices still in use, in their old order.
        var keep = Enumerable.Range(0, faces.Count).Where(f => alive[f]).SelectMany(f => faces[f]).Distinct().Order().ToList();
        var renumber = new Dictionary<int, int>();
        for (int i = 0; i < keep.Count; i++)
            renumber[keep[i]] = i;
        mesh.Pos = [.. keep.Select(i => pos[i])];
        mesh.Col = [.. keep.Select(i => col[i])];
        mesh.Uv = [.. keep.Select(i => uv[i])];
        mesh.Nrm = [.. keep.Select(i => nrm[i])];
        mesh.Keys = [.. keep.Select(i => mesh.Keys[i])];
        mesh.Idx = [.. Enumerable.Range(0, faces.Count).Where(f => alive[f]).SelectMany(f => faces[f].Select(w => renumber[w]))];
        mesh.Tris = [.. Enumerable.Range(0, faces.Count).Where(f => alive[f] && !mesh.Unsolid.Contains(f))
            .Select(f => (renumber[faces[f][0]], renumber[faces[f][2]], renumber[faces[f][1]]))];
    }
}

public static class Output
{
    // Share of the ground's area (seen from above) flatter than the motor's slope limit,
    // where one stands without sliding.
    public static double Slopes(Mesh mesh)
    {
        double flat = 0.0, total = 0.0;
        double limit = Libm.cos(M.SlopeLimit * (Math.PI / 180.0));
        foreach (var (a, c, b) in mesh.Tris)
        {
            V3 n = V3.Cross(V3.Sub(mesh.Pos[b], mesh.Pos[a]), V3.Sub(mesh.Pos[c], mesh.Pos[a]));
            double length = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
            if (length == 0 || n.Y <= 0)
                continue;
            total += n.Y / 2;
            if (n.Y / length > limit)
                flat += n.Y / 2;
        }
        return flat / total;
    }

    public sealed record Part(string Name, List<V3> Pos, List<V3> Nrm, List<int> Idx,
                              List<(double, double, double, double)> Col, List<(double, double)> Uv);

    /// <summary>
    /// A glTF binary: one mesh with a primitive per part (material name, positions, normals,
    /// indices, COLOR_0 or none, TEXCOORD_0 or none); the model asset maps the materials by
    /// name. The JSON is laid out as Python's json.dumps did it.
    /// </summary>
    public static void WriteGlb(string path, IEnumerable<Part> parts)
    {
        var blob = new MemoryStream();
        var views = new List<string>();
        var accessors = new List<string>();
        var primitives = new List<string>();
        var materials = new List<string>();
        int Add(byte[] data, int target, string accessor)
        {
            views.Add($"{{\"buffer\":0,\"byteOffset\":{blob.Length},\"byteLength\":{data.Length},\"target\":{target}}}");
            blob.Write(data);
            accessors.Add(accessor[..^1] + $",\"bufferView\":{views.Count - 1}}}");
            return accessors.Count - 1;
        }
        static byte[] Floats(IEnumerable<double> values)
        {
            var output = new MemoryStream();
            var w = new BinaryWriter(output);
            foreach (double v in values)
                w.Write((float)v);
            return output.ToArray();
        }
        foreach (var part in parts)
        {
            int n = part.Pos.Count;
            string Bounds(Func<V3, double, double, double> pick) => "[" + string.Join(",", Enumerable.Range(0, 3).Select(i =>
                Py.Repr(part.Pos.Select(p => p[i]).Aggregate((m, v) => pick(default, m, v))))) + "]";
            string min = Bounds((_, m, v) => Py.Min(m, v)), max = Bounds((_, m, v) => Py.Max(m, v));
            var attributes = new List<string>
            {
                $"\"POSITION\":{Add(Floats(part.Pos.SelectMany(p => new[] { p.X, p.Y, p.Z })), 34962, $"{{\"componentType\":5126,\"count\":{n},\"type\":\"VEC3\",\"min\":{min},\"max\":{max}}}")}",
                $"\"NORMAL\":{Add(Floats(part.Nrm.SelectMany(p => new[] { p.X, p.Y, p.Z })), 34962, $"{{\"componentType\":5126,\"count\":{n},\"type\":\"VEC3\"}}")}",
            };
            if (part.Col is { Count: > 0 })
                attributes.Add($"\"COLOR_0\":{Add(Floats(part.Col.SelectMany(c => new[] { c.Item1, c.Item2, c.Item3, c.Item4 })), 34962, $"{{\"componentType\":5126,\"count\":{n},\"type\":\"VEC4\"}}")}");
            if (part.Uv is { Count: > 0 })
                attributes.Add($"\"TEXCOORD_0\":{Add(Floats(part.Uv.SelectMany(c => new[] { c.Item1, c.Item2 })), 34962, $"{{\"componentType\":5126,\"count\":{n},\"type\":\"VEC2\"}}")}");
            var indexBytes = new byte[part.Idx.Count * 4];
            for (int i = 0; i < part.Idx.Count; i++)
                BitConverter.TryWriteBytes(indexBytes.AsSpan(4 * i), (uint)part.Idx[i]);
            int indices = Add(indexBytes, 34963, $"{{\"componentType\":5125,\"count\":{part.Idx.Count},\"type\":\"SCALAR\"}}");
            primitives.Add($"{{\"attributes\":{{{string.Join(",", attributes)}}},\"indices\":{indices},\"material\":{materials.Count}}}");
            // Stride's import merges materials that differ only by name: tell them apart.
            materials.Add($"{{\"name\":\"{part.Name}\",\"pbrMetallicRoughness\":{{\"metallicFactor\":0.0,\"roughnessFactor\":{Py.Repr(0.9 - 0.01 * materials.Count)}}}}}");
        }
        string name = Path.GetFileNameWithoutExtension(path);
        string json = "{\"asset\":{\"version\":\"2.0\",\"generator\":\"KotH Tools/maps/Mountain.cs\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}]," +
                      $"\"nodes\":[{{\"name\":\"{name}\",\"mesh\":0}}],\"meshes\":[{{\"name\":\"{name}\",\"primitives\":[{string.Join(",", primitives)}]}}]," +
                      $"\"materials\":[{string.Join(",", materials)}],\"buffers\":[{{\"byteLength\":{blob.Length}}}]," +
                      $"\"bufferViews\":[{string.Join(",", views)}],\"accessors\":[{string.Join(",", accessors)}]}}";
        Glb.Write(path, json, blob.ToArray());
    }

    // The mesh split by kind, each with only its own vertices; the single leaves and the
    // clump cards each in their own material.
    public static List<Part> Parts(Mesh mesh)
    {
        var output = new List<Part>();
        foreach (var (kinds, name) in new[] { (new[] { M.Ground }, "Terrain"), (new[] { M.Leaf }, "Leaves"), (new[] { M.Trunk }, "Bark"), (new[] { M.Clump }, "LeafCards") })
        {
            var faces = new List<int[]>();
            for (int i = 0; i < mesh.Idx.Count; i += 3)
                if (kinds.Contains(mesh.Keys[mesh.Idx[i]].Kind))
                    faces.Add([mesh.Idx[i], mesh.Idx[i + 1], mesh.Idx[i + 2]]);
            var used = faces.SelectMany(f => f).Distinct().Order().ToList();
            var renumber = new Dictionary<int, int>();
            for (int i = 0; i < used.Count; i++)
                renumber[used[i]] = i;
            var uv = used.Select(v => mesh.Uv[v]).ToList();
            output.Add(new Part(name, [.. used.Select(v => mesh.Pos[v])], [.. used.Select(v => mesh.Nrm[v])],
                [.. faces.SelectMany(f => f.Select(v => renumber[v]))], [.. used.Select(v => mesh.Col[v])], [.. uv.Select(u => (u.U, u.V))]));
        }
        return output;
    }

    /// <summary>
    /// The sea round the island and out to SeaReach, with a hole where the island stands:
    /// overlapping it, the far depth buffer lost the meadow under the water. Flat at the
    /// entity's height (NeuVisual puts it at the water's height).
    /// </summary>
    public static Part Sea(Terrain t)
    {
        var pos = new List<V3>();
        var idx = new List<int>();
        void Rect(double x0, double z0, double x1, double z1)
        {
            int b = pos.Count;
            pos.AddRange([new V3(x0, 0.0, z0), new V3(x0, 0.0, z1), new V3(x1, 0.0, z1), new V3(x1, 0.0, z0)]);
            idx.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }
        // The hole keeps SeaUnder inside the coast line, so the sea reaches in under the
        // rounded coast wall, else one saw past it into the void.
        bool Hole(int x, int z)
        {
            var (r, a) = t.Polar(x, z);
            return r + 0.71 <= t.Coast(a) - M.SeaUnder;
        }
        for (int z = -M.Half; z < M.Half; z++)
        {
            int x = -M.Half;
            while (x < M.Half)
            {
                if (Hole(x, z))
                {
                    x++;
                    continue;
                }
                int end = x;
                while (end < M.Half && !Hole(end, z))
                    end++;
                Rect(x, z, end, z + 1);
                x = end;
            }
        }
        // Outside, tiles growing outwards to SeaReach: four 30-km rectangles came past
        // the far plane with a depth that jumped every frame, and the coastal fog on the
        // water flickered with it.
        var cuts = new List<double> { M.Half };
        while (cuts[^1] < M.SeaReach)
            cuts.Add(Py.Min(M.SeaReach, cuts[^1] * M.SeaGrowth));
        var lines = cuts.AsEnumerable().Reverse().Select(c => -c).Concat(cuts).ToList();
        for (int j = 0; j + 1 < lines.Count; j++)
            for (int i = 0; i + 1 < lines.Count; i++)
            {
                double x0 = lines[i], x1 = lines[i + 1], z0 = lines[j], z1 = lines[j + 1];
                if (!(-M.Half <= x0 && x1 <= M.Half && -M.Half <= z0 && z1 <= M.Half))
                    Rect(x0, z0, x1, z1);
            }
        return new Part("Water", pos, [.. pos.Select(_ => new V3(0.0, 1.0, 0.0))], idx, null, null);
    }

    /// <summary>
    /// The drawn triangles for the physics, compressed: the vertices, then per Chunk x Chunk
    /// chunk (by centroid, in order) its triangles as vertex triples wound into the solid,
    /// as Bepu's triangles take them. The game puts each chunk into one static compound.
    /// </summary>
    public static SortedDictionary<(int, int), List<(int, int, int)>> WriteCollision(Mesh mesh, string path)
    {
        var chunks = new SortedDictionary<(int, int), List<(int, int, int)>>();
        foreach (var tri in mesh.Tris)
        {
            int[] corners = [tri.Item1, tri.Item2, tri.Item3];
            double cx = Py.Sum(corners.Select(i => mesh.Pos[i].X)) / 3;
            double cz = Py.Sum(corners.Select(i => mesh.Pos[i].Z)) / 3;
            var key = ((int)Math.Floor(cx / M.Chunk), (int)Math.Floor(cz / M.Chunk));
            if (!chunks.TryGetValue(key, out var list))
                chunks[key] = list = new List<(int, int, int)>();
            list.Add(tri);
        }
        // The leaves come last (Build) and are not solid.
        static bool Leafy(int kind) => kind == M.Leaf || kind == M.Clump;
        int solid = mesh.Keys.Count(k => !Leafy(k.Kind));
        if (mesh.Keys.Skip(solid).Any(k => !Leafy(k.Kind)))
            throw new Exception("Blätter nicht am Ende");
        var raw = new MemoryStream();
        var w = new BinaryWriter(raw);
        w.Write("KTRN"u8);
        w.Write(1u);
        w.Write((uint)solid);
        foreach (V3 p in mesh.Pos.Take(solid))
        {
            w.Write((float)p.X);
            w.Write((float)p.Y);
            w.Write((float)p.Z);
        }
        w.Write((uint)chunks.Count);
        foreach (var (key, tris) in chunks)
        {
            w.Write(key.Item1);
            w.Write(key.Item2);
            w.Write((uint)tris.Count);
            foreach (var (a, b, c) in tris)
            {
                w.Write((uint)a);
                w.Write((uint)b);
                w.Write((uint)c);
            }
        }
        w.Flush();
        using var file = File.Create(path);
        using var z = new ZLibStream(file, CompressionLevel.SmallestSize);
        z.Write(raw.GetBuffer(), 0, (int)raw.Length);
        return chunks;
    }

    // The picture from above.
    public static void WritePng(Terrain t, string path)
    {
        int size = 2 * M.Half;
        var png = new Png(size, size, 3);
        int at = 0;
        for (int z = -M.Half; z < M.Half; z++)
            for (int x = -M.Half; x < M.Half; x++)
            {
                Rgb c;
                if (!t.Height.TryGetValue((x, z), out int h))
                    c = new Rgb(0.25, 0.45, 0.75);
                else
                {
                    c = t.Material[(x, z)].Top;
                    // Light from the north-west, from the height steps.
                    int west = t.Height.TryGetValue((x - 1, z), out int hw) ? hw : h;
                    int north = t.Height.TryGetValue((x, z - 1), out int hn) ? hn : h;
                    double k = Py.Max(0.45, Py.Min(1.25, 1.0 + 0.12 * ((h - west) + (h - north))));
                    c = new Rgb(Py.Min(1.0, c.R * k), Py.Min(1.0, c.G * k), Py.Min(1.0, c.B * k));
                }
                if (Enumerable.Range(M.GrassY, 9).Any(y => t.Trees.Contains((x, y, z))))
                    c = M.Leaves;
                png.Pixels[at++] = (byte)(int)(c.R * 255);
                png.Pixels[at++] = (byte)(int)(c.G * 255);
                png.Pixels[at++] = (byte)(int)(c.B * 255);
            }
        png.Write(path);
    }
}
