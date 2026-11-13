#!/usr/bin/env -S dotnet run
// Map "Pyramid" after the KotH map koth_map_minecraftinfinity (used with its author's permission): a step pyramid
// in a walled garden, measured from the schematic (PyramidSource.txt, 1 m across, 0.8 m high). Writes Pyramid.glb
// and KotH.Shared/Resources/Maps/PyramidTerrain.bin, and the far trees' TreeImpostors(Normal).png.
//   dotnet run Tools/maps/Pyramid.cs   then Tools/maps/MapUV --map Pyramid
#:project ../../KotH.MapFormat/KotH.MapFormat.csproj
#:include ArenaShapes.cs
#:include MountainTerrain.cs
#:include Impostors.cs
#:include ../Common/Py.cs
#:include ../Common/Libm.cs
#:include ../Common/Glb.cs
#:include ../Common/Png.cs
#:property ServerGarbageCollection=true
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using KotH.MapFormat;
using System.Globalization;
using KotH.Tools;
using KotH.Tools.Maps;

// The source text has decimal points, whatever the machine's language.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
string root = Path.GetFullPath(Path.Combine(Here(), "../.."));
var arena = new Arena();
var chunky = Arena.Chamfered;

// ---- the grid: block (x, y, z) of the source spans x +- 0.5, z +- 0.5 and heights y * 0.8 to (y + 1) * 0.8;
// the ground layer is y -1, its top height 0. The beacon's column is x = z = 0 ----
const double Rise = 0.8;
double H(double y) => y * Rise;

string[] source = File.ReadAllLines(Path.Combine(Here(), "PyramidSource.txt"));
string[] Section(string name)
{
    int at = Array.FindIndex(source, l => l.StartsWith($"[{name}]"));
    return [.. source.Skip(at + 1).TakeWhile(l => !l.StartsWith('['))];
}

// A box from (x0, y0, z0) to (x1, y1, z1), its upright edges cut by c.
void Box(string material, double x0, double x1, double y0, double y1, double z0, double z1, bool solid = true, double c = 0,
         (double, double, double, double)? colour = null)
{
    double dx = x1 - x0, dz = z1 - z0;
    arena.Bar(material, new V3((x0 + x1) / 2, y0, (z0 + z1) / 2), new V3((x0 + x1) / 2, y1, (z0 + z1) / 2),
              [.. chunky(dz, dx, c).Select(p => (p.U, p.V - dx / 2))], solid, up: new V3(1, 0, 0), colour: colour);
}

// ---- the garden's ground: a height field over the block columns, grass, podzol, paths and cobbles as the
// terrain's weights (grass, sand, snow, cap; none is rock); round the pond a smooth hollow on a 25 cm grid ----
string[] ground = Section("ground");
const int GX = -48, GZ = -48;
char Cell(int x, int z)
{
    int r = z - GZ, c = x - GX;
    return r >= 0 && r < ground.Length && c >= 0 && c < ground[r].Length ? ground[r][c] : ' ';
}
bool Inside(int x, int z) => Math.Sqrt(x * x + z * z) < 48.6;
((double, double, double, double) Col, double Shade) Surface(char k) => k switch
{
    'g' or ' ' or 'S' or 'G' => (Arena.Grass, 1.0),
    'o' => (Arena.Grass, 0.8),
    'p' => ((0.45, 0.55, 0, 0), 0.85),
    'd' or '=' => ((0, 1, 0, 0), 0.8),
    'w' => ((0, 1, 0, 0), 0.6),
    _ => (Arena.Plain, 0.9),
};
var pond = new List<(int X, int Z)>();
for (int z = -48; z <= 48; z++)
    for (int x = -48; x <= 50; x++)
        if (Cell(x, z) == 'w')
            pond.Add((x, z));
