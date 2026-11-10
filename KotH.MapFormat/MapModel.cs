using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KotH.MapFormat
{
    /// <summary>How an object's shadow is made: baked into the lightmap and the static sun map once
    /// at load (cheap, never moves), drawn live into the sun's cascades every frame, or none.</summary>
    public enum MapShadows { Baked, Live, Off }

    /// <summary>What an object is for: seen and solid (the default, the drawn mesh is the collision),
    /// only seen (grass, leaves, decoration), or only solid (an invisible wall).</summary>
    public enum MapCollide { Mesh, None, Only }

    /// <summary>A material by one of the game's templates: Surface (colour or texture, normal map,
    /// roughness, metalness), Terrain (Surface projected from three sides, for rock and ground),
    /// Leaves (alpha cut, both sides, sways) or Water (waves, reflection).</summary>
    public sealed class MapMaterial
    {
        public static readonly string[] Templates = { "Surface", "Terrain", "Leaves", "Water" };

        public string Name = "Material";
        public string Template = "Surface";
        public float[] Color = { 1f, 1f, 1f, 1f };   // linear RGBA, times the texture
        public int ColorTexture = -1;
        public int NormalTexture = -1;
        public float Roughness = 0.8f;
        public float Metalness;
        public float TextureScale = 1f;   // Terrain: metres one texture repeat spans
    }

    /// <summary>One object's triangles in world space (Stride axes: +Y up, -Z forward), drawn
    /// counter-clockwise seen from outside as glTF does. Uv1 is the lightmap atlas (baked only).</summary>
    public sealed class MapMesh
    {
        public string Name = "Mesh";
        public int Material;
        public MapShadows Shadows = MapShadows.Baked;
        public MapCollide Collide = MapCollide.Mesh;
        public float[] Positions = new float[0];
        public float[] Normals;
        public float[] Uv0;
        public float[] Uv1;
        public int[] Indices = new int[0];

        public int VertexCount => Positions.Length / 3;
    }

    /// <summary>map.glb: a glTF 2.0 binary with one node and mesh per object, PBR materials whose
    /// extras name the template, PNG textures inside. Written by hand (fixed order, invariant
    /// numbers), so the same scene gives the same bytes and the same map hash.</summary>
    public sealed class MapModel
    {
        public readonly List<MapMesh> Meshes = new List<MapMesh>();
        public readonly List<MapMaterial> Materials = new List<MapMaterial>();
        public readonly List<byte[]> Textures = new List<byte[]>();   // PNG files

        public void Write(Stream stream)
        {
            var bin = new MemoryStream();
            var json = new StringBuilder();
            var views = new List<string>();
            var accessors = new List<string>();

            int View(byte[] bytes, int? target)
            {
                while (bin.Length % 4 != 0)
                    bin.WriteByte(0);
                long offset = bin.Length;
                bin.Write(bytes, 0, bytes.Length);
                views.Add("{\"buffer\":0,\"byteOffset\":" + offset + ",\"byteLength\":" + bytes.Length
                          + (target.HasValue ? ",\"target\":" + target.Value : "") + "}");
                return views.Count - 1;
            }
            int Floats(float[] data, int width, string type, bool bounds)
            {
                var bytes = new byte[data.Length * 4];
                Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
                int view = View(bytes, 34962);
                string extra = "";
                if (bounds)
                {
                    var min = new float[width];
                    var max = new float[width];
                    for (int k = 0; k < width; k++) { min[k] = float.MaxValue; max[k] = float.MinValue; }
                    for (int i = 0; i < data.Length; i++)
                    {
                        min[i % width] = Math.Min(min[i % width], data[i]);
                        max[i % width] = Math.Max(max[i % width], data[i]);
                    }
                    extra = ",\"min\":" + Array(min) + ",\"max\":" + Array(max);
                }
                accessors.Add("{\"bufferView\":" + view + ",\"componentType\":5126,\"count\":" + data.Length / width
                              + ",\"type\":\"" + type + "\"" + extra + "}");
                return accessors.Count - 1;
            }
            int Indices(int[] data)
            {
                var bytes = new byte[data.Length * 4];
                Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
                int view = View(bytes, 34963);
                accessors.Add("{\"bufferView\":" + view + ",\"componentType\":5125,\"count\":" + data.Length + ",\"type\":\"SCALAR\"}");
                return accessors.Count - 1;
            }

            var meshes = new List<string>();
            var nodes = new List<string>();
            foreach (MapMesh m in Meshes)
            {
                var attributes = new StringBuilder();
                attributes.Append("\"POSITION\":").Append(Floats(m.Positions, 3, "VEC3", true));
                if (m.Normals != null)
                    attributes.Append(",\"NORMAL\":").Append(Floats(m.Normals, 3, "VEC3", false));
                if (m.Uv0 != null)
                    attributes.Append(",\"TEXCOORD_0\":").Append(Floats(m.Uv0, 2, "VEC2", false));
                if (m.Uv1 != null)
                    attributes.Append(",\"TEXCOORD_1\":").Append(Floats(m.Uv1, 2, "VEC2", false));
                int indices = Indices(m.Indices);
                meshes.Add("{\"name\":" + Quote(m.Name) + ",\"primitives\":[{\"attributes\":{" + attributes + "},\"indices\":"
                           + indices + ",\"material\":" + m.Material + "}]}");
                nodes.Add("{\"name\":" + Quote(m.Name) + ",\"mesh\":" + (meshes.Count - 1) + ",\"extras\":{\"shadows\":\""
                          + m.Shadows.ToString().ToLowerInvariant() + "\",\"collide\":\"" + m.Collide.ToString().ToLowerInvariant() + "\"}}");
            }

            var images = new List<string>();
            foreach (byte[] png in Textures)
                images.Add("{\"bufferView\":" + View(png, null) + ",\"mimeType\":\"image/png\"}");

            var materials = new List<string>();
            foreach (MapMaterial m in Materials)
            {
                var pbr = new StringBuilder();
                pbr.Append("\"baseColorFactor\":").Append(Array(m.Color));
                if (m.ColorTexture >= 0)
                    pbr.Append(",\"baseColorTexture\":{\"index\":").Append(m.ColorTexture).Append('}');
                pbr.Append(",\"metallicFactor\":").Append(Num(m.Metalness)).Append(",\"roughnessFactor\":").Append(Num(m.Roughness));
                string normal = m.NormalTexture >= 0 ? ",\"normalTexture\":{\"index\":" + m.NormalTexture + "}" : "";
                bool leaves = m.Template == "Leaves";
                materials.Add("{\"name\":" + Quote(m.Name) + ",\"pbrMetallicRoughness\":{" + pbr + "}" + normal
                              + (leaves ? ",\"alphaMode\":\"MASK\",\"doubleSided\":true" : "")
                              + ",\"extras\":{\"template\":" + Quote(m.Template) + ",\"textureScale\":" + Num(m.TextureScale) + "}}");
            }

            var textures = new List<string>();
            for (int i = 0; i < Textures.Count; i++)
                textures.Add("{\"sampler\":0,\"source\":" + i + "}");

            var nodeList = new StringBuilder();
            for (int i = 0; i < nodes.Count; i++)
                nodeList.Append(i == 0 ? "" : ",").Append(i);
            json.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"KotH map kit\"},\"scene\":0,\"scenes\":[{\"nodes\":[")
                .Append(nodeList).Append("]}]");
            json.Append(",\"nodes\":").Append(List(nodes));
            json.Append(",\"meshes\":").Append(List(meshes));
            json.Append(",\"materials\":").Append(List(materials));
            if (Textures.Count > 0)
            {
                json.Append(",\"samplers\":[{\"magFilter\":9729,\"minFilter\":9987,\"wrapS\":10497,\"wrapT\":10497}]");
                json.Append(",\"images\":").Append(List(images));
                json.Append(",\"textures\":").Append(List(textures));
            }
            json.Append(",\"accessors\":").Append(List(accessors));
            json.Append(",\"bufferViews\":").Append(List(views));
            while (bin.Length % 4 != 0)
                bin.WriteByte(0);
            json.Append(",\"buffers\":[{\"byteLength\":").Append(bin.Length).Append("}]}");

            byte[] jsonBytes = Encoding.UTF8.GetBytes(json.ToString());
            int jsonLength = (jsonBytes.Length + 3) & ~3;
            var w = new BinaryWriter(stream, Encoding.UTF8, true);
            w.Write(0x46546C67u);   // "glTF"
            w.Write(2u);
            w.Write((uint)(12 + 8 + jsonLength + 8 + bin.Length));
            w.Write((uint)jsonLength);
            w.Write(0x4E4F534Au);   // "JSON"
            w.Write(jsonBytes);
            for (int i = jsonBytes.Length; i < jsonLength; i++)
                w.Write((byte)' ');
            w.Write((uint)bin.Length);
            w.Write(0x004E4942u);   // "BIN"
            w.Write(bin.GetBuffer(), 0, (int)bin.Length);
            w.Flush();
        }

        static string List(List<string> items) => "[" + string.Join(",", items) + "]";

        static string Array(float[] values)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < values.Length; i++)
                sb.Append(i == 0 ? "" : ",").Append(Num(values[i]));
            return sb.Append(']').ToString();
        }

        static string Num(float v) => float.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) : "0";

        static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
