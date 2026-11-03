using System;
using System.IO;
using System.IO.Compression;
using Stride.Core.Mathematics;

namespace KotH.Client.Rendering
{
    /// <summary>What the ground is made of, from above: Tools/maps/MapUV's surface map
    /// (Resources/Maps/NAMESurface.bin), per cell the topmost face's surface and height. A map
    /// without one is the Test map's plain ground everywhere.</summary>
    public sealed class GroundSurface
    {
        public enum Kind : byte { Ground, Grass, Sand, Snow, Rock }

        // Further above the map's top than this, the landing was on something else: rock.
        const float OnTop = 0.6f;

        readonly int size;
        readonly float half, cell;
        readonly byte[] kinds;
        readonly short[] heights;

        GroundSurface(int size, float half, byte[] kinds, short[] heights)
        {
            this.size = size;
            this.half = half;
            cell = 2f * half / size;
            this.kinds = kinds;
            this.heights = heights;
        }

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
                return Kind.Rock;
            int i = z * size + x;
            if (kinds[i] == 0 || p.Y > heights[i] * 0.01f + OnTop)
                return Kind.Rock;
            return (Kind)kinds[i];
        }
    }
}
