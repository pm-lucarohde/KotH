#!/usr/bin/env -S dotnet run
// Map "The Ladder" (id TheLadder) after the Minecraft map "The Ultimate King of the Ladder" by KRMisha and
// Invensations (used with permission): its main tower, block for block from .work/ladder/blocks.pkl, 1 m
// across and 0.8 m high per block. Writes TheLadder.glb and KotH.Shared/Resources/Maps/TheLadderTerrain.bin.
//   dotnet run Tools/maps/Ladder.cs   then Tools/maps/MapUV --map TheLadder
#:project ../../KotH.MapFormat/KotH.MapFormat.csproj
#:include ArenaShapes.cs
#:include MountainTerrain.cs
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
using KotH.Tools;
using KotH.Tools.Maps;

string root = Path.GetFullPath(Path.Combine(Here(), "../.."));
var arena = new Arena();
var chunky = Arena.Chamfered;
double Rad(double degrees) => degrees * Math.PI / 180;
double[] arms = [0, 90, 180, 270];
// A point by an arm m: along it from the middle, across it (to the left looking out), at height y.
V3 Arm(double m, double along, double across, double y)
    => new(along * Math.Cos(Rad(m)) - across * Math.Sin(Rad(m)), y, along * Math.Sin(Rad(m)) + across * Math.Cos(Rad(m)));

// ---- the grid: block (x, y, z) of the source spans x +- 0.5, z +- 0.5 (Minecraft's x and z, both right-handed
// with y up), heights from (y - 1) * 0.8 to y * 0.8; the base's top (block y 0) is height 0 ----
const double Rise = 0.8;
double H(double y) => (y - 1) * Rise;
// A block or part of one (y0 to y1 in blocks, 0.5 steps for slabs) at column (x, z), w x d metres.
void Block(string material, double x, double z, double y0, double y1, bool solid = true, double w = 1, double d = 1)
    => arena.Bar(material, new V3(x, H(y0), z), new V3(x, H(y1), z),
                 [.. chunky(w, d, Math.Min(0.05, Math.Min(w, d) / 6)).Select(p => (p.U, p.V - d / 2))], solid, up: new V3(1, 0, 0));
// The same block in each of the four quarters: (x, z) turned by 0, 90, 180 and 270 degrees.
void Four(string material, double x, double z, double y0, double y1, bool solid = true)
{
    foreach (var (a, b) in new[] { (x, z), (-z, x), (-x, -z), (z, -x) })
        Block(material, a, b, y0, y1, solid);
}

// ---- the base: a disc r 16.6 one block thick, the glass ring r 10 with four open gates and its roof ring,
// the outer wall r 16 (glass between a plinth and a cornice), beams from the roof ring to the wall ----
const double Disc = 16.6, Ring = 10.0, Wall = 16.1;
arena.Lathe("Quartz", [(0, -Rise), (Disc, -Rise), (Disc, 0), (0, 0)], true);
// The gates on the axes are 7 blocks wide, as the start sequence's "Remove Glass Walls" opens them.
double gate = Math.Asin(3.5 / Ring) * 180 / Math.PI;
foreach (double m in arms)
    arena.Lathe("Glass", [(Ring - 0.5, 0), (Ring + 0.5, 0), (Ring + 0.5, H(5)), (Ring - 0.5, H(5)), (Ring - 0.5, 0)], true,
                a0: Rad(m + gate), a1: Rad(m + 90 - gate), least: 2);
Four("Bricks", 7, 7, 1, 5);
arena.Lathe("Smooth", [(Ring - 1.5, H(5)), (Ring + 1.5, H(5)), (Ring + 1.5, H(6)), (Ring - 1.5, H(6)), (Ring - 1.5, H(5))], true);
arena.Lathe("Quartz", [(Wall - 0.6, 0), (Wall + 0.5, 0), (Wall + 0.5, H(2)), (Wall - 0.6, H(2)), (Wall - 0.6, 0)], true);
arena.Lathe("Glass", [(Wall - 0.1, H(2)), (Wall + 0.1, H(2)), (Wall + 0.1, H(5)), (Wall - 0.1, H(5)), (Wall - 0.1, H(2))], true);
arena.Lathe("Quartz", [(Wall - 0.7, H(5)), (Wall + 0.6, H(5)), (Wall + 0.6, H(6)), (Wall - 0.7, H(6)), (Wall - 0.7, H(5))], true);
foreach (double m in arms)
    foreach (int s in new[] { -1, 1 })
        arena.Bar("Smooth", Arm(m, Ring + 1.4, s * 4, H(5)), Arm(m, Wall - 0.6, s * 4, H(5)), chunky(1.0, Rise, 0.04), true);

