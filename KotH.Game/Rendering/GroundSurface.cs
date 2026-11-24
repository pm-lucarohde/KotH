using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using KotH.MapFormat;
using Stride.Core.Mathematics;

namespace KotH.Client.Rendering
{
    /// <summary>What the ground is made of, from above: Tools/maps/MapUV's surface map
    /// (Resources/Maps/NAMESurface.bin), per cell the topmost face's surface and height; for a
    /// custom map its water (FromWater). A map without either is plain ground everywhere.</summary>
    public sealed class GroundSurface
    {
        public enum Kind : byte { Ground, Grass, Sand, Snow, Rock, Water }

        // Further above the map's top than this, the landing was on something else: rock.
        const float OnTop = 0.6f;

        readonly int size;
        readonly float half, cell;
        readonly byte[] kinds;
        readonly short[] heights;
        readonly Kind elsewhere;
        readonly int present;

        GroundSurface(int size, float half, byte[] kinds, short[] heights, Kind elsewhere = Kind.Rock)
        {
            this.size = size;
            this.half = half;
            cell = 2f * half / size;
            this.kinds = kinds;
            this.heights = heights;
            this.elsewhere = elsewhere;
            present = 1 << (int)elsewhere;
            foreach (byte k in kinds)
                if (k != 0)
                    present |= 1 << k;
        }

        /// <summary>Whether At can answer kind anywhere on this map.</summary>
        public bool Has(Kind kind) => (present & (1 << (int)kind)) != 0;

        // A custom map's water grid: cells of a quarter metre, coarser if the water spreads far.
        const float WaterCell = 0.25f;
        const int MaxWaterCells = 1024;

        /// <summary>A custom map's water (its Water template meshes' upward faces, the highest per cell)
        /// for wading; plain ground elsewhere. Null when the map has no water.</summary>
        public static GroundSurface FromWater(MapModel model)
        {
            float reach = 0f;
            var faces = new List<(Vector3 A, Vector3 B, Vector3 C)>();
            foreach (MapMesh m in model.Meshes)
            {
                if (m.Collide == MapCollide.Only || m.Material < 0 || m.Material >= model.Materials.Count ||
                    model.Materials[m.Material].Template != "Water")
                    continue;
                float[] p = m.Positions;
                for (int i = 0; i + 2 < m.Indices.Length; i += 3)
                {
                    Vector3 a = At(p, m.Indices[i]), b = At(p, m.Indices[i + 1]), c = At(p, m.Indices[i + 2]);
                    Vector3 n = Vector3.Cross(b - a, c - a);
                    if (n.Y <= 0.5f * n.Length())
                        continue;
                    faces.Add((a, b, c));
                    foreach (Vector3 v in new[] { a, b, c })
                        reach = MathF.Max(reach, MathF.Max(MathF.Abs(v.X), MathF.Abs(v.Z)));
                }
            }
            if (faces.Count == 0)
                return null;
            float half = reach + 1f;
            int size = Math.Min(MaxWaterCells, (int)MathF.Ceiling(2f * half / WaterCell));
            float cell = 2f * half / size;
            var kinds = new byte[size * size];
            var heights = new short[size * size];
            foreach (var (a, b, c) in faces)
            {
                int x0 = Math.Max(0, (int)MathF.Floor((MathF.Min(a.X, MathF.Min(b.X, c.X)) + half) / cell));
                int x1 = Math.Min(size - 1, (int)MathF.Floor((MathF.Max(a.X, MathF.Max(b.X, c.X)) + half) / cell));
                int z0 = Math.Max(0, (int)MathF.Floor((MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) + half) / cell));
                int z1 = Math.Min(size - 1, (int)MathF.Floor((MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) + half) / cell));
                float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
                if (MathF.Abs(d) < 1e-9f)
                    continue;
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = (x + 0.5f) * cell - half, pz = (z + 0.5f) * cell - half;
                        float l1 = ((b.Z - c.Z) * (px - c.X) + (c.X - b.X) * (pz - c.Z)) / d;
                        float l2 = ((c.Z - a.Z) * (px - c.X) + (a.X - c.X) * (pz - c.Z)) / d;
                        float l3 = 1f - l1 - l2;
                        if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f)
                            continue;
                        short h = (short)Math.Clamp(MathF.Round((l1 * a.Y + l2 * b.Y + l3 * c.Y) * 100f), short.MinValue, short.MaxValue);
                        int i = z * size + x;
                        if (kinds[i] == 0 || h > heights[i])
                            (kinds[i], heights[i]) = ((byte)Kind.Water, h);
                    }
            }
            return new GroundSurface(size, half, kinds, heights, Kind.Ground);
        }

        static Vector3 At(float[] p, int v) => new Vector3(p[3 * v], p[3 * v + 1], p[3 * v + 2]);

        /// <summary>The map's surface map, null when it has none.</summary>
        public static GroundSurface Load(string map)
        {
            using Stream stream = typeof(GroundSurface).Assembly.GetManifestResourceStream($"Maps/{map}Surface.bin");
            if (stream == null)
                return null;
            using var reader = new BinaryReader(new ZLibStream(stream, CompressionMode.Decompress));
            if (reader.ReadUInt32() != 0x4652534B || reader.ReadUInt32() != 1)   // "KSRF", version 1
                throw new InvalidDataException($"Maps/{map}Surface.bin has an unknown format.");
            int size = reader.ReadInt32();
            float half = reader.ReadSingle();
            var kinds = new byte[size * size];
            var heights = new short[size * size];
            for (int i = 0; i < kinds.Length; i++)
            {
                kinds[i] = reader.ReadByte();
                heights[i] = reader.ReadInt16();
            }
            return new GroundSurface(size, half, kinds, heights);
        }

        /// <summary>The surface at a point on the ground; rock off the map or well above its top.</summary>
        public Kind At(Vector3 p)
        {
            int x = (int)MathF.Floor((p.X + half) / cell), z = (int)MathF.Floor((p.Z + half) / cell);
            if (x < 0 || z < 0 || x >= size || z >= size)
                return elsewhere;
            int i = z * size + x;
            if (kinds[i] == 0 || p.Y > heights[i] * 0.01f + OnTop)
                return elsewhere;
            return (Kind)kinds[i];
        }

        /// <summary>Whether a figure's feet stand in water (Pyramid's pond) and where its surface is.</summary>
        public bool InWater(Vector3 feet, out float level)
        {
            level = 0f;
            int x = (int)MathF.Floor((feet.X + half) / cell), z = (int)MathF.Floor((feet.Z + half) / cell);
            if (x < 0 || z < 0 || x >= size || z >= size || kinds[z * size + x] != (byte)Kind.Water)
                return false;
            level = heights[z * size + x] * 0.01f;
            return feet.Y < level;
        }
    }
}
