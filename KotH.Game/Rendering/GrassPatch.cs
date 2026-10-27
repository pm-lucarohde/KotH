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
    /// The one patch of grass GrassField draws many times (2 × 2 m, 19 × 19
    /// blades, five flower places): every vertex only says which blade it
    /// belongs to and where on it, GrassBlade builds the blade from that.
    /// </summary>
    static class GrassPatch
    {
        public const float Size = 2f;
        const int Cells = 19;
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

        /// <summary>The patch's mesh; its box takes the ground's heights and the blades' lean.</summary>
        public static Mesh Build(GraphicsDevice device)
        {
            var vertices = new List<Vertex>();
            var indices = new List<int>();
            for (int x = 0; x < Cells; x++)
                for (int z = 0; z < Cells; z++)
                    Blade(vertices, indices, Centre(x, z), 0f, x * Cells + z);
            for (int k = 0; k < Flowers.Length; k++)
            {
                Vector3 at = Centre(Flowers[k].X, Flowers[k].Z);
                int id = Cells * Cells + k;
                Blade(vertices, indices, at, 1f, id);
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

        static Vector3 Centre(int x, int z) => new Vector3((x + 0.5f) * Size / Cells, 0f, (z + 0.5f) * Size / Cells);

        // Two edges up the blade in three steps, then the tip: 7 corners, 5 triangles.
        static void Blade(List<Vertex> vertices, List<int> indices, Vector3 root, float kind, int id)
        {
            int first = vertices.Count;
            for (int step = 0; step < 3; step++)
                for (int edge = -1; edge <= 1; edge += 2)
                    vertices.Add(new Vertex { Position = root, Normal = Vector3.UnitY, Data = new Vector4(edge, step / 3f, kind, id) });
            vertices.Add(new Vertex { Position = root, Normal = Vector3.UnitY, Data = new Vector4(0f, 1f, kind, id) });
            for (int step = 0; step < 2; step++)
            {
                int a = first + 2 * step;
                indices.AddRange(new[] { a, a + 1, a + 3, a, a + 3, a + 2 });
            }
            indices.AddRange(new[] { first + 4, first + 5, first + 6 });
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