// ---- the tower: the centre pillar (to block 24, again from 43 to the diamond at 96), the inner satellites
// on the axes at 4 (blocks 1-11) under a slab cross, the columns over them (17-40), steps and pads ----
Block("Quartz", 0, 0, 1, 25);
Block("Quartz", 0, 0, 42, 43);
// A hole in the pillar at 77-79: the way goes out over the blocks round it (below).
Block("Quartz", 0, 0, 43, 77);
Block("Quartz", 0, 0, 80, 96);
Block("Glow", 0, 0, 96, 97);
Four("Quartz", 4, 0, 1, 12);
// Slab plinths round each satellite's foot, the cross at block 12 (a ring round the pillar, arms out to 4).
foreach (var (x, z) in new[] { (5, 0), (4, 1), (4, -1), (3, 0) })
    Four("Smooth", x, z, 1, 1.5);
foreach (var (x, z) in new[] { (3, 0), (4, 0), (2, 1), (2, -1) })
    Four("Smooth", x, z, 12, 12.5);
foreach (var (x, z) in new[] { (2, 0), (1, 1) })
    Four("Bricks", x, z, 12, 13);
Four("Bricks", 4, 0, 17, 41);
// A top-slab ring round the pillar's first top, single blocks to rest on in the gap.
foreach (var (x, z) in new[] { (2, 1), (2, -1) })
    Four("Smooth", x, z, 24.5, 25);
Four("Smooth", 5, 5, 26.5, 27);
Four("Quartz", 2, 0, 37, 38);
// Over the columns: steps in to the centre's cap at block 42 (top slabs round it).
foreach (var (x, z) in new[] { (4, 3), (3, 4) })
    Four("Bricks", x, z, 41, 42);
Four("Bricks", 2, 2, 42, 43);
Four("Smooth", 1, 0, 42.5, 43);
// Pads on slabs at 49/50 with an L of blocks in each corner, plus-shaped platforms at 56 and 59, more pads.
Four("Smooth", 3, 0, 49.5, 50);
foreach (var (x, z) in new[] { (4, 4), (4, 3), (3, 4) })
    Four("Bricks", x, z, 50, 51);
foreach (var (x, z) in new[] { (5, 0), (6, 0), (4, 0), (5, 1), (5, -1) })
    Four(x == 5 && z == 0 ? "Bricks" : "Glass", x, z, 56, 57);
foreach (var (x, z) in new[] { (6, 1), (6, -1), (4, 1), (4, -1) })
    Four("Smooth", x, z, 56.5, 57);
foreach (var (x, z) in new[] { (5, 0), (6, 0), (4, 0), (5, 1), (5, -1) })
    Four("Smooth", x, z, 59.5, 60);
Four("Smooth", 4, 4, 61.5, 62);
Four("Quartz", 4, 0, 77, 78);
Four("Quartz", 4, 4, 78, 79);
Four("Quartz", 2, 2, 79, 80);

// ---- slime pads (bounce): (x, z, block y), each in the four quarters ----
(int X, int Z, int Y)[] pads = [(4, 4, 13), (5, 5, 27), (3, 0, 50), (5, 0, 60), (4, 4, 62), (2, 2, 63)];
foreach (var (x, z, y) in pads)
    Four("Glass", x, z, y, y + 1);

