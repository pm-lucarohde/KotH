using System;
using System.Collections.Generic;
using KotH.MapFormat;
using Prowl.Unwrapper;
using Prowl.Vector;

namespace KotH.MapKit
{
    /// <summary>The lightmap atlas for the baked meshes with Prowl.Unwrapper, as Tools/maps/MapUV
    /// does it for Mount Destiny: all faces in one unwrap, so they share the 4096² map; a corner
    /// whose vertex has another UV elsewhere gets a copy. Kept out of KotH.MapFormat (Unity has its
    /// own unwrapper); the Stride kit and Tools/maps/SampleMap.cs compile it in.</summary>
    public static class LightmapUnwrap
    {
        public const int Size = 4096;
        public const int Pad = 3;

        public static void Run(List<MapMesh> baked)
        {
            var positions = new List<Double3>();
            var normals = new List<Double3>();
            var triangles = new List<int>();
            foreach (MapMesh m in baked)
            {
                int first = positions.Count;
                for (int v = 0; v < m.VertexCount; v++)
                {
                    positions.Add(new Double3(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]));
                    normals.Add(m.Normals != null ? new Double3(m.Normals[3 * v], m.Normals[3 * v + 1], m.Normals[3 * v + 2])
                                                  : new Double3(0, 1, 0));
                }
                foreach (int i in m.Indices)
                    triangles.Add(first + i);
            }
            if (triangles.Count == 0)
                return;
            UnwrapResult result = new UnwrapMesh(positions.ToArray(), triangles.ToArray())
                .WithNormals(normals.ToArray())
                .Unwrap(new UnwrapOptions { PackMargin = (double)Pad / Size, Method = UnwrapMethod.Projection });

            int corner = 0;
            foreach (MapMesh m in baked)
            {
                var copies = new Dictionary<(int, float, float), int>();
                var from = new List<int>();
                var uv1 = new List<float>();
                var indices = new int[m.Indices.Length];
                for (int i = 0; i < indices.Length; i++, corner++)
                {
                    Double2 uv = result.PerCornerUVs[corner];
                    var key = (m.Indices[i], (float)uv.X, (float)uv.Y);
                    if (!copies.TryGetValue(key, out int index))
                    {
                        copies[key] = index = from.Count;
                        from.Add(m.Indices[i]);
                        uv1.Add((float)uv.X);
                        uv1.Add((float)uv.Y);
                    }
                    indices[i] = index;
                }
                m.Positions = Pick(m.Positions, 3, from);
                m.Normals = m.Normals == null ? null : Pick(m.Normals, 3, from);
                m.Uv0 = m.Uv0 == null ? null : Pick(m.Uv0, 2, from);
                m.Uv1 = uv1.ToArray();
                m.Indices = indices;
            }
        }

        static float[] Pick(float[] data, int width, List<int> from)
        {
            var picked = new float[from.Count * width];
            for (int i = 0; i < from.Count; i++)
                Array.Copy(data, from[i] * width, picked, i * width, width);
            return picked;
        }
    }
}
