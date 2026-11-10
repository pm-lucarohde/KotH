#:project ../../KotH.MapFormat/KotH.MapFormat.csproj
#:package Prowl.Unwrapper@3.7.2
#:include ../../KotH.MapFormat/Unwrap/LightmapUnwrap.cs
#:include ../Common/Png.cs
// The sample custom map, as a kit would export it: a 48 m meadow with a stepped tower (zone on
// top), crates and a pillar; four spawns facing the zone. Writes the package the probe
// `custommap` loads; with --install also into the player's map folder (KotH's Maps page).
//   dotnet run Tools/maps/SampleMap.cs [-- --install | --live DIR]
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KotH.MapFormat;
using KotH.MapKit;
using KotH.Tools;

string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Environment.GetCommandLineArgs()[0])!, "../../.."));
root = Directory.Exists(Path.Combine(root, "KotH.Shared")) ? root : Directory.GetCurrentDirectory();

var model = new MapModel();
model.Materials.Add(new MapMaterial { Name = "Meadow", Template = "Terrain", Color = [0.32f, 0.55f, 0.22f, 1f], TextureScale = 4f });
model.Materials.Add(new MapMaterial { Name = "Stone", Template = "Terrain", Color = [0.55f, 0.53f, 0.5f, 1f], TextureScale = 4f });
model.Materials.Add(new MapMaterial { Name = "Crate", Template = "Surface", Color = [1f, 1f, 1f, 1f], ColorTexture = 0, Roughness = 0.9f });
model.Materials.Add(new MapMaterial { Name = "Pillar", Template = "Surface", Color = [0.8f, 0.3f, 0.25f, 1f], Roughness = 0.5f });
model.Materials.Add(new MapMaterial { Name = "Pond", Template = "Water" });
model.Materials.Add(new MapMaterial { Name = "Bush", Template = "Leaves", Color = [0.35f, 0.6f, 0.25f, 1f], ColorTexture = 1 });
model.Textures.Add(Checker());
model.Textures.Add(Blob());

Box("Ground", 0, -24, -1, -24, 24, 0, 24);
for (int step = 0; step < 5; step++)
{
    float half = 9f - 1.5f * step;
    Box($"Step{step + 1}", 1, -half, step, -half, half, step + 1, half);
}
Box("Crate1", 2, 12, 0, 4, 14, 2, 6, MapShadows.Baked);
Box("Crate2", 2, -14, 0, -7, -12, 1.2f, -5, MapShadows.Baked);
Box("Crate3", 2, 4, 0, 13, 6, 2, 15, MapShadows.Baked);
Box("Pillar", 3, -15, 0, 12, -13, 6, 14, MapShadows.Live);
// A pond on the meadow and a bush of two crossed cards, neither solid.
Quad("Pond", 4, [-20f, 0.04f, -20f], [-20f, 0.04f, -14f], [-14f, 0.04f, -14f], [-14f, 0.04f, -20f], MapShadows.Off);
Quad("BushA", 5, [17f, 0f, -12f], [19f, 0f, -12f], [19f, 2f, -12f], [17f, 2f, -12f], MapShadows.Baked);
Quad("BushB", 5, [18f, 0f, -13f], [18f, 0f, -11f], [18f, 2f, -11f], [18f, 2f, -13f], MapShadows.Baked);
// An invisible wall round the edge: the meadow ends at a drop otherwise.
Box("EdgeNorth", 0, -24, 0, -25, 24, 3, -24, MapShadows.Off, MapCollide.Only);

var manifest = new MapManifest
{
    Name = "Sample",
    Title = "Sample tower",
    Author = "KotH",
    Description = "The map kit's sample: a stepped tower on a meadow, the zone on top.",
};
manifest.Zone = new MapZone { X = 0, Y = 5f + 1.75f, Z = 0, SizeX = 3f, SizeY = 3.5f, SizeZ = 3f, RespawnBelow = -5f };
foreach (var (x, z) in new[] { (16f, 16f), (-16f, 16f), (16f, -16f), (-16f, -16f) })
    manifest.Spawns.Add(new MapSpawn { X = x, Y = 0f, Z = z });

string folder = Path.Combine(root, "KotH.Probes/Resources/MapPackages/Sample");
// --live DIR: every object's shadow live, written to DIR instead (a test of the cascades).
int live = Array.IndexOf(args, "--live");
if (live >= 0)
{
    foreach (MapMesh m in model.Meshes)
        if (m.Shadows == MapShadows.Baked)
            m.Shadows = MapShadows.Live;
    folder = Path.GetFullPath(args[live + 1]);
}
var result = MapBuilder.Write(folder, manifest, model, Preview(), LightmapUnwrap.Run, "The sample");
foreach (string w in result.Warnings)
    Console.WriteLine("warning: " + w);
foreach (string e in result.Errors)
    Console.WriteLine("error: " + e);
if (!result.Ok)
    return 1;
File.Delete(result.Vdf);   // the probe's copy is never uploaded
Console.WriteLine($"{result.Package.Id}: {folder}");
if (args.Contains("--install"))
{
    var again = MapBuilder.Write(Path.Combine(MapPackage.UserFolder(), manifest.Name), manifest, model, Preview(), null, "The sample");
    Console.WriteLine($"installed {again.Package.Id}: {again.Package.Folder}, SteamCMD: {again.Vdf}");
}
return 0;