// The hollow: the share of water columns round a point, each weighted down to 0 at 2 m (Luca: counted
// alike within 1.6 m the share jumped from cell to cell and the shore came out jagged), eased into a block's depth.
double Depth(double x, double z)
{
    const double Reach = 2.0;
    double all = 0, wet = 0;
    for (int cz = (int)Math.Floor(z - Reach); cz <= (int)Math.Ceiling(z + Reach); cz++)
        for (int cx = (int)Math.Floor(x - Reach); cx <= (int)Math.Ceiling(x + Reach); cx++)
        {
            double f = 1 - ((cx - x) * (cx - x) + (cz - z) * (cz - z)) / (Reach * Reach);
            if (f <= 0)
                continue;
            all += f * f;
            wet += Cell(cx, cz) == 'w' ? f * f : 0;
        }
    double t = Math.Clamp((wet / all - 0.15) / 0.45, 0, 1);
    return -Rise * t * t * (3 - 2 * t);
}
const double WaterLevel = -0.13;
// Round the pond (3 m clear of it, where the ground is flat again) each column in 4 x 4.
int px0 = pond.Min(p => p.X) - 3, px1 = pond.Max(p => p.X) + 3, pz0 = pond.Min(p => p.Z) - 3, pz1 = pond.Max(p => p.Z) + 3;
bool Fine(double x, double z) => x >= px0 - 0.5 && x <= px1 + 0.5 && z >= pz0 - 0.5 && z <= pz1 + 0.5;
var heights = new Dictionary<(int, int), double>();
{
    Piece terrain = arena.PieceOf("Terrain");
    var corner = new Dictionary<(int, int), int>();
    // A corner on the quarter-metre grid: weights and shade bilinear between the columns' middles round it.
    int Corner(int qx, int qz)
    {
        if (corner.TryGetValue((qx, qz), out int v))
            return v;
        double x = qx / 4.0, z = qz / 4.0, shade = 0, g = 0, s = 0, n = 0, cap = 0, weight = 0;
        int x0 = (int)Math.Floor(x), z0 = (int)Math.Floor(z);
        foreach (var (cx, cz) in new[] { (x0, z0), (x0 + 1, z0), (x0, z0 + 1), (x0 + 1, z0 + 1) })
        {
            double w = (1 - Math.Abs(x - cx)) * (1 - Math.Abs(z - cz));
            if (!Inside(cx, cz) || w <= 0)
                continue;
            var (col, sh) = Surface(Cell(cx, cz));
            shade += w * sh;
            (g, s, n, cap) = (g + w * col.Item1, s + w * col.Item2, n + w * col.Item3, cap + w * col.Item4);
            weight += w;
        }
        double y = Fine(x, z) ? Depth(x, z) : 0;
        heights[(qx, qz)] = y;
        return corner[(qx, qz)] = terrain.Add(new V3(x, y, z), new V3(0, 1, 0), (g / weight, s / weight, n / weight, cap / weight),
                                              (shade / weight, 0.0));
    }
    for (int z = -49; z <= 49; z++)
        for (int x = -49; x <= 49; x++)
            if (Inside(x, z))
            {
                // Where the fine grid meets the coarse one the ground is flat, so no crack opens.
                int k = Fine(x, z) ? 4 : 1, q = 4 / k;
                for (int j = 0; j < k; j++)
                    for (int i = 0; i < k; i++)
                    {
                        int qx = 4 * x - 2 + q * i, qz = 4 * z - 2 + q * j;
                        int a = Corner(qx, qz), b = Corner(qx + q, qz), c = Corner(qx + q, qz + q), d = Corner(qx, qz + q);
                        arena.Triangle(terrain, a, c, b, true);
                        arena.Triangle(terrain, a, d, c, true);
                    }
            }
    // Normals from the faces round each corner.
    var sum = new V3[terrain.Pos.Count];
    for (int t = 0; t < terrain.Idx.Count; t += 3)
    {
        V3 pa = terrain.Pos[terrain.Idx[t]], pb = terrain.Pos[terrain.Idx[t + 1]], pc = terrain.Pos[terrain.Idx[t + 2]];
        V3 fn = V3.Cross(V3.Sub(pb, pa), V3.Sub(pc, pa));
        for (int k = 0; k < 3; k++)
        {
            V3 o = sum[terrain.Idx[t + k]];
            sum[terrain.Idx[t + k]] = new V3(o.X + fn.X, o.Y + fn.Y, o.Z + fn.Z);
        }
    }
    for (int v = 0; v < sum.Length; v++)
        terrain.Nrm[v] = Mesh.Unit(sum[v]);
}
// The pond: one flat water face (waves from the sea's shader) over every quarter cell the hollow dips under it;
// its outer edges lie where the bank is above the water.
{
    Piece water = arena.PieceOf("Water");
    var up = new V3(0, 1, 0);
    foreach (var (qx, qz) in heights.Keys.Order())
    {
        (int, int)[] round = [(qx, qz), (qx + 1, qz), (qx, qz + 1), (qx + 1, qz + 1)];
        if (!round.All(heights.ContainsKey) || round.All(c => heights[c] >= WaterLevel))
            continue;
        double x = qx / 4.0, z = qz / 4.0;
        int a = water.Add(new V3(x, WaterLevel, z), up, Arena.Plain, (1.0, 0.0));
        water.Add(new V3(x + 0.25, WaterLevel, z), up, Arena.Plain, (1.0, 0.0));
        water.Add(new V3(x + 0.25, WaterLevel, z + 0.25), up, Arena.Plain, (1.0, 0.0));
        water.Add(new V3(x, WaterLevel, z + 0.25), up, Arena.Plain, (1.0, 0.0));
        arena.Triangle(water, a, a + 2, a + 1, false);
        arena.Triangle(water, a, a + 3, a + 2, false);
    }
}
// Lamps let into the grass (the source's shroomlights) and the spawns' gold plates.
for (int z = -48; z <= 48; z++)
    for (int x = -48; x <= 50; x++)
        if (Cell(x, z) == 'S')
            arena.Lathe("Glow", [(0, 0.0), (0.3, 0.0), (0.28, 0.03), (0, 0.03)], false, x, z, chord: 0.1, least: 12);
        else if (Cell(x, z) == 'G')
            arena.Lathe("Sandstone", [(0, 0.0), (0.47, 0.0), (0.45, 0.02), (0, 0.02)], false, x, z, chord: 0.1, least: 16);

