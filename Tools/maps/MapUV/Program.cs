// Adds a second UV set (TEXCOORD_1) to Mountain.glb: the terrain's and the bark's
// faces unwrapped into one atlas, which the client fills with the voxel light once
// it is baked (a lightmap). Leaves and sea keep none. Run after Tools/maps/mountain.py:
//
//   dotnet run --project Tools/maps/MapUV -c Release [-- --size 4096 --pad 3]
//
// --size is the lightmap's side in texels the margins are meant for, --pad the
// texels kept free round every chart. Everything else in the file stays as it was.
using System.Text.Json;
using System.Text.Json.Nodes;
using Prowl.Unwrapper;
using Prowl.Vector;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
string path = Path.Combine(root, "KotH.Game/Resources/Models/Mountain.glb");
int size = Option("--size", 4096);
int pad = Option("--pad", 3);
string[] lit = ["Terrain", "Bark"];

byte[] file = File.ReadAllBytes(path);
int jsonLength = BitConverter.ToInt32(file, 12);
var gltf = JsonNode.Parse(file.AsSpan(20, jsonLength))!.AsObject();
int binStart = 20 + jsonLength + 8;
byte[] bin = file[binStart..(binStart + BitConverter.ToInt32(file, 20 + jsonLength))];

var primitives = new List<Primitive>();
foreach (JsonNode? p in gltf["meshes"]![0]!["primitives"]!.AsArray())
{
    var attributes = p!["attributes"]!.AsObject();
    var prim = new Primitive((string)gltf["materials"]![(int)p["material"]!]!["name"]!, (int)p["material"]!);
    foreach (var (name, accessor) in attributes)
        if (name != "TEXCOORD_1")
            prim.Attributes[name] = ReadFloats((int)accessor!);
    prim.Indices = ReadIndices((int)p["indices"]!);
    primitives.Add(prim);
}

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

// Metres per texel from the areas in the world and in the atlas.
double world = 0, atlas = 0;
for (int t = 0; t < triangles.Count; t += 3)
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

Write();

int Option(string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : fallback;
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

// The same layout as mountain.py's write_glb, plus TEXCOORD_1.
void Write()
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
    foreach (Primitive prim in primitives)
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
    gltf["asset"]!["generator"] = "KotH Tools/maps/mountain.py + Tools/maps/MapUV";

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

static Double3 Cross(Double3 a, Double3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
static double Length(Double3 a) => Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);

sealed class Primitive(string material, int materialIndex)
{
    public readonly string Material = material;
    public readonly int MaterialIndex = materialIndex;
    public readonly Dictionary<string, float[]> Attributes = [];
    public int[] Indices = [];
}
