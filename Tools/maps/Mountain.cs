#!/usr/bin/env -S dotnet run
// The block mountain of Mount Destiny (Luca, after a Minecraft map): writes Mountain.glb,
// Sea.glb, KotH.Shared/Resources/Maps/MountDestinyTerrain.bin and .work/maps/mountain.png,
// never what Game Studio edits. 1 m blocks: one up is a jump, two a double jump, three a wall.
//   dotnet run Tools/maps/Mountain.cs [-- --seed N]   then Tools/maps/MapUV
#:include MountainTerrain.cs
#:include ../Common/Py.cs
#:include ../Common/Libm.cs
#:include ../Common/Glb.cs
#:include ../Common/Png.cs
#:property ServerGarbageCollection=true
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using KotH.Tools;
using KotH.Tools.Maps;

string root = Path.GetFullPath(Path.Combine(Here(), "../.."));
int seed = M.Seed;
int at = Array.IndexOf(args, "--seed");
if (at >= 0)
    seed = int.Parse(args[at + 1]);
var t = new Terrain(seed);

var (shares, sectors, quickest) = Climb.Check(t);
Console.WriteLine($"Seed {seed}: {t.Height.Count} columns, {t.Wood.Count + t.Trees.Count(b => !t.Wood.Contains(b))} tree blocks, " +
                  $"{t.Raised} columns raised out of pits, {t.Dropped} trees on the beach left out");
Console.WriteLine($"summit reachable from {sectors} / {M.Sectors} directions (with double jump); reachable on the mountain: " +
                  $"{Py.F(shares[2].Item2 * 100, 0)} % with double jump, {Py.F(shares[1].Item2 * 100, 0)} % with single jumps only " +
                  $"(summit {(shares[1].Item1 ? "yes" : "no")})");
Console.WriteLine($"Fastest climb from the spawn: {(quickest is double q && q != 0 ? $"{Py.F(q, 0)} s (without mistakes, path known)" : "none")}");
if (sectors < M.MinSectors)
{
    Console.Error.WriteLine("Abort: the summit is reachable from too few directions");
    return 1;
}

var mesh = Mesh.Build(t);
string glb = Path.Combine(root, "KotH.Game/Resources/Models/Mountain.glb");
Output.WriteGlb(glb, Output.Parts(mesh));
Output.WriteGlb(Path.Combine(root, "KotH.Game/Resources/Models/Sea.glb"), [Output.Sea(t)]);
Console.WriteLine($"Mountain.glb: {mesh.Idx.Count / 3} triangles, {mesh.Pos.Count} vertices, {Py.F(new FileInfo(glb).Length / 1e6, 1)} MB");

Console.WriteLine($"Walkable (flatter than {Py.F(M.SlopeLimit, 0)} degrees): {Py.F(Output.Slopes(mesh) * 100, 0)} % of the groand area");

string data = Path.Combine(root, "KotH.Shared/Resources/Maps/MountDestinyTerrain.bin");
Directory.CreateDirectory(Path.GetDirectoryName(data));
var chunks = Output.WriteCollision(mesh, data);
var counts = chunks.Values.Select(c => c.Count).ToList();
Console.WriteLine($"MountDestinyTerrain.bin: {chunks.Count} Chunks, {counts.Sum()} triangles, at most {counts.Max()} per chunk, {Py.F(new FileInfo(data).Length / 1e6, 1)} MB");

Directory.CreateDirectory(Path.Combine(root, ".work/maps"));
Output.WritePng(t, Path.Combine(root, ".work/maps/mountain.png"));

Console.WriteLine($"Summit plateau: {2 * M.Plateau + 2} x {2 * M.Plateau + 2} blocks, top y = {M.SummitY}, centre (0, 0)");
Console.WriteLine($"Meadow in the south: height y = {M.GrassY}, mountain foot there at z = {Py.F(t.Foot(M.South), 1)}, " +
                  $"beach from z = {Py.F(t.Coast(M.South) - t.Beach(M.South), 1)}, spawn rows at z = {Py.F(t.Foot(M.South) + M.SpawnGap, 1)} " +
                  $"and {Py.F(t.Foot(M.South) + M.SpawnGap + 2.5, 1)}");
return 0;

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path);
