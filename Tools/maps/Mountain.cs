#!/usr/bin/env -S dotnet run
// The block mountain of map "Neu", after a Minecraft map (Luca, 21.10.2026). Writes only
// what belongs to it, never what Game Studio edits:
//   KotH.Game/Resources/Models/Mountain.glb       what is drawn (vertex colours with AO)
//   KotH.Game/Resources/Models/Sea.glb            the sea round the island
//   KotH.Shared/Resources/Maps/NeuTerrain.bin     the same triangles for the physics, embedded
//   .work/maps/mountain.png                       a map from above, for a look
// Markers, zone and bot settings stay in NeuCollision (Game Studio); this prints where the
// summit and the meadow are. Blocks are 1 m: one block up is a jump, two a double jump,
// three a wall. Seeded; the numbers are Python's, as mountain.py made them (MountainTerrain.cs).
//
//   dotnet run Tools/maps/Mountain.cs [-- --seed N]
//   dotnet run --project Tools/maps/MapUV -c Release      then: the lightmap UVs (TEXCOORD_1)
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
Console.WriteLine($"Seed {seed}: {t.Height.Count} Säulen, {t.Wood.Count + t.Trees.Count(b => !t.Wood.Contains(b))} Baumblöcke, " +
                  $"{t.Raised} Säulen aus Fallen angehoben, {t.Dropped} Bäume am Strand weggelassen");
Console.WriteLine($"Gipfel aus {sectors} / {M.Sectors} Richtungen erreichbar (mit Doppelsprung); vom Berg erreichbar: " +
                  $"{Py.F(shares[2].Item2 * 100, 0)} % mit Doppelsprung, {Py.F(shares[1].Item2 * 100, 0)} % nur mit einfachen Sprüngen " +
                  $"(Gipfel {(shares[1].Item1 ? "ja" : "nein")})");
Console.WriteLine($"Schnellster Aufstieg vom Spawn: {(quickest is double q && q != 0 ? $"{Py.F(q, 0)} s (ohne Fehler, Weg bekannt)" : "keiner")}");
if (sectors < M.MinSectors)
{
    Console.Error.WriteLine("Abbruch: der Gipfel ist aus zu wenigen Richtungen erreichbar");
    return 1;
}

var mesh = Mesh.Build(t);
string glb = Path.Combine(root, "KotH.Game/Resources/Models/Mountain.glb");
Output.WriteGlb(glb, Output.Parts(mesh));
Output.WriteGlb(Path.Combine(root, "KotH.Game/Resources/Models/Sea.glb"), [Output.Sea(t)]);
Console.WriteLine($"Mountain.glb: {mesh.Idx.Count / 3} Dreiecke, {mesh.Pos.Count} Ecken, {Py.F(new FileInfo(glb).Length / 1e6, 1)} MB");

Console.WriteLine($"Begehbar (flacher als {Py.F(M.SlopeLimit, 0)} Grad): {Py.F(Output.Slopes(mesh) * 100, 0)} % der Bodenfläche");

string data = Path.Combine(root, "KotH.Shared/Resources/Maps/NeuTerrain.bin");
Directory.CreateDirectory(Path.GetDirectoryName(data));
var chunks = Output.WriteCollision(mesh, data);
var counts = chunks.Values.Select(c => c.Count).ToList();
Console.WriteLine($"NeuTerrain.bin: {chunks.Count} Chunks, {counts.Sum()} Dreiecke, höchstens {counts.Max()} je Chunk, {Py.F(new FileInfo(data).Length / 1e6, 1)} MB");

Directory.CreateDirectory(Path.Combine(root, ".work/maps"));
Output.WritePng(t, Path.Combine(root, ".work/maps/mountain.png"));

Console.WriteLine($"Gipfelplateau: {2 * M.Plateau + 2} x {2 * M.Plateau + 2} Blöcke, Oberkante y = {M.SummitY}, Mitte (0, 0)");
Console.WriteLine($"Wiese im Süden: Höhe y = {M.GrassY}, Bergfuß dort bei z = {Py.F(t.Foot(M.South), 1)}, " +
                  $"Strand ab z = {Py.F(t.Coast(M.South) - t.Beach(M.South), 1)}, Startreihen bei z = {Py.F(t.Foot(M.South) + M.SpawnGap, 1)} " +
                  $"und {Py.F(t.Foot(M.South) + M.SpawnGap + 2.5, 1)}");
return 0;

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path);
