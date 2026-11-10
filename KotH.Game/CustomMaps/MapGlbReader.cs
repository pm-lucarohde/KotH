using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using KotH.MapFormat;

namespace KotH.Client.CustomMaps
{
    /// <summary>Reads map.glb as MapModel.Write lays it out (one node and mesh per object, float
    /// attributes, uint indices, PNG images in the binary chunk). JsonDocument keeps it free of
    /// reflection for AOT; anything malformed is an InvalidDataException naming what.</summary>
    public static class MapGlbReader
    {
        public static MapModel Read(string path)
        {
            byte[] file = File.ReadAllBytes(path);
            if (file.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(file) != 0x46546C67)
                throw new InvalidDataException($"{path}: not a glb");
            int jsonLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(12));
            if (jsonLength <= 0 || 20 + jsonLength > file.Length)
                throw new InvalidDataException($"{path}: broken JSON chunk");
            int at = 20 + jsonLength;
            ReadOnlyMemory<byte> bin = ReadOnlyMemory<byte>.Empty;
            if (at + 8 <= file.Length)
            {
                int binLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at));
                if (binLength < 0 || at + 8 + binLength > file.Length)
                    throw new InvalidDataException($"{path}: broken binary chunk");
                bin = file.AsMemory(at + 8, binLength);
            }
            using JsonDocument doc = JsonDocument.Parse(file.AsMemory(20, jsonLength));
            return Parse(doc.RootElement, bin);
        }

        static MapModel Parse(JsonElement root, ReadOnlyMemory<byte> bin)
        {
            var model = new MapModel();
            JsonElement views = Get(root, "bufferViews"), accessors = Get(root, "accessors");

            ReadOnlySpan<byte> View(int index)
            {
                JsonElement v = views[index];
                int offset = v.TryGetProperty("byteOffset", out JsonElement o) ? o.GetInt32() : 0;
                int length = v.GetProperty("byteLength").GetInt32();
                if (offset < 0 || length < 0 || offset + length > bin.Length)
                    throw new InvalidDataException("buffer view out of range");
                return bin.Span.Slice(offset, length);
            }

            if (root.TryGetProperty("images", out JsonElement images))
                foreach (JsonElement image in images.EnumerateArray())
                    model.Textures.Add(View(image.GetProperty("bufferView").GetInt32()).ToArray());
            var textureImage = new List<int>();
            if (root.TryGetProperty("textures", out JsonElement textures))
                foreach (JsonElement t in textures.EnumerateArray())
                    textureImage.Add(t.GetProperty("source").GetInt32());
            int Texture(JsonElement holder, string name)
                => holder.TryGetProperty(name, out JsonElement t) ? textureImage[t.GetProperty("index").GetInt32()] : -1;

            foreach (JsonElement m in Get(root, "materials").EnumerateArray())
            {
                var mat = new MapMaterial { Name = m.TryGetProperty("name", out JsonElement n) ? n.GetString() : "Material" };
                if (m.TryGetProperty("pbrMetallicRoughness", out JsonElement pbr))
                {
                    if (pbr.TryGetProperty("baseColorFactor", out JsonElement c) && c.GetArrayLength() == 4)
                        for (int i = 0; i < 4; i++)
                            mat.Color[i] = c[i].GetSingle();
                    mat.ColorTexture = Texture(pbr, "baseColorTexture");
                    mat.Metalness = pbr.TryGetProperty("metallicFactor", out JsonElement mf) ? mf.GetSingle() : 1f;
                    mat.Roughness = pbr.TryGetProperty("roughnessFactor", out JsonElement rf) ? rf.GetSingle() : 1f;
                }
                mat.NormalTexture = Texture(m, "normalTexture");
                if (m.TryGetProperty("extras", out JsonElement extras))
                {
                    if (extras.TryGetProperty("template", out JsonElement t))
                        mat.Template = t.GetString();
                    if (extras.TryGetProperty("textureScale", out JsonElement s))
                        mat.TextureScale = s.GetSingle();
                }
                model.Materials.Add(mat);
            }

            float[] Floats(int accessor, int width)
            {
                JsonElement a = accessors[accessor];
                if (a.GetProperty("componentType").GetInt32() != 5126)
                    throw new InvalidDataException("attribute not float");
                int count = a.GetProperty("count").GetInt32();
                ReadOnlySpan<byte> bytes = View(a.GetProperty("bufferView").GetInt32());
                if (bytes.Length < count * width * 4)
                    throw new InvalidDataException("attribute shorter than its count");
                return MemoryMarshal.Cast<byte, float>(bytes[..(count * width * 4)]).ToArray();
            }

            JsonElement meshes = Get(root, "meshes");
            foreach (JsonElement node in Get(root, "nodes").EnumerateArray())
            {
                JsonElement mesh = meshes[node.GetProperty("mesh").GetInt32()];
                JsonElement prim = mesh.GetProperty("primitives")[0];
                JsonElement attributes = prim.GetProperty("attributes");
                var m = new MapMesh
                {
                    Name = node.TryGetProperty("name", out JsonElement n) ? n.GetString() : "Mesh",
                    Material = prim.TryGetProperty("material", out JsonElement mat) ? mat.GetInt32() : 0,
                    Positions = Floats(attributes.GetProperty("POSITION").GetInt32(), 3),
                };
                if (attributes.TryGetProperty("NORMAL", out JsonElement nrm))
                    m.Normals = Floats(nrm.GetInt32(), 3);
                if (attributes.TryGetProperty("TEXCOORD_0", out JsonElement uv0))
                    m.Uv0 = Floats(uv0.GetInt32(), 2);
                if (attributes.TryGetProperty("TEXCOORD_1", out JsonElement uv1))
                    m.Uv1 = Floats(uv1.GetInt32(), 2);
                JsonElement ia = accessors[prim.GetProperty("indices").GetInt32()];
                if (ia.GetProperty("componentType").GetInt32() != 5125)
                    throw new InvalidDataException("indices not uint32");
                int count = ia.GetProperty("count").GetInt32();
                ReadOnlySpan<byte> ib = View(ia.GetProperty("bufferView").GetInt32());
                if (ib.Length < count * 4)
                    throw new InvalidDataException("indices shorter than their count");
                m.Indices = MemoryMarshal.Cast<byte, int>(ib[..(count * 4)]).ToArray();
                foreach (int i in m.Indices)
                    if ((uint)i >= (uint)m.VertexCount)
                        throw new InvalidDataException($"{m.Name}: index out of range");
                if (m.Material < 0 || m.Material >= model.Materials.Count)
                    throw new InvalidDataException($"{m.Name}: no material");
                if (node.TryGetProperty("extras", out JsonElement extras))
                {
                    if (extras.TryGetProperty("shadows", out JsonElement s))
                        m.Shadows = s.GetString() switch { "live" => MapShadows.Live, "off" => MapShadows.Off, _ => MapShadows.Baked };
                    if (extras.TryGetProperty("collide", out JsonElement c))
                        m.Collide = c.GetString() switch { "none" => MapCollide.None, "only" => MapCollide.Only, _ => MapCollide.Mesh };
                }
                model.Meshes.Add(m);
            }
            return model;
        }

        static JsonElement Get(JsonElement root, string name)
            => root.TryGetProperty(name, out JsonElement e) ? e : throw new InvalidDataException($"glb without {name}");
    }
}