// ---- the four faces, each the same: f its outward direction (x, z); a point d out along it, a across it, y up ----
(int X, int Z)[] faces = [(0, -1), (1, 0), (0, 1), (-1, 0)];
V3 At((int X, int Z) f, double d, double a, double y) => new(f.X * d - f.Z * a, y, f.Z * d + f.X * a);
void FaceBox((int X, int Z) f, string material, double d0, double d1, double a0, double a1, double y0, double y1, bool solid = true, double c = 0)
{
    V3 p = At(f, d0, a0, 0), q = At(f, d1, a1, 0);
    Box(material, Math.Min(p.X, q.X), Math.Max(p.X, q.X), y0, y1, Math.Min(p.Z, q.Z), Math.Max(p.Z, q.Z), solid, c);
}
// What is left of a0..a1 across a face once the cuts are taken out.
List<(double A0, double A1)> Spans(double a0, double a1, (double Lo, double Hi)[] cuts)
{
    var left = new List<(double, double)>();
    double at = a0;
    foreach (var (lo, hi) in cuts.OrderBy(c => c.Lo))
    {
        if (lo > at)
            left.Add((at, Math.Min(lo, a1)));
        at = Math.Max(at, hi);
    }
    if (at < a1)
        left.Add((at, a1));
    return left;
}
// A square ring between half widths inner and outer: each face's strip takes the corner on its right, so none
// overlap; cut(f) leaves spans across a face open.
void Ring(string material, double inner, double outer, double y0, double y1, Func<(int X, int Z), (double, double)[]> cut = null)
{
    foreach (var f in faces)
        foreach (var (a0, a1) in Spans(-inner, outer, cut?.Invoke(f) ?? []))
            FaceBox(f, material, inner, outer, a0, a1, y0, y1);
}
void Square(string material, double half, double y0, double y1) => Box(material, -half, half, y0, y1, -half, half);
// A light cornice round a tier's top edge at half width e, standing out 7 cm (seen only: walking stays as it was).
void Cornice(double e, double top, Func<(int X, int Z), (double, double)[]> cut = null)
{
    foreach (var f in faces)
        foreach (var (a0, a1) in Spans(-e, e + 0.07, cut?.Invoke(f) ?? []))
            FaceBox(f, "Smooth", e, e + 0.07, a0, a1, top - 0.16, top, solid: false);
}
// A column on the floor at (x, z): plinth and capital of light stone, the shaft of material.
void Column(double x, double z, double bottom, double top, double r, string shaft)
{
    double w = r + 0.12;
    arena.Lathe("Quartz", [(w, bottom), (w, bottom + 0.2), (r + 0.06, bottom + 0.3), (r, bottom + 0.38)], true, x, z, chord: 0.2, least: 14);
    arena.Lathe(shaft, [(r, bottom + 0.38), (r, top - 0.38)], true, x, z, chord: 0.2, least: 14);
    arena.Lathe("Quartz", [(r, top - 0.38), (r + 0.06, top - 0.3), (w, top - 0.2), (w, top), (0, top)], true, x, z, chord: 0.2, least: 14);
}

// ---- the ring wall: cobbles from the ground to 7.2 m, open where the four shrines' stone backs stand in it ----
const double WallIn = 47.2, WallOut = 48.6, WallTop = 9 * Rise, ShrineHalf = 5.5;
{
    // A shrine's back is 11 m across from 47.5 m out; the wall keeps clear of it.
    double half = Math.Atan2(ShrineHalf, Math.Sqrt(47.5 * 47.5 - ShrineHalf * ShrineHalf));
    double[] axes = [0, Math.PI / 2, Math.PI, 1.5 * Math.PI];
    for (int q = 0; q < 4; q++)
    {
        double a0 = axes[q] + half, a1 = (q + 1 < 4 ? axes[q + 1] : 2 * Math.PI) - half;
        // Closed profiles (the first point again at the end), else the inner face is missing.
        arena.Lathe("Cobble", [(WallIn, -0.4), (WallOut, -0.4), (WallOut, WallTop - 0.12), (WallIn, WallTop - 0.12), (WallIn, -0.4)], true,
                    a0: a0, a1: a1, chord: 0.8);
        arena.Lathe("Cobble", [(WallIn - 0.12, WallTop - 0.12), (WallOut + 0.12, WallTop - 0.12), (WallOut + 0.12, WallTop), (WallIn - 0.12, WallTop),
                               (WallIn - 0.12, WallTop - 0.12)], true, a0: a0, a1: a1, chord: 0.8);
    }
}
// Its timber posts, standing half out of its inner face.
foreach (string line in Section("posts"))
{
    var v = line.Split(' ').Select(double.Parse).ToArray();
    double a = Math.Atan2(v[1], v[0]);
    arena.Lathe("Bark", [(0, -0.35), (0.32, -0.35), (0.32, WallTop + 0.1), (0, WallTop + 0.1)], true, WallIn * Math.Cos(a), WallIn * Math.Sin(a),
                chord: 0.15, least: 10);
}

