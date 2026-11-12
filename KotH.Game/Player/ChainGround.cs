using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KotH.MapFormat;
using Stride.Core.Mathematics;

namespace KotH.Client.Player
{
    /// <summary>The map's ground triangles in a grid of 1 m cells, for the chain's many short rays: about a
    /// microsecond each on any map (Bepu's chunk compounds take up to 30 µs on Atrium). Faces are hit from
    /// outside only, as Bepu's (wound into the solid). Presentation only; built once per map, read only after.</summary>
    sealed class ChainGround
    {
        const float Cell = 1f;
        const int Bias = 1 << 20;
        const float Edge = 1e-4f;
        const float Margin = 0.01f, Slack = 1e-3f;   // box growth: 1 cm plus 10 times the edges' slack

        // Bepu's winding: seen from outside the corners run clockwise, so det < 0 and the face's
        // normal (outwards) is -(e1 x e2). Checked hit for hit against Bepu on Atrium and MountDestiny.
        const float Outside = -1f;

        // A face's corner and edges as the test takes them, and its box grown by more than the test's
        // slack over the edges: a ray whose segment misses the box cannot hit the face.
        struct Face
        {
            public Vector3 A, E1, E2, Low, High;
        }

        readonly float[] v;
        readonly int[] corners;                      // vertex triples, one per triangle
        readonly Face[] faces;
        readonly Dictionary<long, (int Start, int Count)> cells = new Dictionary<long, (int, int)>();
        readonly int[] members;                      // triangle numbers, cell after cell

        // Per thread (ropes move in parallel): the query that last tested a triangle.
        [ThreadStatic] static int[] seen;
        [ThreadStatic] static int query;

        public MapCollision Source { get; }

        public ChainGround(MapCollision collision)
        {
            Source = collision;
            v = collision.Vertices;
            var all = new List<int>();
            foreach (MapCollision.Chunk chunk in collision.Chunks)
                all.AddRange(chunk.Triangles);
            corners = all.ToArray();
            int triangles = corners.Length / 3;
            faces = new Face[triangles];
            for (int t = 0; t < triangles; t++)
            {
                Vector3 a = Corner(t, 0), b = Corner(t, 1), c = Corner(t, 2);
                Vector3 low = Vector3.Min(a, Vector3.Min(b, c)), high = Vector3.Max(a, Vector3.Max(b, c));
                Vector3 size = high - low;
                var grow = new Vector3(Margin + Slack * Math.Max(size.X, Math.Max(size.Y, size.Z)));
                faces[t] = new Face { A = a, E1 = b - a, E2 = c - a, Low = low - grow, High = high + grow };
            }

            // Twice over the triangles: count per cell, then file each into every cell its box touches.
            var counts = new Dictionary<long, int>();
            for (int t = 0; t < triangles; t++)
                Cells(t, key => counts[key] = counts.GetValueOrDefault(key) + 1);
            int next = 0;
            foreach (KeyValuePair<long, int> kv in counts)
            {
                cells[kv.Key] = (next, 0);
                next += kv.Value;
            }
            members = new int[next];
            for (int t = 0; t < triangles; t++)
            {
                int tri = t;
                Cells(t, key =>
                {
                    (int start, int count) = cells[key];
                    members[start + count] = tri;
                    cells[key] = (start, count + 1);
                });
            }
        }

        void Cells(int t, Action<long> each)
        {
            Vector3 a = Corner(t, 0), b = Corner(t, 1), c = Corner(t, 2);
            Vector3 lo = Vector3.Min(a, Vector3.Min(b, c)), hi = Vector3.Max(a, Vector3.Max(b, c));
            int x0 = Index(lo.X), y0 = Index(lo.Y), z0 = Index(lo.Z), x1 = Index(hi.X), y1 = Index(hi.Y), z1 = Index(hi.Z);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                        each(Key(x, y, z));
        }

        Vector3 Corner(int t, int i)
        {
            int k = 3 * corners[3 * t + i];
            return new Vector3(v[k], v[k + 1], v[k + 2]);
        }

