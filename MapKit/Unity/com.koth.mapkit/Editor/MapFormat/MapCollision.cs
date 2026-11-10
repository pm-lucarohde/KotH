using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace KotH.MapFormat
{
    /// <summary>collision.bin, the KTRN v1 stream MountDestiny's ground uses too (zlib): the vertices,
    /// then per 4 m chunk (by centroid, sorted by x then z) its triangles as vertex triples wound
    /// into the solid, as Bepu's triangles take them. The game puts each chunk into one compound.</summary>
    public sealed class MapCollision
    {
        public const uint Magic = 0x4E52544B;   // "KTRN"
        public const float ChunkSize = 4f;

        public struct Chunk
        {
            public int X, Z;
            public int[] Triangles;   // vertex triples, Bepu's winding
        }

        public float[] Vertices = new float[0];   // x, y, z per vertex
        public readonly List<Chunk> Chunks = new List<Chunk>();

        public int TriangleCount
        {
            get
            {
                int n = 0;
                foreach (Chunk c in Chunks)
                    n += c.Triangles.Length / 3;
                return n;
            }
        }

        /// <summary>From triangles as glTF draws them (counter-clockwise seen from outside): welds
        /// equal positions, drops triangles without area, turns each into Bepu's winding.</summary>
        public static MapCollision FromTriangles(IReadOnlyList<float> positions, IReadOnlyList<int> triangles)
        {
            var weld = new Dictionary<(float, float, float), int>();
            var vertices = new List<float>();
            var remap = new int[positions.Count / 3];
            for (int i = 0; i < remap.Length; i++)
            {
                var key = (positions[3 * i], positions[3 * i + 1], positions[3 * i + 2]);
                if (!weld.TryGetValue(key, out int at))
                {
                    weld[key] = at = vertices.Count / 3;
                    vertices.Add(key.Item1);
                    vertices.Add(key.Item2);
                    vertices.Add(key.Item3);
                }
                remap[i] = at;
            }
            var chunks = new SortedDictionary<(int, int), List<int>>();
            for (int t = 0; t + 2 < triangles.Count; t += 3)
            {
                int a = remap[triangles[t]], b = remap[triangles[t + 1]], c = remap[triangles[t + 2]];
                if (a == b || b == c || a == c || Area2(vertices, a, b, c) <= 1e-12)
                    continue;
                double cx = ((double)vertices[3 * a] + vertices[3 * b] + vertices[3 * c]) / 3;
                double cz = ((double)vertices[3 * a + 2] + vertices[3 * b + 2] + vertices[3 * c + 2]) / 3;
                var key = ((int)Math.Floor(cx / ChunkSize), (int)Math.Floor(cz / ChunkSize));
                if (!chunks.TryGetValue(key, out List<int> list))
                    chunks[key] = list = new List<int>();
                list.Add(a);
                list.Add(c);
                list.Add(b);
            }
            var m = new MapCollision { Vertices = vertices.ToArray() };
            foreach (KeyValuePair<(int, int), List<int>> kv in chunks)
                m.Chunks.Add(new Chunk { X = kv.Key.Item1, Z = kv.Key.Item2, Triangles = kv.Value.ToArray() });
            return m;
        }

        static double Area2(List<float> v, int a, int b, int c)
        {
            double ux = v[3 * b] - v[3 * a], uy = v[3 * b + 1] - v[3 * a + 1], uz = v[3 * b + 2] - v[3 * a + 2];
            double wx = v[3 * c] - v[3 * a], wy = v[3 * c + 1] - v[3 * a + 1], wz = v[3 * c + 2] - v[3 * a + 2];
            double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            return nx * nx + ny * ny + nz * nz;
        }

        public void Write(Stream stream)
        {
            var raw = new MemoryStream();
            var w = new BinaryWriter(raw);
            w.Write(Magic);
            w.Write(1u);
            w.Write((uint)(Vertices.Length / 3));
            foreach (float f in Vertices)
                w.Write(f);
            w.Write((uint)Chunks.Count);
            foreach (Chunk c in Chunks)
            {
                w.Write(c.X);
                w.Write(c.Z);
                w.Write((uint)(c.Triangles.Length / 3));
                foreach (int i in c.Triangles)
                    w.Write((uint)i);
            }
            w.Flush();
            Zlib.Write(stream, raw.GetBuffer(), (int)raw.Length);
        }

        /// <summary>Reads a KTRN stream; every index is checked, a broken file is an error.</summary>
        public static MapCollision Read(Stream stream)
        {
            using (var reader = new BinaryReader(Zlib.Open(stream)))
            {
                if (reader.ReadUInt32() != Magic || reader.ReadUInt32() != 1)
                    throw new InvalidDataException("collision: unknown format");
                uint count = reader.ReadUInt32();
                if (count > 50_000_000)
                    throw new InvalidDataException("collision: too many vertices");
                var m = new MapCollision { Vertices = new float[count * 3] };
                for (int i = 0; i < m.Vertices.Length; i++)
                    m.Vertices[i] = reader.ReadSingle();
                uint chunks = reader.ReadUInt32();
                for (uint c = 0; c < chunks; c++)
                {
                    var chunk = new Chunk { X = reader.ReadInt32(), Z = reader.ReadInt32() };
                    uint tris = reader.ReadUInt32();
                    if (tris > 10_000_000)
                        throw new InvalidDataException("collision: too many triangles");
                    chunk.Triangles = new int[tris * 3];
                    for (int i = 0; i < chunk.Triangles.Length; i++)
                    {
                        uint v = reader.ReadUInt32();
                        if (v >= count)
                            throw new InvalidDataException("collision: vertex index out of range");
                        chunk.Triangles[i] = (int)v;
                    }
                    m.Chunks.Add(chunk);
                }
                return m;
            }
        }
    }

    /// <summary>zlib by hand around DeflateStream (header, Adler-32), which every .NET and Unity has.</summary>
    public static class Zlib
    {
        public static void Write(Stream stream, byte[] data, int length)
        {
            stream.WriteByte(0x78);
            stream.WriteByte(0xDA);
            using (var deflate = new DeflateStream(stream, CompressionLevel.Optimal, true))
                deflate.Write(data, 0, length);
            uint a = 1, b = 0;
            for (int i = 0; i < length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            uint adler = (b << 16) | a;
            stream.WriteByte((byte)(adler >> 24));
            stream.WriteByte((byte)(adler >> 16));
            stream.WriteByte((byte)(adler >> 8));
            stream.WriteByte((byte)adler);
        }

        /// <summary>The inflated content after the two header bytes (the checksum is not checked).</summary>
        public static Stream Open(Stream stream)
        {
            int cmf = stream.ReadByte(), flg = stream.ReadByte();
            if ((cmf & 0x0F) != 8 || ((cmf << 8) | flg) % 31 != 0)
                throw new InvalidDataException("not a zlib stream");
            return new DeflateStream(stream, CompressionMode.Decompress);
        }
    }
}
