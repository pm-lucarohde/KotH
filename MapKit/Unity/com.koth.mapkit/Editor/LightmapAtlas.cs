using System;
using System.Collections.Generic;
using KotH.MapFormat;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace KotH.MapKit.Editor
{
    /// <summary>The lightmap atlas for the baked meshes: each gets a square of the 4096² map by the
    /// root of its surface area (shelf-packed), filled with its own lightmap UVs (Unity's "Generate
    /// Lightmap UVs", a terrain's grid) or, without them, Unity's unwrapper.</summary>
    static class LightmapAtlas
    {
        const int Size = 4096, Pad = 3, MinCell = 8;

        public static void Run(List<MapMesh> baked, Dictionary<MapMesh, float[]> own, List<string> notes)
        {
            var sides = new float[baked.Count];
            for (int i = 0; i < baked.Count; i++)
                sides[i] = (float)Math.Sqrt(Area(baked[i]));
            var order = new int[baked.Count];
            for (int i = 0; i < order.Length; i++)
                order[i] = i;
            Array.Sort(order, (a, b) => sides[b].CompareTo(sides[a]));

            // The largest scale at which every square fits, by bisection.
            double lo = 0, hi = Size;
            var cells = new Rect[baked.Count];
            for (int k = 0; k < 40; k++)
            {
                double mid = (lo + hi) / 2;
                if (Pack(sides, order, mid, cells))
                    lo = mid;
                else
                    hi = mid;
            }
            if (!Pack(sides, order, lo, cells))
                throw new InvalidOperationException("The lightmap atlas has no room for every baked mesh.");

            for (int i = 0; i < baked.Count; i++)
            {
                MapMesh m = baked[i];
                if (!own.TryGetValue(m, out float[] uv) || !Inside(uv))
                    uv = Unwrap(m, cells[i].width * Size);
                if (uv == null)
                {
                    m.Shadows = MapShadows.Live;
                    notes.Add($"{m.Name}: no lightmap UVs could be made, its shadow is live.");
                    continue;
                }
                Rect c = cells[i];
                m.Uv1 = new float[uv.Length];
                for (int j = 0; j < uv.Length; j += 2)
                {
                    m.Uv1[j] = c.x + uv[j] * c.width;
                    m.Uv1[j + 1] = c.y + uv[j + 1] * c.height;
                }
            }
        }

        // Shelves of squares, largest first, Pad texels round each; false if they overflow.
        static bool Pack(float[] sides, int[] order, double scale, Rect[] cells)
        {
            double gap = 2.0 * Pad / Size, x = 0, y = 0, shelf = 0;
            foreach (int i in order)
            {
                double cell = Math.Max(sides[i] * scale, MinCell) / Size;
                if (cell + gap > 1)
                    return false;
                if (x + cell + gap > 1)
                {
                    x = 0;
                    y += shelf;
                    shelf = 0;
                }
                if (y + cell + gap > 1)
                    return false;
                cells[i] = new Rect((float)(x + gap / 2), (float)(y + gap / 2), (float)cell, (float)cell);
                x += cell + gap;
                shelf = Math.Max(shelf, cell + gap);
            }
            return true;
        }

        static bool Inside(float[] uv)
        {
            foreach (float f in uv)
                if (f < 0f || f > 1f || float.IsNaN(f))
                    return false;
            return true;
        }

        static double Area(MapMesh m)
        {
            double sum = 0;
            float[] p = m.Positions;
            for (int t = 0; t + 2 < m.Indices.Length; t += 3)
            {
                int a = 3 * m.Indices[t], b = 3 * m.Indices[t + 1], c = 3 * m.Indices[t + 2];
                var ab = new Vector3(p[b] - p[a], p[b + 1] - p[a + 1], p[b + 2] - p[a + 2]);
                var ac = new Vector3(p[c] - p[a], p[c + 1] - p[a + 1], p[c + 2] - p[a + 2]);
                sum += Vector3.Cross(ab, ac).magnitude / 2;
            }
            return Math.Max(sum, 1e-4);
        }

        // Unity's unwrapper on the mesh; it splits vertices along seams, so every array comes back.
        static float[] Unwrap(MapMesh m, float pixels)
        {
            int count = m.VertexCount;
            var mesh = new Mesh { indexFormat = count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            try
            {
                var vertices = new Vector3[count];
                for (int v = 0; v < count; v++)
                    vertices[v] = new Vector3(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]);
                mesh.vertices = vertices;
                if (m.Normals != null)
                    mesh.normals = Vectors3(m.Normals);
                if (m.Uv0 != null)
                    mesh.uv = Vectors2(m.Uv0);
                mesh.triangles = m.Indices;
                UnwrapParam.SetDefaults(out UnwrapParam settings);
                settings.packMargin = Mathf.Clamp(2f * Pad / Math.Max(pixels, 1f), 0.002f, 0.25f);
                Unwrapping.GenerateSecondaryUVSet(mesh, settings);
                Vector2[] uv2 = mesh.uv2;
                if (uv2.Length != mesh.vertexCount)
                    return null;
                m.Positions = Floats(mesh.vertices);
                m.Normals = m.Normals != null ? Floats(mesh.normals) : null;
                m.Uv0 = m.Uv0 != null ? Floats(mesh.uv) : null;
                m.Indices = mesh.triangles;
                return Floats(uv2);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        static Vector3[] Vectors3(float[] f)
        {
            var v = new Vector3[f.Length / 3];
            for (int i = 0; i < v.Length; i++)
                v[i] = new Vector3(f[3 * i], f[3 * i + 1], f[3 * i + 2]);
            return v;
        }

        static Vector2[] Vectors2(float[] f)
        {
            var v = new Vector2[f.Length / 2];
            for (int i = 0; i < v.Length; i++)
                v[i] = new Vector2(f[2 * i], f[2 * i + 1]);
            return v;
        }

        static float[] Floats(Vector3[] v)
        {
            var f = new float[v.Length * 3];
            for (int i = 0; i < v.Length; i++)
            {
                f[3 * i] = v[i].x;
                f[3 * i + 1] = v[i].y;
                f[3 * i + 2] = v[i].z;
            }
            return f;
        }

        static float[] Floats(Vector2[] v)
        {
            var f = new float[v.Length * 2];
            for (int i = 0; i < v.Length; i++)
            {
                f[2 * i] = v[i].x;
                f[2 * i + 1] = v[i].y;
            }
            return f;
        }
    }
}