        static int Index(float x) => (int)MathF.Floor(x / Cell);

        static long Key(int x, int y, int z) => ((long)(x + Bias) << 42) | ((long)(y + Bias) << 21) | (long)(z + Bias);

        /// <summary>The nearest face along the ray within length: walks the cells it passes (Amanatides-Woo)
        /// and stops once a hit lies before the cell's far side.</summary>
        public bool Raycast(Vector3 from, Vector3 dir, float length, out float distance, out Vector3 point, out Vector3 normal)
        {
            distance = float.MaxValue;
            point = normal = default;
            if (seen == null || seen.Length != faces.Length)
                seen = new int[faces.Length];
            int[] marks = seen;
            int mark = ++query;
            Vector3 end = from + dir * length, low = Vector3.Min(from, end), high = Vector3.Max(from, end);
            int x = Index(from.X), y = Index(from.Y), z = Index(from.Z);
            int sx = Math.Sign(dir.X), sy = Math.Sign(dir.Y), sz = Math.Sign(dir.Z);
            float nx = Next(from.X, dir.X, x), ny = Next(from.Y, dir.Y, y), nz = Next(from.Z, dir.Z, z);
            float dx = Step(dir.X), dy = Step(dir.Y), dz = Step(dir.Z);
            while (true)
            {
                if (cells.TryGetValue(Key(x, y, z), out (int Start, int Count) cell))
                    for (int i = 0; i < cell.Count; i++)
                    {
                        int t = members[cell.Start + i];
                        if (marks[t] == mark)
                            continue;
                        marks[t] = mark;
                        ref readonly Face f = ref faces[t];
                        if (f.High.X < low.X || f.Low.X > high.X || f.High.Y < low.Y || f.Low.Y > high.Y
                            || f.High.Z < low.Z || f.Low.Z > high.Z)
                            continue;
                        Test(in f, from, dir, length, ref distance, ref normal);
                    }
                float leave = Math.Min(nx, Math.Min(ny, nz));
                if (distance <= leave || leave > length)
                    break;
                if (nx == leave) { x += sx; nx += dx; }
                else if (ny == leave) { y += sy; ny += dy; }
                else { z += sz; nz += dz; }
            }
            if (distance > length)
                return false;
            point = from + dir * distance;
            return true;
        }

        static float Next(float p, float d, int i)
            => d > 0f ? ((i + 1) * Cell - p) / d : d < 0f ? (i * Cell - p) / d : float.MaxValue;

        static float Step(float d) => d != 0f ? Cell / Math.Abs(d) : float.MaxValue;

        // Möller-Trumbore from outside, a hair over the edges so no ray slips through between two
        // faces; each triangle once per query though it sits in several cells.
        static void Test(in Face f, Vector3 from, Vector3 dir, float length, ref float distance, ref Vector3 normal)
        {
            Vector3 e1 = f.E1, e2 = f.E2;
            Vector3 p = Cross(dir, e2);
            float det = Dot(e1, p);
            if (det * Outside <= 1e-12f)
                return;
            float inv = 1f / det;
            Vector3 s = from - f.A;
            float u = Dot(s, p) * inv;
            if (u < -Edge || u > 1f + Edge)
                return;
            Vector3 q = Cross(s, e1);
            float w = Dot(dir, q) * inv;
            if (w < -Edge || u + w > 1f + Edge)
                return;
            float d = Dot(e2, q) * inv;
            if (d <= 1e-6f || d > length || d >= distance)
                return;
            distance = d;
            normal = Vector3.Normalize(Vector3.Cross(e1, e2)) * Outside;
        }

        // Stride's Cross and Dot term for term, inlined.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector3 Cross(Vector3 l, Vector3 r)
            => new Vector3(l.Y * r.Z - l.Z * r.Y, l.Z * r.X - l.X * r.Z, l.X * r.Y - l.Y * r.X);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Dot(Vector3 l, Vector3 r) => l.X * r.X + l.Y * r.Y + l.Z * r.Z;
    }
}
