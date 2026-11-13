using System;
using System.Threading.Tasks;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace KotH.Client.Rendering
{
    /// <summary>The volcanic smoke's billows, a tiling 3D texture made once on the CPU: red the shape (Perlin
    /// noise swollen by coarse inverted Worley cells, the cauliflower look of an ash plume), green finer
    /// cells that only erode its edges. Each spread to 0 to 1; one shape for both left loose flecks.</summary>
    static class SmokeNoise
    {
        public const int Size = 96;

        public static Texture Create(GraphicsDevice device)
        {
            var shapes = new float[Size * Size * Size];
            var details = new float[shapes.Length];
            Parallel.For(0, Size, z =>
            {
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                    {
                        var p = new Vector3((x + 0.5f) / Size, (y + 0.5f) / Size, (z + 0.5f) / Size);
                        float w4 = Worley(p, 4, 1), w8 = Worley(p, 8, 2), w16 = Worley(p, 16, 3), w32 = Worley(p, 32, 4);
                        float coarse = 0.625f * w4 + 0.25f * w8 + 0.125f * w16;
                        float perlin = MathUtil.Clamp(0.5f + 0.5f * Fbm(p), 0f, 1f);
                        // Perlin noise remapped into the coarse cells: soft shapes with round, swelling edges.
                        float shape = MathUtil.Clamp((perlin - (coarse - 1f)) / (2f - coarse), 0f, 1f);
                        int i = (z * Size + y) * Size + x;
                        shapes[i] = shape;
                        details[i] = 0.625f * w8 + 0.25f * w16 + 0.125f * w32;
                    }
            });
            var data = new byte[shapes.Length * 2];
            Spread(shapes, data, 0);
            Spread(details, data, 1);
            return Texture.New3D(device, Size, Size, Size, PixelFormat.R8G8_UNorm, data);
        }

        // Into bytes from the 2nd to the 98th percentile, so every channel spans the whole 0 to 1.
        static void Spread(float[] values, byte[] data, int channel)
        {
            var sorted = (float[])values.Clone();
            Array.Sort(sorted);
            float lo = sorted[sorted.Length / 50], hi = sorted[sorted.Length - 1 - sorted.Length / 50];
            for (int i = 0; i < values.Length; i++)
                data[2 * i + channel] = (byte)Byte((values[i] - lo) / (hi - lo));
        }

        static uint Byte(float v) => (uint)MathF.Round(MathUtil.Clamp(v, 0f, 1f) * 255f);

        // 1 at a cell's feature point, 0 a cell's width away; the cells repeat over the texture.
        static float Worley(Vector3 p, int cells, uint seed)
        {
            Vector3 f = p * cells;
            int cx = (int)MathF.Floor(f.X), cy = (int)MathF.Floor(f.Y), cz = (int)MathF.Floor(f.Z);
            float nearest = float.MaxValue;
            for (int k = -1; k <= 1; k++)
                for (int j = -1; j <= 1; j++)
                    for (int i = -1; i <= 1; i++)
                    {
                        int x = cx + i, y = cy + j, z = cz + k;
                        uint h = Hash(Wrap(x, cells), Wrap(y, cells), Wrap(z, cells), seed);
                        var point = new Vector3(x + Unit(h), y + Unit(h * 747796405u + 1u), z + Unit(h * 2891336453u + 7u));
                        nearest = Math.Min(nearest, (point - f).LengthSquared());
                    }
            return 1f - MathUtil.Clamp(MathF.Sqrt(nearest), 0f, 1f);
        }

        // Three octaves of tiling gradient noise from 4 cells across, about -1 to 1.
        static float Fbm(Vector3 p) => Gradient(p, 4) + 0.5f * Gradient(p, 8) + 0.25f * Gradient(p, 16);

        static float Gradient(Vector3 p, int cells)
        {
            Vector3 f = p * cells;
            int x0 = (int)MathF.Floor(f.X), y0 = (int)MathF.Floor(f.Y), z0 = (int)MathF.Floor(f.Z);
            Vector3 t = f - new Vector3(x0, y0, z0);
            Vector3 s = t * t * t * (t * (t * 6f - new Vector3(15f)) + new Vector3(10f));
            float sum = 0f;
            for (int c = 0; c < 8; c++)
            {
                int dx = c & 1, dy = c >> 1 & 1, dz = c >> 2;
                uint h = Hash(Wrap(x0 + dx, cells), Wrap(y0 + dy, cells), Wrap(z0 + dz, cells), 9u);
                var g = Vector3.Normalize(new Vector3(Unit(h) - 0.5f, Unit(h * 747796405u + 1u) - 0.5f, Unit(h * 2891336453u + 7u) - 0.5f) + new Vector3(1e-4f));
                float dot = Vector3.Dot(g, t - new Vector3(dx, dy, dz));
                sum += dot * (dx == 1 ? s.X : 1f - s.X) * (dy == 1 ? s.Y : 1f - s.Y) * (dz == 1 ? s.Z : 1f - s.Z);
            }
            return sum * 1.6f;
        }

        static int Wrap(int i, int n) => ((i % n) + n) % n;

        static uint Hash(int x, int y, int z, uint seed)
        {
            uint h = (uint)x * 73856093u ^ (uint)y * 19349663u ^ (uint)z * 83492791u ^ seed * 2654435761u;
            h ^= h >> 16;
            h *= 0x7feb352du;
            h ^= h >> 15;
            h *= 0x846ca68bu;
            return h ^ h >> 16;
        }

        static float Unit(uint h)
        {
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }
}
