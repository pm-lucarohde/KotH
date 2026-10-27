// Adds a second UV set (TEXCOORD_1) to Mountain.glb and Sea.glb: the terrain's and
// the bark's faces unwrapped into one atlas, which the client fills with the voxel
// light once it is baked (a lightmap), and the inner sea as one square of it, laid
// on from above (the outer sea clamped to its rim). Leaves keep none. Run after
// Tools/maps/Mountain.cs, on its fresh output:
//
//   dotnet run --project Tools/maps/MapUV -c Release [-- --size 4096 --pad 3]
//
// --size is the lightmap's side in texels the margins are meant for, --pad the
// texels kept free round every chart. Everything else in the file stays as it was.
// It also writes the grass map (NeuGrass.bin, GrassField); --grass writes only that,
// from a Mountain.glb that has its lightmap UVs already.
using System.Text.Json;
using System.Text.Json.Nodes;
using Prowl.Unwrapper;
using Prowl.Vector;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
int size = Option("--size", 4096);
int pad = Option("--pad", 3);
string[] lit = ["Terrain", "Bark"];
// The sea's square is unwrapped this much smaller: its light is soft, the mountain keeps the texels.
const double SeaShrink = 4.0;

var mountain = new Glb(Path.Combine(root, "KotH.Game/Resources/Models/Mountain.glb"));
var sea = new Glb(Path.Combine(root, "KotH.Game/Resources/Models/Sea.glb"));
string grassPath = Path.Combine(root, "KotH.Game/Resources/Maps/NeuGrass.bin");
if (args.Contains("--grass") && mountain.HadSecondUV)
{
    GrassMap.Write(grassPath, mountain.Primitives);
    return 0;
}
if (mountain.HadSecondUV || sea.HadSecondUV)
{
    // Its corners are split along the old charts already; unwrapped again, they would stay split.
    Console.Error.WriteLine("Mountain.glb/Sea.glb haben schon Lightmap-UVs: erst Tools/maps/Mountain.cs laufen lassen.");
    return 1;
}
var primitives = mountain.Primitives;
Primitive water = sea.Primitives[0];

// All lit faces in one unwrap, so they share the atlas.
var positions = new List<Double3>();
var normals = new List<Double3>();
var triangles = new List<int>();
foreach (Primitive prim in primitives.Where(p => lit.Contains(p.Material)))
{
    int base0 = positions.Count;
    float[] pos = prim.Attributes["POSITION"], nrm = prim.Attributes["NORMAL"];
    for (int v = 0; v < pos.Length / 3; v++)
    {
        positions.Add(new Double3(pos[3 * v], pos[3 * v + 1], pos[3 * v + 2]));
        normals.Add(new Double3(nrm[3 * v], nrm[3 * v + 1], nrm[3 * v + 2]));
    }
    triangles.AddRange(prim.Indices.Select(i => i + base0));
}
// The inner sea (inside the outer tiles' rim) as one more chart, far off and shrunk.
// Its strips are 1 m deep; the outer tiles, growing outwards, are far deeper.
float[] seaPos = water.Attributes["POSITION"];
double half = water.Indices.Chunk(3)
    .Where(t => t.Max(i => seaPos[3 * i + 2]) - t.Min(i => seaPos[3 * i + 2]) <= 1.001f)
    .SelectMany(t => t).Max(i => Math.Abs(seaPos[3 * i]));
