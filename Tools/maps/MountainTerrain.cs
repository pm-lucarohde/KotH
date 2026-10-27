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
    public static double CrownStep = 0.35;      // crowns are soft lobes run together (Luca), meshed this fine
    public static double CrownMelt = 0.6;       // metres over which the lobes run together
    public static double CrownNoise = 0.12;     // lumps on the lobes, metres
    public static double CrownWidth = 1.1;      // crowns this much wider than the lobes say (Luca: 10 % more)
    public static double CrownBlocky = 0.5;     // share of the old leaf blocks in the crown's shape (Luca)
    public static double CrownClear = 2.3;      // undersides stay this high over the ground: bodies (1.8 m) wedged under low rims
    public static double CrownFloorRound = 0.4; // metres over which the cut underside rounds into the lobes
    public static int Cards = 200;              // twigs standing out of every crown, evenly all round (Luca)
    public static readonly (double Min, double Max) CardLength = (0.72, 1.035);  // metres, a third of it inside the crown
    // The leaves are cut out of their texture (Luca); the crown's surface this far
    // in, this much darker, fills it behind the gaps. Only drawn, not in the collision.
    public static readonly (double Depth, double Dark)[] LeafShells = [(0.3, 0.8), (0.6, 0.62)];

    // What a block is; -1 is air.
    public const int Air = -1, Ground = 0, Foliage = 1, Trunk = 2, Card = 3, Shell = 4;

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
        if (Trees.Contains((x, y, z)))
            return M.Foliage;
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
        var shells = new List<Action>();
        foreach (var (x, z, top, blocks, n) in t.Crowns)
        {
            int bottom = t.Height[(x, z)];
            // The trunk reaches into the crown's middle.
            Trees.RoundTrunk(mesh, x, z, bottom, top - 1.3);
            shells.Add(Trees.RoundCrown(mesh, new PyRandom((long)t.Seed * 1000 + n), x, z, bottom, top, blocks));
        }
        // The shells last: everything before keeps its numbers, the collision its bytes.
        foreach (var shell in shells)
            shell();
        Simplifier.Run(mesh);
        return mesh;
    }
}

public static class Trees
{
    // A round trunk of TrunkSides from under the ground at base into the leaves at top,
    // widened at the foot like roots. Shaded as the blocks' corners were: darker where
    // ground or leaves meet it.
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
        heights.AddRange([(top - 0.5, M.TrunkRadius, 1.0), (top, M.TrunkRadius, dark), (top + 0.3, M.TrunkRadius, dark)]);
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

    // A body round the trunk's top and 6 to 8 smaller lobes round it, one on top, each
    // as (centre, radii) of an ellipsoid as it shows on its own.
    static List<(V3 C, V3 R)> CrownLobes(PyRandom rng, int x, int z, int top)
    {
        double cx = x + 0.5 + rng.Uniform(-0.25, 0.25);
        double cz = z + 0.5 + rng.Uniform(-0.25, 0.25);
        double cy = top - 1.0;
        var lobes = new List<(V3 C, V3 R)> { (new V3(cx, cy, cz), new V3(2.1, 1.5, 2.1)) };
        int count = rng.RandInt(6, 8);
        double turn = rng.Uniform(0.0, 2 * Math.PI);
        for (int i = 0; i < count; i++)
        {
            // Some hang lower at the rim, some sit up on the shoulders.
            double a = turn + 2 * Math.PI * i / count + rng.Uniform(-0.35, 0.35);
            double up = rng.Uniform(-0.35, 0.8);
            double d = rng.Uniform(1.5, 2.0);
            double r = rng.Uniform(0.9, 1.3);
            var centre = new V3(cx + d * Libm.cos(up) * Libm.cos(a), cy + 0.8 * d * Libm.sin(up), cz + d * Libm.cos(up) * Libm.sin(a));
            lobes.Add((centre, new V3(r, 0.85 * r, r)));
        }
        double tx = cx + rng.Uniform(-0.5, 0.5);
        double tz = cz + rng.Uniform(-0.5, 0.5);
        lobes.Add((new V3(tx, cy + 1.3, tz), new V3(1.2, 0.95, 1.2)));
        double w = M.CrownWidth;
        return lobes.Select(l => (new V3(cx + (l.C.X - cx) * w, l.C.Y, cz + (l.C.Z - cz) * w), new V3(l.R.X * w, l.R.Y, l.R.Z * w))).ToList();
    }