// ---- the gate shrines (the spawns): brick piers with timber screens under a gabled roof, before a stone back
// with a brick panel; the gold plate between the piers is the ground's ----
foreach (var f in faces)
{
    FaceBox(f, "Smooth", 47.5, 48.5, -ShrineHalf, ShrineHalf, -0.4, WallTop);
    FaceBox(f, "Bricks", 47.4, 47.5, -2.5, 2.5, 0, 6 * Rise);
    foreach (int s in new[] { -1, 1 })
    {
        FaceBox(f, "Bricks", 44.5, 47.4, s > 0 ? 1.5 : -2.5, s > 0 ? 2.5 : -1.5, 0, 3 * Rise, c: 0.05);
        FaceBox(f, "Planks", 44.6, 47.3, s > 0 ? 2.5 : -2.69, s > 0 ? 2.69 : -2.5, 0.1, 3 * Rise - 0.1);
    }
    FaceBox(f, "Smooth", 44.3, 47.4, -2.7, 2.7, 3 * Rise, 3 * Rise + 0.32);
    arena.Bar("Dark", At(f, 44.2, 0, 0), At(f, 47.4, 0, 0), [(-3.0, 3 * Rise + 0.32), (3.0, 3 * Rise + 0.32), (0, 3 * Rise + 1.4)], true);
}

// ---- the step pyramid, measured from the source: tiers 0.8 m high stepping in 1 m (half widths 19.5 at the
// ground to 7.5 under the zone), a hall inside the lowest three, a pit with an altar on the fifth, a colonnade
// storey above it under a roof with a light well, and a flight up each face to the zone ----
(double, double)[] flight = [(-2.5, 2.5)], doors = [(-6.5, -3.5), (3.5, 6.5)];
(double, double)[] FlightCut((int X, int Z) f) => flight;
// The hall's walls: 21 m across east-west, 31 m north-south, doors either side of the flights north and south.
for (int y = 0; y <= 2; y++)
{
    double e = 19.5 - y;
    foreach (var f in faces)
    {
        bool ns = f.X == 0;
        foreach (var (a0, a1) in Spans(ns ? -e : -15.5, ns ? e : 15.5, ns ? doors : []))
            FaceBox(f, "Cobble", ns ? 15.5 : 10.5, e, a0, a1, H(y), H(y + 1));
    }
    Cornice(e, H(y + 1), f => f.X == 0 ? [.. flight, .. doors] : flight);
}
foreach (var (x, z) in new[] { (6, 0), (-6, 0), (0, 6), (0, -6), (5, 3), (5, -3), (-5, 3), (-5, -3), (3, 5), (3, -5), (-3, 5), (-3, -5) })
    Column(x, z, 0, H(3), 0.32, "Smooth");
Square("Cobble", 16.5, H(3), H(4));
Cornice(16.5, H(4), FlightCut);
// The fifth tier: the pit 13 m across, two steps down round it, the altar in it (a dark block between two ledges).
Ring("Cobble", 7.5, 15.5, H(4), H(5));
Ring("Bricks", 6.5, 7.5, H(4), H(4) + 0.4);
Ring("Bricks", 7.0, 7.5, H(4) + 0.4, H(5));
Cornice(15.5, H(5), FlightCut);
Box("Dark", -1.5, 1.5, H(4), H(5), -1.5, 1.5);
foreach (int s in new[] { -1, 1 })
{
    Box("Smooth", -1.5, 1.5, H(5), H(5) + 0.4, s > 0 ? 0.5 : -1.5, s > 0 ? 1.5 : -0.5);
    Box("Bricks", -1.5, 1.5, H(4), H(4) + 0.4, s > 0 ? 1.5 : -2.5, s > 0 ? 2.5 : -1.5);
}
// The colonnade storey: a kerb with a step inside and merlons on it, columns under the roof's edge.
Ring("Cobble", 13.5, 14.5, H(5), H(6), FlightCut);
Ring("Bricks", 12.5, 13.5, H(5), H(5) + 0.4, FlightCut);
foreach (var f in faces)
{
    for (int a = -12; a <= 14; a += 2)
        if (Math.Abs(a) > 2)
        {
            FaceBox(f, "Bricks", 13.6, 14.4, a - 0.4, a + 0.4, H(6), H(8) - 0.12, c: 0.06);
            FaceBox(f, "Smooth", 13.54, 14.46, a - 0.46, a + 0.46, H(8) - 0.12, H(8), c: 0.04);
        }
    for (int a = -9; a <= 11; a += 2)
        if (Math.Abs(a) > 2)
        {
            V3 c = At(f, 11, a, 0);
            Column(c.X, c.Z, H(5), H(8), 0.3, "Smooth");
        }
}
// The upper tiers; under the roof's opening a hollow lit from its sides.
// (Their openings 10 cm inside the block lines, so no face lies in the cheek walls' planes.)
Ring("Cobble", 1.4, 11.5, H(8), H(9));
Ring("Cobble", 2.4, 10.5, H(9), H(10));
foreach (var f in faces)
    FaceBox(f, "Glow", 2.38, 2.4, -0.5, 0.5, H(9) + 0.1, H(10) - 0.1, solid: false);