int litTriangles = triangles.Count;
int quad = positions.Count;
foreach (var (x, z) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
{
    positions.Add(new Double3(x * half / SeaShrink, -1000, z * half / SeaShrink));
    normals.Add(new Double3(0, 1, 0));
}
triangles.AddRange([quad, quad + 3, quad + 2, quad, quad + 2, quad + 1]);
Console.WriteLine($"Abwickeln: {triangles.Count / 3} Dreiecke, {positions.Count} Ecken ...");
var watch = System.Diagnostics.Stopwatch.StartNew();
UnwrapResult result = new UnwrapMesh(positions.ToArray(), triangles.ToArray())
    .WithNormals(normals.ToArray())
    .Unwrap(new UnwrapOptions
    {
        PackMargin = (double)pad / size,
        Method = args.Contains("--conformal") ? UnwrapMethod.Conformal : UnwrapMethod.Projection,
        Projection = { UseDiagonalAxes = args.Contains("--diagonal") },
    });
Console.WriteLine($"fertig nach {watch.Elapsed.TotalSeconds:F1} s, {result.DegenerateTriangleIndices?.Length ?? 0} entartete Dreiecke");
Console.WriteLine($"davon {MapDegenerates()} auf die Ebene eines Nachbarn gelegt");

// Metres per texel from the areas in the world and in the atlas.
double world = 0, atlas = 0;
for (int t = 0; t < litTriangles; t += 3)
{
    Double3 a = positions[triangles[t]], b = positions[triangles[t + 1]], c = positions[triangles[t + 2]];
    world += Length(Cross(b - a, c - a)) / 2;
    Double2 u = result.PerCornerUVs[t], v = result.PerCornerUVs[t + 1], w = result.PerCornerUVs[t + 2];
    atlas += Math.Abs((v.X - u.X) * (w.Y - u.Y) - (w.X - u.X) * (v.Y - u.Y)) / 2;
}
Console.WriteLine($"Fläche {world:F0} m², Atlas zu {atlas * 100:F0} % belegt, " +
    string.Join(", ", new[] { 2048, 4096, 8192 }.Select(n => $"{n}²: {Math.Sqrt(world / atlas) / n * 100:F1} cm/Texel")));

// Each corner keeps its vertex unless the vertex has another UV there: then a copy.
int corner = 0;
foreach (Primitive prim in primitives.Where(p => lit.Contains(p.Material)))
{
    int before = prim.Attributes["POSITION"].Length / 3;
    var copies = new Dictionary<(int, float, float), int>();
    var from = new List<int>();
    var uv1 = new List<float>();
    int[] indices = new int[prim.Indices.Length];
    for (int i = 0; i < indices.Length; i++, corner++)
    {
        Double2 uv = result.PerCornerUVs[corner];
        var key = (prim.Indices[i], (float)uv.X, (float)uv.Y);
        if (!copies.TryGetValue(key, out int index))
        {
            copies[key] = index = from.Count;
            from.Add(prim.Indices[i]);
            uv1.Add((float)uv.X);
            uv1.Add((float)uv.Y);
        }
        indices[i] = index;
    }
    foreach (string name in prim.Attributes.Keys.ToArray())
    {
        float[] old = prim.Attributes[name];
        int width = old.Length / before;
        prim.Attributes[name] = from.SelectMany(v => old.AsSpan(v * width, width).ToArray()).ToArray();
    }
    prim.Attributes["TEXCOORD_1"] = uv1.ToArray();
    Console.WriteLine($"{prim.Material}: {from.Count} Ecken statt {before}");
    prim.Indices = indices;
}

// The sea: affine from the square's corners (-h,-h), (h,-h) and (-h,h); outside it clamped to the rim.
Double2 uv00 = result.PerCornerUVs[litTriangles], uv01 = result.PerCornerUVs[litTriangles + 1];
Double2 uv10 = result.PerCornerUVs[litTriangles + 5];
var seaUV = new float[seaPos.Length / 3 * 2];
for (int v = 0; v < seaPos.Length / 3; v++)
{
    double s = (Math.Clamp(seaPos[3 * v], -half, half) + half) / (2 * half);
    double t = (Math.Clamp(seaPos[3 * v + 2], -half, half) + half) / (2 * half);
    Double2 uv = uv00 + (uv10 - uv00) * s + (uv01 - uv00) * t;
    seaUV[2 * v] = (float)uv.X;
    seaUV[2 * v + 1] = (float)uv.Y;
}
water.Attributes["TEXCOORD_1"] = seaUV;
// Stride's importer fails on a second UV set without a first one; the sea's material reads none.
water.Attributes["TEXCOORD_0"] = seaUV;
Console.WriteLine($"Meer: ±{half:F0} m, {Math.Sqrt(world / atlas) / size * SeaShrink * 100:F0} cm/Texel bei {size}²");

mountain.Write();
sea.Write();
GrassMap.Write(grassPath, primitives);
return 0;

// The unwrapper drops slivers (the flat beach has some a few centimetres wide) with
// zero UVs, so they read an empty texel and draw a black line. Each takes the map of
// the nearest coplanar kept triangle reached over shared edges: a projected chart is
// affine in its plane, so the sliver lands in the seam between its neighbours.
int MapDegenerates()
{
    if (result.DegenerateTriangleIndices is not { Length: > 0 } dropped)
        return 0;
    var isDropped = new HashSet<int>(dropped);
    var byEdge = new Dictionary<(int, int), List<int>>();
    for (int t = 0; t < triangles.Count / 3; t++)
        for (int k = 0; k < 3; k++)
        {
            int a = triangles[3 * t + k], b = triangles[3 * t + (k + 1) % 3];
            var edge = (Math.Min(a, b), Math.Max(a, b));
            if (!byEdge.TryGetValue(edge, out var list))
                byEdge[edge] = list = [];
            list.Add(t);
        }
    int mapped = 0;
    foreach (int start in dropped)
    {
        var seen = new HashSet<int> { start };
        var queue = new Queue<int>([start]);
        while (queue.Count > 0)
        {
            int t = queue.Dequeue();
            if (!isDropped.Contains(t) && TryMapOnto(start, t))
            {
                mapped++;
                break;
            }
            for (int k = 0; k < 3; k++)
            {
                int a = triangles[3 * t + k], b = triangles[3 * t + (k + 1) % 3];
                foreach (int next in byEdge[(Math.Min(a, b), Math.Max(a, b))])
                    if (seen.Add(next))
                        queue.Enqueue(next);
            }
        }
    }
    return mapped;
}

// Gives the dropped triangle's corners the UVs of their points in the kept triangle's
// plane (barycentric, extrapolated), if all three lie in that plane.
bool TryMapOnto(int dropped, int kept)
{
    Double3 a = positions[triangles[3 * kept]], b = positions[triangles[3 * kept + 1]], c = positions[triangles[3 * kept + 2]];
    Double3 e1 = b - a, e2 = c - a, n = Cross(e1, e2);
    double area2 = Dot(n, n);
    if (area2 < 1e-12)
        return false;
    var uvs = new Double2[3];
    for (int k = 0; k < 3; k++)
    {
        Double3 p = positions[triangles[3 * dropped + k]] - a;
        if (Math.Abs(Dot(p, n)) / Math.Sqrt(area2) > 1e-3)
            return false;
        double s = Dot(Cross(p, e2), n) / area2, t = Dot(Cross(e1, p), n) / area2;
        uvs[k] = result.PerCornerUVs[3 * kept] * (1 - s - t) + result.PerCornerUVs[3 * kept + 1] * s + result.PerCornerUVs[3 * kept + 2] * t;
    }
    for (int k = 0; k < 3; k++)
        result.PerCornerUVs[3 * dropped + k] = uvs[k];
    return true;
}

int Option(string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : fallback;
}

static Double3 Cross(Double3 a, Double3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
static double Length(Double3 a) => Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
static double Dot(Double3 a, Double3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

sealed class Primitive(string material, int materialIndex)
{
    public readonly string Material = material;
    public readonly int MaterialIndex = materialIndex;
    public readonly Dictionary<string, float[]> Attributes = [];
    public int[] Indices = [];
}

// A glb as Mountain.cs (Output.WriteGlb) lays it out: one mesh, float attributes, uint indices.
sealed class Glb
{
    readonly string path;
    readonly JsonObject gltf;
    readonly byte[] bin;
    public readonly List<Primitive> Primitives = [];
    public readonly bool HadSecondUV;

    public Glb(string path)
    {
        this.path = path;
        byte[] file = File.ReadAllBytes(path);
        int jsonLength = BitConverter.ToInt32(file, 12);
        gltf = JsonNode.Parse(file.AsSpan(20, jsonLength))!.AsObject();
        int binStart = 20 + jsonLength + 8;
        bin = file[binStart..(binStart + BitConverter.ToInt32(file, 20 + jsonLength))];
        foreach (JsonNode? p in gltf["meshes"]![0]!["primitives"]!.AsArray())
        {
            var prim = new Primitive((string)gltf["materials"]![(int)p!["material"]!]!["name"]!, (int)p["material"]!);
            foreach (var (name, accessor) in p["attributes"]!.AsObject())
            {
                HadSecondUV |= name == "TEXCOORD_1";
                prim.Attributes[name] = ReadFloats((int)accessor!);
            }
            prim.Indices = ReadIndices((int)p["indices"]!);
            Primitives.Add(prim);
        }
    }

    float[] ReadFloats(int accessor)
    {
        var a = gltf["accessors"]![accessor]!;
        var view = gltf["bufferViews"]![(int)a["bufferView"]!]!;
        int width = (string)a["type"]! switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, _ => 4 };
        int offset = (int?)view["byteOffset"] ?? 0;
        float[] values = new float[(int)a["count"]! * width];
        Buffer.BlockCopy(bin, offset, values, 0, values.Length * 4);
        return values;
    }

    int[] ReadIndices(int accessor)
    {
        var a = gltf["accessors"]![accessor]!;
        var view = gltf["bufferViews"]![(int)a["bufferView"]!]!;
        int[] values = new int[(int)a["count"]!];
        Buffer.BlockCopy(bin, (int?)view["byteOffset"] ?? 0, values, 0, values.Length * 4);
        return values;
    }

    // The same layout as Mountain.cs writes, plus TEXCOORD_1.
    public void Write()
    {
        var body = new MemoryStream();
        var views = new JsonArray();
        var accessors = new JsonArray();
        int Add(byte[] blob, int target, JsonObject accessor)
        {
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = body.Length, ["byteLength"] = blob.Length, ["target"] = target });
            body.Write(blob);
            accessor["bufferView"] = views.Count - 1;
            accessors.Add(accessor);
            return accessors.Count - 1;
        }
        byte[] Bytes<T>(T[] values) where T : struct => System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        var prims = new JsonArray();
        foreach (Primitive prim in Primitives)
        {
            var attributes = new JsonObject();
            foreach (string name in new[] { "POSITION", "NORMAL", "COLOR_0", "TEXCOORD_0", "TEXCOORD_1" })
            {
                if (!prim.Attributes.TryGetValue(name, out float[]? values))
                    continue;
                int width = name switch { "COLOR_0" => 4, "POSITION" or "NORMAL" => 3, _ => 2 };
                var accessor = new JsonObject { ["componentType"] = 5126, ["count"] = values.Length / width, ["type"] = width == 2 ? "VEC2" : width == 3 ? "VEC3" : "VEC4" };
                if (name == "POSITION")
                {
                    accessor["min"] = new JsonArray(Enumerable.Range(0, 3).Select(k => (JsonNode)Enumerable.Range(0, values.Length / 3).Min(v => values[3 * v + k])).ToArray());
                    accessor["max"] = new JsonArray(Enumerable.Range(0, 3).Select(k => (JsonNode)Enumerable.Range(0, values.Length / 3).Max(v => values[3 * v + k])).ToArray());
                }
                attributes[name] = Add(Bytes(values), 34962, accessor);
            }
            int indices = Add(Bytes(prim.Indices), 34963, new JsonObject { ["componentType"] = 5125, ["count"] = prim.Indices.Length, ["type"] = "SCALAR" });
            prims.Add(new JsonObject { ["attributes"] = attributes, ["indices"] = indices, ["material"] = prim.MaterialIndex });
        }
        gltf["meshes"]![0]!["primitives"] = prims;
        gltf["bufferViews"] = views;
        gltf["accessors"] = accessors;
        gltf["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = body.Length });
        gltf["asset"]!["generator"] = "KotH Tools/maps/Mountain.cs + Tools/maps/MapUV";

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(gltf);
        json = [.. json, .. Enumerable.Repeat((byte)' ', -json.Length & 3)];
        while (body.Length % 4 != 0)
            body.WriteByte(0);
        using var output = new BinaryWriter(File.Create(path));
        output.Write("glTF"u8);
        output.Write(2);
        output.Write(12 + 8 + json.Length + 8 + (int)body.Length);
        output.Write(json.Length);
        output.Write("JSON"u8);
        output.Write(json);
        output.Write((int)body.Length);
        output.Write("BIN\0"u8);
        output.Write(body.ToArray());
        Console.WriteLine($"{path}: {new FileInfo(path).Length / 1e6:F1} MB");
    }
}