    static double MinOf(V3 r) => Py.Min(Py.Min(r.X, r.Y), r.Z);

    /// <summary>
    /// A crown as one smooth surface (Surface Nets, CrownStep), CrownBlocky of the way from
    /// the lobes (run together over CrownMelt, a little noise for lumps) to the old leaf
    /// blocks. Normals from the field, lighter on top; solid like the leaf blocks were.
    /// Returns what adds its inner shells.
    /// </summary>
    public static Action RoundCrown(Mesh mesh, PyRandom rng, int x, int z, int bottom, int top, List<(int X, int Y, int Z)> blocks)
    {
        var lobes = CrownLobes(rng, x, z, top);
        long seed = rng.RandRange(1L << 30);

        double Field(V3 p)
        {
            // Into the lobes: an ellipsoid's distance roughly, smooth minimum.
            double near = 0;
            bool first = true;
            foreach (var (c, r) in lobes)
            {
                double q = Math.Sqrt(Py.Sum([Py.Pow((p.X - c.X) / r.X, 2), Py.Pow((p.Y - c.Y) / r.Y, 2), Py.Pow((p.Z - c.Z) / r.Z, 2)]));
                double d = (q - 1.0) * MinOf(r);
                if (first)
                {
                    near = d;
                    first = false;
                }
                else
                {
                    double h = Py.Max(M.CrownMelt - Math.Abs(near - d), 0.0) / M.CrownMelt;
                    near = Py.Min(near, d) - h * h * M.CrownMelt / 4;
                }
            }
            double rounded = -near + M.CrownNoise * M.Noise3(seed, p.X / 0.9, p.Y / 0.9, p.Z / 0.9);
            // Into the blocks: the nearest block's box distance.
            double box = 0;
            bool any = false;
            foreach (var b in blocks)
            {
                double o0 = Math.Abs(p.X - b.X - 0.5) - 0.5, o1 = Math.Abs(p.Y - b.Y - 0.5) - 0.5, o2 = Math.Abs(p.Z - b.Z - 0.5) - 0.5;
                double big = Py.Max(Py.Max(o0, o1), o2);
                double d = Math.Sqrt(Py.Sum([Py.Pow(Py.Max(o0, 0.0), 2), Py.Pow(Py.Max(o1, 0.0), 2), Py.Pow(Py.Max(o2, 0.0), 2)])) + Py.Min(big, 0.0);
                box = any ? Py.Min(box, d) : d;
                any = true;
            }
            double crown = (1 - M.CrownBlocky) * rounded - M.CrownBlocky * box;
            // Cut flat CrownClear over the ground, rounded like the lobes melt.
            double above = p.Y - bottom - M.CrownClear;
            double hh = Py.Max(M.CrownFloorRound - Math.Abs(crown - above), 0.0) / M.CrownFloorRound;
            return Py.Min(crown, above) - hh * hh * M.CrownFloorRound / 4;
        }

        double[] lo = new double[3], hi = new double[3];
        for (int k = 0; k < 3; k++)
        {
            double lobeLo = lobes.Select(l => l.C[k] - l.R[k]).Aggregate((m, v) => Py.Min(m, v));
            int blockLo = blocks.Select(b => k == 0 ? b.X : k == 1 ? b.Y : b.Z).Aggregate((m, v) => v < m ? v : m);
            lo[k] = Py.Min(lobeLo, blockLo) - M.CrownStep;
            double lobeHi = lobes.Select(l => l.C[k] + l.R[k]).Aggregate((m, v) => Py.Max(m, v));
            int blockHi = blocks.Select(b => (k == 0 ? b.X : k == 1 ? b.Y : b.Z) + 1).Aggregate((m, v) => v > m ? v : m);
            hi[k] = Py.Max(lobeHi, blockHi) + M.CrownStep;
        }
        int[] size = [.. Enumerable.Range(0, 3).Select(k => (int)Math.Ceiling((hi[k] - lo[k]) / M.CrownStep) + 1)];
        V3 Point(int i, int j, int k) => new(lo[0] + i * M.CrownStep, lo[1] + j * M.CrownStep, lo[2] + k * M.CrownStep);
        var values = new double[size[0], size[1], size[2]];
        for (int i = 0; i < size[0]; i++)
            for (int j = 0; j < size[1]; j++)
                for (int k = 0; k < size[2]; k++)
                    values[i, j, k] = Field(Point(i, j, k));

        void Surface(double level, int kind, double dark)
        {
            // The field's surface at level (metres inside), shaded dark.
            var cell = new Dictionary<(int, int, int), int>();
            for (int i = 0; i < size[0] - 1; i++)
                for (int j = 0; j < size[1] - 1; j++)
                    for (int k = 0; k < size[2] - 1; k++)
                    {
                        // The mean of the crossings on the cell's edges.
                        double sx = 0, sy = 0, sz = 0;
                        int n = 0;
                        foreach (var (u, v) in Mesh.Edges)
                        {
                            double fu = values[i + u.Item1, j + u.Item2, k + u.Item3] - level;
                            double fv = values[i + v.Item1, j + v.Item2, k + v.Item3] - level;
                            if ((fu > 0) != (fv > 0))
                            {
                                double s = fu / (fu - fv);
                                V3 pu = Point(i + u.Item1, j + u.Item2, k + u.Item3), pv = Point(i + v.Item1, j + v.Item2, k + v.Item3);
                                sx += pu.X + s * (pv.X - pu.X);
                                sy += pu.Y + s * (pv.Y - pu.Y);
                                sz += pu.Z + s * (pv.Z - pu.Z);
                                n++;
                            }
                        }
                        if (n == 0)
                            continue;
                        var p = new V3(sx / n, sy / n, sz / n);
                        const double h = 0.05;
                        double[] g = new double[3];
                        for (int a = 0; a < 3; a++)
                        {
                            V3 plus = new(p.X + (a == 0 ? h : 0.0), p.Y + (a == 1 ? h : 0.0), p.Z + (a == 2 ? h : 0.0));
                            V3 minus = new(p.X - (a == 0 ? h : 0.0), p.Y - (a == 1 ? h : 0.0), p.Z - (a == 2 ? h : 0.0));
                            g[a] = Field(plus) - Field(minus);
                        }
                        double length = Math.Sqrt(Py.Sum(g.Select(v => v * v)));
                        if (length == 0)
                            length = 1.0;
                        var normal = new V3(-g[0] / length, -g[1] / length, -g[2] / length);
                        cell[(i, j, k)] = mesh.Pos.Count;
                        mesh.AddVertex(p, normal, (0.0, 0.0, 0.0, 0.0), ((0.72 + 0.28 * (0.5 + 0.5 * normal.Y)) * dark, 0.0), kind);
                    }
            // A quad round every grid edge the surface crosses, of the four cells sharing it.
            for (int i = 0; i < size[0]; i++)
                for (int j = 0; j < size[1]; j++)
                    for (int k = 0; k < size[2]; k++)
                    {
                        double f = values[i, j, k];
                        for (int axis = 0; axis < 3; axis++)
                        {
                            int[] o = [i, j, k];
                            o[axis]++;
                            if (o[axis] >= size[axis] || (f > level) == (values[o[0], o[1], o[2]] > level))
                                continue;
                            int[] bc = [.. Enumerable.Range(0, 3).Where(m => m != axis)];
                            var ring = new int?[4];
                            int at = 0;
                            foreach (var (db, dc) in new[] { (0, 0), (1, 0), (1, 1), (0, 1) })
                            {
                                int[] q = [i, j, k];
                                q[bc[0]] -= db;
                                q[bc[1]] -= dc;
                                ring[at++] = cell.TryGetValue((q[0], q[1], q[2]), out int vi) ? vi : null;
                            }
                            if (ring.Contains(null))
                                continue;
                            double[] outward = [0.0, 0.0, 0.0];
                            outward[axis] = f > level ? 1.0 : -1.0;
                            foreach (var tri0 in new[] { (ring[0].Value, ring[1].Value, ring[2].Value), (ring[0].Value, ring[2].Value, ring[3].Value) })
                            {
                                var tri = tri0;
                                V3 p0 = mesh.Pos[tri.Item1], p1 = mesh.Pos[tri.Item2], p2 = mesh.Pos[tri.Item3];
                                V3 c = V3.Cross(V3.Sub(p1, p0), V3.Sub(p2, p0));
                                if (Py.Sum([c.X * outward[0], c.Y * outward[1], c.Z * outward[2]]) < 0)
                                    tri = (tri.Item1, tri.Item3, tri.Item2);
                                if (kind == M.Shell)
                                    mesh.Unsolid.Add(mesh.Idx.Count / 3);
                                mesh.Idx.AddRange([tri.Item1, tri.Item2, tri.Item3]);
                            }
                        }
                    }
        }

        int faces = mesh.Idx.Count;
        Surface(0.0, M.Foliage, 1.0);
        LeafCards(mesh, rng, faces);
        // The field's own surfaces further in: inside wherever the crown is that thick,
        // none where it is thinner (copies along the normal turned inside out there and
        // stood out as dark lumps and smears, Luca).
        return () =>
        {
            foreach (var (depth, dark) in M.LeafShells)
                Surface(depth, M.Shell, dark);
        };
    }