Square("Cobble", 9.5, H(10), H(11));
Square("Cobble", 8.5, H(11), H(12));
// The zone's floor: a light field 7 m across in a dark border, brick round it.
Ring("Bricks", 4.5, 7.5, H(12), H(13));
Ring("Dark", 3.5, 4.5, H(12), H(13));
Square("Quartz", 3.5, H(12), H(13));
for (int y = 8; y <= 12; y++)
    Cornice(19.5 - y, H(y + 1), FlightCut);
// The flights: 30 plain steps (0.35 m high, 0.43 m deep) from the ground to the zone between sloping cheek
// walls; through the colonnade storey on a bed of stone.
foreach (var f in faces)
{
    // Step k from its nose back into the tier (or bed) its top lies in, whose face is at 19.5 - that tier.
    const int Steps = 30;
    for (int k = 0; k < Steps; k++)
    {
        double bottom = H(13) * k / Steps, top = H(13) * (k + 1) / Steps;
        FaceBox(f, "Bricks", 19.5 - Math.Floor(top / Rise - 1e-9), 20.5 - 13.0 * k / Steps, -1.5, 1.5, bottom, top);
    }
    for (int y = 5; y <= 7; y++)
        FaceBox(f, "Cobble", 10.5, 19.5 - y, -1.6, 1.6, H(y), H(y + 1));   // into the cheek walls, clear of the steps' sides
    foreach (var (a0, a1) in new[] { (-2.5, -1.5), (1.5, 2.5) })
        arena.Bar("Smooth", At(f, 0, a0, 0), At(f, 0, a1, 0), [(18.5, -0.1), (20.5, -0.1), (20.5, Rise), (8.5, H(13)), (7.5, H(13)), (7.5, 11 * Rise)], true);
}

// ---- the temple over the zone: eight columns under a ring beam, a stepped roof with a lit hollow, the gold
// cap, the beacon in glass ----
foreach (var (x, z) in new[] { (5, 5), (5, -5), (-5, 5), (-5, -5), (0, 5), (0, -5), (5, 0), (-5, 0) })
    Column(x, z, H(13), H(16), 0.34, "Smooth");
Ring("Bricks", 4.5, 5.5, H(16), H(17));
Ring("Bricks", 1.5, 6.5, H(17), H(18));
Ring("Bricks", 2.5, 5.5, H(18), H(19));
foreach (var f in faces)
    FaceBox(f, "Glow", 2.48, 2.5, -0.5, 0.5, H(18) + 0.1, H(19) - 0.1, solid: false);
Square("Bricks", 4.5, H(19), H(20));
Square("Bricks", 3.5, H(20), H(21));
Ring("Bricks", 1.5, 2.5, H(21), H(22));
Square("Sandstone", 1.5, H(21), H(22));
Ring("Bricks", 0.5, 1.5, H(22), H(23));
Square("Glass", 0.5, H(22), H(23));
Box("Glow", -0.28, 0.28, H(22) + 0.15, H(23) - 0.15, -0.28, 0.28, solid: false);
Square("Glass", 0.5, H(23), H(24));
foreach (var (e, y) in new[] { (5.5, 16), (6.5, 17), (5.5, 18), (4.5, 19), (3.5, 20), (2.5, 21), (1.5, 22) })
    Cornice(e, H(y + 1));

// ---- low bushes in the garden where the source's moss blocks stood ----
{
    var moss = new Mesh(null!);
    int seed = 2600;
    foreach (string[] f in Section("blocks").Select(l => l.Split(' ')).Where(f => f[4] == "O" && f[1] == "0"))
    {
        double x = double.Parse(f[0]), z = double.Parse(f[2]);
        if (Math.Abs(x) > 20.5 || Math.Abs(z) > 20.5)
            KotH.Tools.Maps.Trees.Leaves(moss, new PyRandom(seed++), [(new V3(x, 0.3, z), 0.5)]);
    }
    arena.Take(moss, new V3(0, 0, 0));
}

