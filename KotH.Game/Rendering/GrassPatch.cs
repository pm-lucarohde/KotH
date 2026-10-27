using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// The one patch of grass GrassField draws many times (2 × 2 m, 20 × 21
    /// blades, five flower places): every vertex only says which blade it
    /// belongs to and where on it, GrassBlade builds the blade from that.
    /// </summary>
    static class GrassPatch
    {
        public const float Size = 2f;
        // Cells across x and z: 420 blades, 105 a square metre.
        const int CellsX = 20, CellsZ = 21;
        // Flower places (cell x, cell z), numbered after the blades.
        static readonly (int X, int Z)[] Flowers = { (3, 4), (9, 2), (6, 10), (1, 9), (11, 7) };

        [StructLayout(LayoutKind.Sequential)]
        struct Vertex
        {
            public Vector3 Position;
            public Vector3 Normal;
            // across (-1..1), along (0..1), kind (0 blade, 1 stem, 2 petals), blade number
            public Vector4 Data;

            public static readonly VertexDeclaration Layout = new VertexDeclaration(
                VertexElement.Position<Vector3>(), VertexElement.Normal<Vector3>(), VertexElement.Color<Vector4>());
        }

        /// <summary>Blades in a patch; GrassBlade's rank of a blade is its number over this.</summary>
        public const int Blades = CellsX * CellsZ;

        /// <summary>
        /// The patch's mesh with the blades numbered below count (and the
        /// flowers): far off GrassBlade keeps only the low numbers, so a far
        /// patch can leave the rest out. Its box takes the ground's heights and
        /// the blades' lean.
        /// </summary>
        public static Mesh Build(GraphicsDevice device, int count)
        {
            var vertices = new List<Vertex>();
            var indices = new List<int>();
            int[] cells = Order();
            for (int id = 0; id < count; id++)
                Blade(vertices, indices, Centre(cells[id] / CellsZ, cells[id] % CellsZ), 0f, id, count < Blades ? 1 : 2);
            for (int k = 0; k < Flowers.Length; k++)
            {
                Vector3 at = Centre(Flowers[k].X, Flowers[k].Z);
                int id = Blades + k;
                Blade(vertices, indices, at, 1f, id, 2);
                Petals(vertices, indices, at, id);
            }

            Buffer vb = Buffer.Vertex.New(device, vertices.ToArray(), GraphicsResourceUsage.Immutable);
            Buffer ib = Buffer.Index.New(device, indices.ToArray());
            return new Mesh
            {
                Draw = new MeshDraw
                {
                    PrimitiveType = PrimitiveType.TriangleList,
                    DrawCount = indices.Count,
                    IndexBuffer = new IndexBufferBinding(ib, true, indices.Count),
                    VertexBuffers = new[] { new VertexBufferBinding(vb, Vertex.Layout, vertices.Count) },
                },
                BoundingBox = new BoundingBox(new Vector3(-1f, 0f, -1f), new Vector3(Size + 1f, 4f, Size + 1f)),
            };
        }

        /// <summary>
        /// The cells by blade number: first every other cell of every other row,
        /// then the rest likewise, each group shuffled; so the low numbers a far
        /// patch keeps are spread evenly over it.
        /// </summary>
        static int[] Order()
        {
            var random = new Random(7);
            var cells = new List<(int Group, double Key, int Cell)>();
            for (int x = 0; x < CellsX; x++)
                for (int z = 0; z < CellsZ; z++)
                    cells.Add(((x & 1) == 0 ? ((z & 1) == 0 ? 0 : 2) : ((z & 1) == 1 ? 1 : 3), random.NextDouble(), x * CellsZ + z));
            cells.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Key.CompareTo(b.Key));
            return cells.ConvertAll(c => c.Cell).ToArray();
        }

        static Vector3 Centre(int x, int z) => new Vector3((x + 0.5f) * Size / CellsX, 0f, (z + 0.5f) * Size / CellsZ);

        // Two edges up the blade in steps, then the tip: near two steps (5
        // corners, 3 triangles), far one triangle; flower stems keep two.
        static void Blade(List<Vertex> vertices, List<int> indices, Vector3 root, float kind, int id, int steps)
        {
            int first = vertices.Count;
            for (int step = 0; step < steps; step++)
                for (int edge = -1; edge <= 1; edge += 2)
                    vertices.Add(new Vertex { Position = root, Normal = Vector3.UnitY, Data = new Vector4(edge, step / (float)steps, kind, id) });
            vertices.Add(new Vertex { Position = root, Normal = Vector3.UnitY, Data = new Vector4(0f, 1f, kind, id) });
            for (int step = 0; step < steps - 1; step++)
            {
                int a = first + 2 * step;
                indices.AddRange(new[] { a, a + 1, a + 3, a, a + 3, a + 2 });
            }
            int last = first + 2 * (steps - 1);
            indices.AddRange(new[] { last, last + 1, last + 2 });
        }

        // A flat five-petal star: the middle, tips at 1 and notches between them.
        static void Petals(List<Vertex> vertices, List<int> indices, Vector3 root, int id)
        {
            int middle = vertices.Count;
            vertices.Add(new Vertex { Position = root, Normal = Vector3.UnitY, Data = new Vector4(0f, 0f, 2f, id) });
            for (int k = 0; k < 10; k++)
            {
                float angle = k * MathUtil.TwoPi / 10f, r = k % 2 == 0 ? 1f : 0.45f;
                vertices.Add(new Vertex { Position = root, Normal = Vector3.UnitY, Data = new Vector4(r * MathF.Cos(angle), r * MathF.Sin(angle), 2f, id) });
            }
            for (int k = 0; k < 10; k++)
                indices.AddRange(new[] { middle, middle + 1 + k, middle + 1 + (k + 1) % 10 });
        }
    }
}