// A box from (x0, y0, z0) to (x1, y1, z1): 6 faces of 4 vertices, counter-clockwise from outside,
// UV0 in metres along the face.
void Box(string name, int material, float x0, float y0, float z0, float x1, float y1, float z1,
         MapShadows shadows = MapShadows.Baked, MapCollide collide = MapCollide.Mesh)
{
    var p = new List<float>(); var n = new List<float>(); var uv = new List<float>(); var idx = new List<int>();
    void Face(float[] a, float[] b, float[] c, float[] d, float nx, float ny, float nz)
    {
        int first = p.Count / 3;
        foreach (float[] v in new[] { a, b, c, d })
        {
            p.AddRange(v);
            n.AddRange([nx, ny, nz]);
            // Planar UV across the face's two widest axes.
            float u = Math.Abs(nx) > 0.5f ? v[2] : v[0], w = Math.Abs(ny) > 0.5f ? v[2] : v[1];
            uv.AddRange([u, w]);
        }
        idx.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
    }
    float[] V(float x, float y, float z) => [x, y, z];
    Face(V(x0, y1, z0), V(x0, y1, z1), V(x1, y1, z1), V(x1, y1, z0), 0, 1, 0);   // top
    Face(V(x0, y0, z0), V(x1, y0, z0), V(x1, y0, z1), V(x0, y0, z1), 0, -1, 0);  // bottom
    Face(V(x0, y0, z1), V(x1, y0, z1), V(x1, y1, z1), V(x0, y1, z1), 0, 0, 1);   // +z
    Face(V(x1, y0, z0), V(x0, y0, z0), V(x0, y1, z0), V(x1, y1, z0), 0, 0, -1);  // -z
    Face(V(x1, y0, z1), V(x1, y0, z0), V(x1, y1, z0), V(x1, y1, z1), 1, 0, 0);   // +x
    Face(V(x0, y0, z0), V(x0, y0, z1), V(x0, y1, z1), V(x0, y1, z0), -1, 0, 0);  // -x
    model.Meshes.Add(new MapMesh
    {
        Name = name, Material = material, Shadows = shadows, Collide = collide,
        Positions = [.. p], Normals = [.. n], Uv0 = [.. uv], Indices = [.. idx],
    });
}

// One flat card (counter-clockwise as given), UV over its corners, facing its normal.
void Quad(string name, int material, float[] a, float[] b, float[] c, float[] d, MapShadows shadows)
{
    float ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2], vx = d[0] - a[0], vy = d[1] - a[1], vz = d[2] - a[2];
    float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
    float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
    model.Meshes.Add(new MapMesh
    {
        Name = name, Material = material, Shadows = shadows, Collide = MapCollide.None,
        Positions = [.. a, .. b, .. c, .. d],
        Normals = [nx / len, ny / len, nz / len, nx / len, ny / len, nz / len, nx / len, ny / len, nz / len, nx / len, ny / len, nz / len],
        Uv0 = [0, 1, 1, 1, 1, 0, 0, 0],
        Indices = [0, 1, 2, 0, 2, 3],
    });
}

// A leafy blob: white with alpha, round lobes cut out of a 128² card.
static byte[] Blob()
{
    var png = new Png(128, 128, 4);
    for (int y = 0; y < 128; y++)
        for (int x = 0; x < 128; x++)
        {
            float dx = (x - 64) / 64f, dy = (y - 70) / 58f;
            float lobes = 0.8f + 0.2f * MathF.Cos(7f * MathF.Atan2(dy, dx));
            bool inside = dx * dx + dy * dy < lobes * lobes;
            int i = (y * 128 + x) * 4;
            png.Pixels[i] = png.Pixels[i + 1] = png.Pixels[i + 2] = (byte)(200 + (x * 7 + y * 3) % 55);
            png.Pixels[i + 3] = (byte)(inside ? 255 : 0);
        }
    return png.Encode();
}

// Planks: a 64² wood checker.
static byte[] Checker()
{
    var png = new Png(64, 64, 3);
    for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            bool dark = ((x / 16) + (y / 16)) % 2 == 0 || y % 16 == 0;
            int i = (y * 64 + x) * 3;
            png.Pixels[i] = (byte)(dark ? 120 : 165);
            png.Pixels[i + 1] = (byte)(dark ? 78 : 112);
            png.Pixels[i + 2] = (byte)(dark ? 40 : 62);
        }
    return png.Encode();
}

// The top view, 256²: each mesh's top faces in its material's colour, higher brighter.
byte[] Preview()
{
    var png = new Png(256, 256, 3);
    var top = new float[256 * 256];
    Array.Fill(top, float.MinValue);
    for (int i = 0; i < png.Pixels.Length; i++)
        png.Pixels[i] = 40;
    foreach (MapMesh m in model.Meshes)
    {
        if (m.Collide == MapCollide.Only)
            continue;
        float[] c = model.Materials[m.Material].Color;
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue, h = float.MinValue;
        for (int v = 0; v < m.VertexCount; v++)
        {
            minX = Math.Min(minX, m.Positions[3 * v]); maxX = Math.Max(maxX, m.Positions[3 * v]);
            minZ = Math.Min(minZ, m.Positions[3 * v + 2]); maxZ = Math.Max(maxZ, m.Positions[3 * v + 2]);
            h = Math.Max(h, m.Positions[3 * v + 1]);
        }
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                float wx = -25f + x * 50f / 256f, wz = -25f + y * 50f / 256f;
                if (wx < minX || wx > maxX || wz < minZ || wz > maxZ || h <= top[y * 256 + x])
                    continue;
                top[y * 256 + x] = h;
                float light = 0.7f + 0.05f * h;
                for (int k = 0; k < 3; k++)
                    png.Pixels[(y * 256 + x) * 3 + k] = (byte)Math.Clamp(c[k] * light * 255f, 0f, 255f);
            }
    }
    return png.Encode();
}