// ---- trees where the source's stood, a MountDestiny tree for each; the azaleas as bushes ----
// Its crowns are 3–4 m wide: of trees closer than 4 m only the tallest stands.
var standing = new List<string[]>();
foreach (string[] f in Section("trees").Select(l => l.Split(' ')).OrderByDescending(f => int.Parse(f[3])).ThenBy(f => f[0] + f[1]))
    if (standing.All(o => Math.Sqrt(Math.Pow(double.Parse(o[0]) - double.Parse(f[0]), 2) + Math.Pow(double.Parse(o[1]) - double.Parse(f[1]), 2)) >= 4))
        standing.Add(f);
Console.WriteLine($"{standing.Count} trees");
foreach (var (f, i) in standing.Select((f, i) => (f, i)))
{
    double x = double.Parse(f[0]), z = double.Parse(f[1]);
    int foot = int.Parse(f[2]), height = int.Parse(f[3]);
    int trunk = Math.Clamp((int)Math.Round(height * Rise * 0.45), 3, 6);
    var mesh = new Mesh(null!);
    KotH.Tools.Maps.Trees.Grow(mesh, new PyRandom(2100 + i), 0, 0, 0, trunk)();
    arena.Take(mesh, new V3(x - 0.5, H(foot), z - 0.5));
}
{
    var tubs = new Mesh(null!);
    int seed = 2500;
    foreach (string line in Section("bushes"))
    {
        var v = line.Split(' ').Select(double.Parse).ToArray();
        KotH.Tools.Maps.Trees.Leaves(tubs, new PyRandom(seed++), [(new V3(v[0], H(v[2]) + 0.45, v[1]), 0.6)]);
    }
    arena.Take(tubs, new V3(0, 0, 0));
}