    static V3? UnitOrNone(V3 v)
    {
        double length = Math.Sqrt(Py.Sum([v.X * v.X, v.Y * v.Y, v.Z * v.Z]));
        return length > 1e-6 ? new V3(v.X / length, v.Y / length, v.Z / length) : null;
    }

    // Cards twigs on the crown whose triangles start at index faces, as many per square
    // metre on top, at the sides and below: each a card leaning out of the surface, a third
    // inside, lit with the crown's normal there; only to be seen. u runs from a whole
    // number k (LeafCard's wind pace) to k + 1, mirrored at random; v from tip (0) to base (1).
    static void LeafCards(Mesh mesh, PyRandom rng, int faces)
    {
        var tris = new List<int[]>();
        for (int i = faces; i < mesh.Idx.Count; i += 3)
            tris.Add([mesh.Idx[i], mesh.Idx[i + 1], mesh.Idx[i + 2]]);
        double total = 0.0;
        var upto = new List<double>();
        foreach (var t in tris)
        {
            V3 c = V3.Cross(V3.Sub(mesh.Pos[t[1]], mesh.Pos[t[0]]), V3.Sub(mesh.Pos[t[2]], mesh.Pos[t[0]]));
            total += 0.5 * Math.Sqrt(Py.Sum([c.X * c.X, c.Y * c.Y, c.Z * c.Z]));
            upto.Add(total);
        }
        int placed = 0;
        while (placed < M.Cards)
        {
            // A point on the surface by area, its normal blended from the corners.
            int[] tri = tris[Math.Min(BisectRight(upto, rng.Random() * total), tris.Count - 1)];
            double r1 = Math.Sqrt(rng.Random()), r2 = rng.Random();
            double[] wts = [1 - r1, r1 * (1 - r2), r1 * r2];
            double Blend(Func<int, double> value) => Py.Sum([wts[0] * value(0), wts[1] * value(1), wts[2] * value(2)]);
            var p = new V3(Blend(m => mesh.Pos[tri[m]].X), Blend(m => mesh.Pos[tri[m]].Y), Blend(m => mesh.Pos[tri[m]].Z));
            V3? n = UnitOrNone(new V3(Blend(m => mesh.Nrm[tri[m]].X), Blend(m => mesh.Nrm[tri[m]].Y), Blend(m => mesh.Nrm[tri[m]].Z)));
            if (n == null)
                continue;
            double g0 = rng.Gauss(0, 1), g1 = rng.Gauss(0, 1), g2 = rng.Gauss(0, 1);
            V3? lean = UnitOrNone(new V3(g0, g1, g2));
            double g3 = rng.Gauss(0, 1), g4 = rng.Gauss(0, 1), g5 = rng.Gauss(0, 1);
            V3? side = UnitOrNone(new V3(g3, g4, g5));
            if (lean == null || side == null)
                continue;
            V3 nv = n.Value, lv = lean.Value;
            V3? d = UnitOrNone(new V3(nv.X + 0.9 * lv.X, nv.Y + 0.9 * lv.Y, nv.Z + 0.9 * lv.Z));
            V3? w = d == null ? null : UnitOrNone(V3.Cross(d.Value, side.Value));
            if (w == null)
                continue;
            V3 dv = d.Value, wv = w.Value;
            double length = rng.Uniform(M.CardLength.Min, M.CardLength.Max);
            double width = 0.8 * length;
            var bottom = new V3(p.X - dv.X * length / 3, p.Y - dv.Y * length / 3, p.Z - dv.Z * length / 3);
            var quad = new[] { (-1, 0), (1, 0), (1, 1), (-1, 1) }.Select(st => new V3(
                bottom.X + st.Item1 * wv.X * width / 2 + st.Item2 * dv.X * length,
                bottom.Y + st.Item1 * wv.Y * width / 2 + st.Item2 * dv.Y * length,
                bottom.Z + st.Item1 * wv.Z * width / 2 + st.Item2 * dv.Z * length)).ToArray();
            long card = rng.RandRange(8);
            var (u0, u1) = rng.Random() < 0.5 ? (card + 1, card) : (card, card + 1);
            int at = mesh.Pos.Count;
            (double, double)[] uvs = [(u0, 1.0), (u1, 1.0), (u1, 0.0), (u0, 0.0)];
            for (int i = 0; i < 4; i++)
                mesh.AddVertex(quad[i], nv, (0.0, 0.0, 0.0, 0.0), uvs[i], M.Card);
            // Both sides wound as fronts (LeafCards culls backs): Stride turns a back
            // face's normal round, and a twig seen from behind went dark.
            foreach (var (a, b, c) in new[] { (at, at + 1, at + 2), (at, at + 2, at + 3), (at, at + 2, at + 1), (at, at + 3, at + 2) })
            {
                mesh.Unsolid.Add(mesh.Idx.Count / 3);
                mesh.Idx.AddRange([a, b, c]);
            }
            placed++;
        }
    }

    // bisect.bisect: how many values are <= x.
    static int BisectRight(List<double> values, double x)
    {
        int lo = 0, hi = values.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (x < values[mid])
                hi = mid;
            else
                lo = mid + 1;
        }
        return lo;
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
            if (mesh.Keys[faces[f][0]].Kind != M.Shell)
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

    // The mesh split by kind of block, each with only its own vertices; the leaves with
    // their inner shells (LeafShells).
    public static List<Part> Parts(Mesh mesh)
    {
        var output = new List<Part>();
        foreach (var (kinds, name) in new[] { (new[] { M.Ground }, "Terrain"), (new[] { M.Foliage, M.Shell }, "Leaves"), (new[] { M.Trunk }, "Bark"), (new[] { M.Card }, "LeafCards") })
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
            if (kinds.Contains(M.Foliage))
                // TEXCOORD_0.y = 1 marks the leaves (LeafSplat needs it no more).
                uv = [.. uv.Select(u => (u.U, 1.0))];
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
        // The leaf shells come last (Build) and are not solid.
        int solid = mesh.Keys.Count(k => k.Kind != M.Shell);
        if (mesh.Keys.Skip(solid).Any(k => k.Kind != M.Shell))
            throw new Exception("Schalen nicht am Ende");
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