// ---- ladders: (block x, z it hangs on, outward face (dx, dz), first and last block y), in the four quarters
// unless marked one; drawn as two rails with rungs 0.3 m apart, 5 cm off the face, not solid ----
var strips = new List<(int X, int Z, int Dx, int Dz, int Y0, int Y1)>();
void Strip(int x, int z, int dx, int dz, int y0, int y1, bool four = true)
{
    foreach (var (a, b, da, db) in four ? new[] { (x, z, dx, dz), (-z, x, -dz, dx), (-x, -z, -dx, -dz), (z, -x, dz, -dx) }
                                        : new[] { (x, z, dx, dz) })
        strips.Add((a, b, da, db, y0, y1));
}
Strip(0, 0, 1, 0, 1, 24);
// The upper pillar: each face has its own gaps, so a climber goes round to another face; at 77-79 the
// pillar itself has a hole, and the way leads out over the blocks at 77, 78 and 79 and back.
Strip(0, 0, 1, 0, 43, 50, false); Strip(0, 0, 1, 0, 54, 70, false); Strip(0, 0, 1, 0, 75, 76, false);
Strip(0, 0, -1, 0, 43, 55, false); Strip(0, 0, -1, 0, 59, 76, false);
Strip(0, 0, 0, -1, 43, 62, false); Strip(0, 0, 0, -1, 66, 76, false);
Strip(0, 0, 0, 1, 43, 64, false); Strip(0, 0, 0, 1, 69, 76, false);
Strip(0, 0, 1, 0, 80, 96);
Strip(4, 0, 1, 0, 2, 11);
foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
    Strip(4, 0, dx, dz, 17, 40);
Strip(3, 0, 0, 1, 50, 50); Strip(3, 0, 0, -1, 50, 50);
Strip(4, 0, -1, 0, 77, 77);
Strip(4, 4, -1, 0, 78, 78); Strip(4, 4, 0, -1, 78, 78);
foreach (var (x, z, dx, dz, y0, y1) in strips)
{
    // Rails along the face's edges, rungs between them.
    V3 Face(double across, double y) => new(x + dx * 0.55 - dz * across, y, z + dz * 0.55 + dx * across);
    var up = new V3(dx, 0, dz);
    foreach (int s in new[] { -1, 1 })
        arena.Bar("Planks", Face(s * 0.38, H(y0)), Face(s * 0.38, H(y1 + 1)), [.. chunky(0.07, 0.07, 0.015).Select(p => (p.U, p.V - 0.035))], false, up);
    for (double y = H(y0) + 0.15; y < H(y1 + 1) - 0.05; y += 0.3)
        arena.Bar("Planks", Face(-0.36, y), Face(0.36, y), [.. chunky(0.05, 0.05, 0.01).Select(p => (p.U, p.V - 0.025))], false);
}

// ---- output ----
string glb = Path.Combine(root, "KotH.Game/Resources/Models/TheLadder.glb");
Output.WriteGlb(glb, arena.Pieces.Values.Select(p => p.ToPart()));
foreach (Piece p in arena.Pieces.Values)
    Console.WriteLine($"  {p.Name,-10} {p.Idx.Count / 3,7} triangles");
// Stride gives the glb's materials the model asset's slots by position, not by name.
string sdm3d = Path.Combine(root, "KotH.Game/Assets/Models/TheLadder.sdm3d");
string[] slots = File.Exists(sdm3d) ? [.. File.ReadAllLines(sdm3d).Where(l => l.TrimStart().StartsWith("Name: ")).Select(l => l.Trim()[6..])] : [];
if (!slots.SequenceEqual(arena.Pieces.Keys))
    Console.WriteLine($"warning: TheLadder.sdm3d's slots ({string.Join(", ", slots)}) are not the glb's materials in order " +
                      $"({string.Join(", ", arena.Pieces.Keys)}): fix the slots, else materials land on the wrong parts");
Console.WriteLine($"TheLadder.glb: {arena.Pieces.Values.Sum(p => p.Idx.Count / 3)} triangles, {Py.F(new FileInfo(glb).Length / 1e6, 1)} MB");
Console.WriteLine($"{strips.Count} ladder strips, {strips.Sum(s => s.Y1 - s.Y0 + 1)} blocks; {pads.Length * 4} pads");

string data = Path.Combine(root, "KotH.Shared/Resources/Maps/TheLadderTerrain.bin");
MapCollision collision = MapCollision.FromTriangles(arena.SolidPos, arena.SolidIdx);
using (var file = File.Create(data))
    collision.Write(file);
Console.WriteLine($"TheLadderTerrain.bin: {collision.Chunks.Count} chunks, {collision.TriangleCount} triangles, " +
                  $"at most {collision.Chunks.Max(c => c.Triangles.Length / 3)} per chunk, {Py.F(new FileInfo(data).Length / 1e6, 2)} MB");
return 0;

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path);