// ---- outside the wall: wooded hills to 200 m (the contact map repeats beyond 208 m), seen only. They rise
// from the wall to a ridge of 14 to 30 m and fall behind it, so the edge stays out of sight; their own slot
// (Hills, the terrain's material) lets MapUV give them fewer lightmap texels ----
const double HillsFrom = 48.6, HillsTo = 200.0;
var hillRng = new PyRandom(4100);
var ridgeWaves = Enumerable.Range(2, 5).Select(k => (K: k, Phase: hillRng.Uniform(0, 2 * Math.PI), Amp: 9.0 / k)).ToArray();
var bumpPhases = Enumerable.Range(0, 6).Select(_ => hillRng.Uniform(0, 2 * Math.PI)).ToArray();
double Ease(double a, double b, double x)
{
    double t = Math.Clamp((x - a) / (b - a), 0, 1);
    return t * t * (3 - 2 * t);
}
double Hill(double x, double z)
{
    double r = Math.Sqrt(x * x + z * z), a = Libm.atan2(z, x);
    double ridge = 22 + ridgeWaves.Sum(w => w.Amp * Libm.cos(w.K * a + w.Phase));
    double bumps = 2.5 * Libm.sin(0.045 * x + bumpPhases[0]) * Libm.sin(0.05 * z + bumpPhases[1])
                 + 1.2 * Libm.sin(0.11 * x + 0.07 * z + bumpPhases[2]) + 0.6 * Libm.sin(0.23 * z - 0.17 * x + bumpPhases[3]);
    return (ridge * Ease(54, 150, r) + bumps * Ease(52, 70, r)) * (1 - 0.35 * Ease(165, HillsTo, r));
}
{
    Piece hills = arena.PieceOf("Hills");
    // Rings closer near the wall, 400 steps round; the first ring a little under the garden's edge.
    var radii = new List<double> { HillsFrom };
    while (radii[^1] < HillsTo)
        radii.Add(Math.Min(HillsTo, radii[^1] + 0.8 + 0.035 * (radii[^1] - 48)));
    const int Round = 400;
    var grid = new int[radii.Count, Round];
    for (int j = 0; j < radii.Count; j++)
        for (int i = 0; i < Round; i++)
        {
            double a = 2 * Math.PI * i / Round, x = radii[j] * Libm.cos(a), z = radii[j] * Libm.sin(a);
            double y = j == 0 ? -0.03 : Hill(x, z);
            const double E = 0.25;
            V3 n = Mesh.Unit(new V3(Hill(x - E, z) - Hill(x + E, z), 2 * E, Hill(x, z - E) - Hill(x, z + E)));
            // Grass, the steeper the more bare rock.
            double grass = Ease(0.78, 0.92, n.Y);
            grid[j, i] = hills.Add(new V3(x, y, z), n, (grass, 0, 0, 0), (1.0, 0.0));
        }
    for (int j = 0; j + 1 < radii.Count; j++)
        for (int i = 0; i < Round; i++)
        {
            int a = grid[j, i], b = grid[j, (i + 1) % Round], c = grid[j + 1, (i + 1) % Round], d = grid[j + 1, i];
            arena.Triangle(hills, a, b, c, false);
            arena.Triangle(hills, a, c, d, false);
        }
}
// The forest on them: a tree on a jittered 4.6 m grid (Luca: twice the trees of 6.5 m) where a slow noise allows (glades), fewer further out,
// each a flat stand-in from TreeImpostors.png that TreeBillboard turns to the camera about its upright.
// COLOR_0 as TreeImpostor reads it: shade, tint, then the half width / 10 (TreeBillboard's reach), unused.
string terrainTextures = Path.Combine(root, "KotH.Game/Resources/Textures/Terrain");
double tallest = Impostors.Render(terrainTextures, Path.Combine(terrainTextures, "TreeImpostors.png"), Path.Combine(terrainTextures, "TreeImpostorsNormal.png"));
Console.WriteLine($"TreeImpostors.png, TreeImpostorsNormal.png: trees up to {Py.F(tallest, 1)} m");
{
    Piece far = arena.PieceOf("FarTrees");
    var rng = new PyRandom(4200);
    var stands = new List<(double R, double X, double Z, double W, double Bottom, double Top, double U0, double U1, double V0, double V1, double Tint)>();
    const double Step = 4.6, Pixel = 1.0 / Impostors.AtlasPx;
    for (double gz = -HillsTo; gz <= HillsTo; gz += Step)
        for (double gx = -HillsTo; gx <= HillsTo; gx += Step)
        {
            double x = gx + rng.Uniform(-1.8, 1.8), z = gz + rng.Uniform(-1.8, 1.8), r = Math.Sqrt(x * x + z * z);
            int tile = rng.RandInt(0, Impostors.Tiles - 1);
            double scale = rng.Uniform(0.8, 1.2), tint = rng.Random(), keep = rng.Random();
            double glade = Libm.sin(0.06 * x + 1.3) * Libm.cos(0.05 * z - 0.4) + 0.5 * Libm.sin(0.13 * (x + z));
            if (r < 56 || r > 192 || glade < -0.6 || keep > 0.9 - 0.3 * Ease(60, 190, r))
                continue;
            double w = Impostors.Size * scale / 2, bottom = Hill(x, z) - Impostors.Ground * scale, top = bottom + 2 * w;
            double u0 = (tile % 2) * 0.5 + Pixel, u1 = (tile % 2) * 0.5 + 0.5 - Pixel, v0 = (tile / 2) * 0.5 + Pixel, v1 = (tile / 2) * 0.5 + 0.5 - Pixel;
            stands.Add((r, x, z, w, bottom, top, u0, u1, v0, v1, tint));
        }
    // Nearest the wall first: the camera stays inside, so this is front to back and the depth prepass
    // rejects the cut-out cards behind early instead of sampling and discarding every row.
    var up = new V3(0, 1, 0);
    foreach (var (r, x, z, w, bottom, top, u0, u1, v0, v1, tint) in stands.OrderBy(s => s.R))
    {
        // A middle column (reach 0) so the depth TreeBillboard gives follows its cylinder closely.
        var col = (1.0, tint, w / 10, 0.0);
        var mid = (1.0, tint, 0.0, 0.0);
        int a = far.Add(new V3(x - w, bottom, z), up, col, (u0, v1)), b = far.Add(new V3(x + w, bottom, z), up, col, (u1, v1));
        int c = far.Add(new V3(x + w, top, z), up, col, (u1, v0)), d = far.Add(new V3(x - w, top, z), up, col, (u0, v0));
        int m0 = far.Add(new V3(x, bottom, z), up, mid, ((u0 + u1) / 2, v1)), m1 = far.Add(new V3(x, top, z), up, mid, ((u0 + u1) / 2, v0));
        far.Idx.AddRange([a, m0, m1, a, m1, d, m0, b, c, m0, c, m1]);
    }
    Console.WriteLine($"{stands.Count} far trees");
}

// ---- checks: faces lying on each other the same way round flicker (z-fighting) ----
{
    var planes = new Dictionary<(long, long, long, long), List<(string Name, V3 A, V3 B, V3 C)>>();
    foreach (Piece piece in arena.Pieces.Values)
        for (int t = 0; t < piece.Idx.Count; t += 3)
        {
            V3 a = piece.Pos[piece.Idx[t]], b = piece.Pos[piece.Idx[t + 1]], c = piece.Pos[piece.Idx[t + 2]];
            V3 n = V3.Cross(V3.Sub(b, a), V3.Sub(c, a));
            double l = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
            // Leaves cross anyway; the far trees turn each about its own foot when drawn.
            if (l < 1e-8 || piece.Name is "Leaves" or "LeafCards" or "FarTrees")
                continue;
            n = new V3(n.X / l, n.Y / l, n.Z / l);
            var key = ((long)Math.Round(n.X * 500), (long)Math.Round(n.Y * 500), (long)Math.Round(n.Z * 500),
                       (long)Math.Round((n.X * a.X + n.Y * a.Y + n.Z * a.Z) * 500));
            if (!planes.TryGetValue(key, out var list))
                planes[key] = list = new();
            list.Add((piece.Name, a, b, c));
        }
    int overlaps = 0;
    var where = new Dictionary<string, int>();
    foreach (var (key, list) in planes)
    {
        if (list.Count < 2)
            continue;
        double nx = Math.Abs(key.Item1), ny = Math.Abs(key.Item2), nz = Math.Abs(key.Item3);
        (double, double) P(V3 v) => nx >= ny && nx >= nz ? (v.Y, v.Z) : ny >= nz ? (v.X, v.Z) : (v.X, v.Y);
        var tris = list.Select(t =>
        {
            var pts = new[] { P(t.A), P(t.B), P(t.C) };
            double cx = pts.Average(p => p.Item1), cy = pts.Average(p => p.Item2);
            return (t.Name, Mid: t.A, Pts: pts.Select(p =>
            {
                double dx = p.Item1 - cx, dy = p.Item2 - cy, d = Math.Sqrt(dx * dx + dy * dy);
                return d < 0.004 ? (cx, cy) : (p.Item1 - 0.002 * dx / d, p.Item2 - 0.002 * dy / d);
            }).ToArray());
        }).ToList();
        static bool Apart((double, double)[] p, (double, double)[] q)
        {
            foreach (var s in new[] { p, q })
                for (int i = 0; i < 3; i++)
                {
                    double ex = s[(i + 1) % 3].Item1 - s[i].Item1, ey = s[(i + 1) % 3].Item2 - s[i].Item2;
                    double Proj((double, double) v) => -ey * v.Item1 + ex * v.Item2;
                    double p0 = p.Min(Proj), p1 = p.Max(Proj), q0 = q.Min(Proj), q1 = q.Max(Proj);
                    if (p1 <= q0 + 1e-9 || q1 <= p0 + 1e-9)
                        return true;
                }
            return false;
        }
        for (int i = 0; i < tris.Count; i++)
            for (int j = i + 1; j < tris.Count; j++)
                if (!Apart(tris[i].Pts, tris[j].Pts))
                {
                    overlaps++;
                    string at = $"{tris[i].Name}/{tris[j].Name} at ({Math.Round(tris[i].Mid.X)}, {Py.F(tris[i].Mid.Y, 2)}, {Math.Round(tris[i].Mid.Z)}) n {key.Item1},{key.Item2},{key.Item3} {Py.F(tris[j].Mid.X, 2)} {Py.F(tris[j].Mid.Y, 2)} {Py.F(tris[j].Mid.Z, 2)}";
                    where[at] = where.GetValueOrDefault(at) + 1;
                }
    }
    foreach (var (at, count) in where.OrderBy(w => w.Key).Take(400))
        Console.WriteLine($"overlap: {at} x{count}");
    Console.WriteLine($"{overlaps} overlapping face pairs in {where.Count} places");
}

// ---- output ----
string glb = Path.Combine(root, "KotH.Game/Resources/Models/Pyramid.glb");
Output.WriteGlb(glb, arena.Pieces.Values.Select(p => p.ToPart()));
foreach (Piece p in arena.Pieces.Values)
    Console.WriteLine($"  {p.Name,-10} {p.Idx.Count / 3,7} triangles");
// Stride gives the glb's materials the model asset's slots by position, not by name.
string sdm3d = Path.Combine(root, "KotH.Game/Assets/Models/Pyramid.sdm3d");
string[] slotNames = File.Exists(sdm3d) ? [.. File.ReadAllLines(sdm3d).Where(l => l.TrimStart().StartsWith("Name: ")).Select(l => l.Trim()[6..])] : [];
if (!slotNames.SequenceEqual(arena.Pieces.Keys))
    Console.WriteLine($"warning: Pyramid.sdm3d's slots ({string.Join(", ", slotNames)}) are not the glb's materials in order " +
                      $"({string.Join(", ", arena.Pieces.Keys)}): fix the slots, else materials land on the wrong parts");
Console.WriteLine($"Pyramid.glb: {arena.Pieces.Values.Sum(p => p.Idx.Count / 3)} triangles, {Py.F(new FileInfo(glb).Length / 1e6, 1)} MB");

string data = Path.Combine(root, "KotH.Shared/Resources/Maps/PyramidTerrain.bin");
MapCollision collision = MapCollision.FromTriangles(arena.SolidPos, arena.SolidIdx, MapCollision.ChunkSize);
using (var file = File.Create(data))
    collision.Write(file);
Console.WriteLine($"PyramidTerrain.bin: {collision.Chunks.Count} chunks, {collision.TriangleCount} triangles, " +
                  $"at most {collision.Chunks.Max(c => c.Triangles.Length / 3)} per chunk, {Py.F(new FileInfo(data).Length / 1e6, 2)} MB");
return 0;

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path);
