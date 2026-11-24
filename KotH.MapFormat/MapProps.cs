using System;
using System.Collections.Generic;

namespace KotH.MapFormat
{
    /// <summary>The game's ladders and trampolines as meshes (The Ladder's, Tools/maps/Ladder.cs), for the
    /// manifest's ladders and trampolines with Model on: added to a copy of the kit's model on export,
    /// so every kit draws the same ones. A trampoline's solid is a block under its top.</summary>
    public static class MapProps
    {
        // Linear colours: wood, the trampoline's rim, its mat and its stone foot.
        static readonly float[] Wood = { 0.30f, 0.17f, 0.08f, 1f };
        static readonly float[] Rim = { 0.62f, 0.10f, 0.06f, 1f };
        static readonly float[] Mat = { 0.03f, 0.03f, 0.035f, 1f };
        static readonly float[] Stone = { 0.42f, 0.40f, 0.37f, 1f };

        /// <summary>The model with the props added, or the model itself when there are none.</summary>
        public static MapModel With(MapModel model, MapManifest manifest)
        {
            if (!manifest.Ladders.Exists(l => l.Model) && !manifest.Trampolines.Exists(t => t.Model))
                return model;
            var all = new MapModel();
            all.Materials.AddRange(model.Materials);
            all.Textures.AddRange(model.Textures);
            all.Meshes.AddRange(model.Meshes);
            int wood = Material(all, "KotH ladder wood", Wood, 0.85f);
            int rim = Material(all, "KotH trampoline rim", Rim, 0.5f);
            int mat = Material(all, "KotH trampoline mat", Mat, 0.7f);
            int stone = Material(all, "KotH trampoline stone", Stone, 0.9f);
            for (int i = 0; i < manifest.Ladders.Count; i++)
                if (manifest.Ladders[i].Model)
                    all.Meshes.Add(Ladder(manifest.Ladders[i], $"Ladder {i + 1}", wood));
            for (int i = 0; i < manifest.Trampolines.Count; i++)
                if (manifest.Trampolines[i].Model)
                    Trampoline(all, manifest.Trampolines[i], $"Trampoline {i + 1}", rim, mat, stone, wood);
            return all;
        }

        static int Material(MapModel m, string name, float[] color, float roughness)
        {
            m.Materials.Add(new MapMaterial { Name = name, Template = "Surface", Color = color, Roughness = roughness });
            return m.Materials.Count - 1;
        }

        /// <summary>Two rails along the strip's edges and rungs 0.3 m apart, 5 cm off the wall; seen only.</summary>
        static MapMesh Ladder(MapLadder l, string name, int material)
        {
            float yaw = l.Yaw * (float)(Math.PI / 180);
            var out_ = new V(-(float)Math.Sin(yaw), 0f, -(float)Math.Cos(yaw));
            var along = new V(-out_.Z, 0f, out_.X);
            var foot = new V(l.X, l.Y, l.Z);
            float top = l.Y + l.Height, half = 0.3f * l.Width;
            V Face(float across, float y) => new V(foot.X, y, foot.Z) + out_ * 0.05f + along * across;
            var b = new Builder();
            foreach (int side in new[] { -1, 1 })
                b.Bar(Face(side * half, l.Y + 0.01f), Face(side * half, top), 0.06f, out_);
            for (float y = l.Y + 0.15f; y < top - 0.05f; y += 0.3f)
                b.Bar(Face(-half + 0.02f, y), Face(half - 0.02f, y), 0.045f, out_);
            return b.Mesh(name, material, MapCollide.None);
        }

        /// <summary>A round mat in a coloured rim on six wooden legs over a stone foot, as on The
        /// Ladder, scaled to the diameter; its solid is the square block from the top down.</summary>
        static void Trampoline(MapModel m, MapTrampoline t, string name, int rim, int mat, int stone, int wood)
        {
            float r = 0.5f * t.Diameter, top = t.Y, bottom = t.Y - Math.Max(0.2f, t.Height);
            float lip = Math.Min(0.12f, 0.3f * (top - bottom)), foot = bottom + 0.3f * (top - bottom - lip);
            var c = new V(t.X, 0f, t.Z);
            var b = new Builder();
            b.Lathe(c, new[] { (r, bottom), (r, foot), (0f, foot) }, 8);
            m.Meshes.Add(b.Mesh(name + " foot", stone, MapCollide.None));
            b = new Builder();
            float ring = 0.72f * r, edge = top - lip;
            b.Lathe(c, new[] { (ring, edge), (r * 0.96f, edge), (r, edge + 0.04f), (r, top - 0.03f), (r * 0.94f, top), (ring, top), (ring * 0.95f, top - 0.04f) }, 24);
            m.Meshes.Add(b.Mesh(name + " rim", rim, MapCollide.None));
            b = new Builder();
            b.Lathe(c, new[] { (ring, top - 0.04f), (0f, top - 0.04f) }, 24);
            m.Meshes.Add(b.Mesh(name + " mat", mat, MapCollide.None));
            b = new Builder();
            for (int k = 0; k < 6; k++)
            {
                float a = (float)((30 + 60 * k) * Math.PI / 180);
                var at = new V(t.X + 0.8f * r * (float)Math.Cos(a), 0f, t.Z + 0.8f * r * (float)Math.Sin(a));
                b.Bar(new V(at.X, foot, at.Z), new V(at.X, edge, at.Z), 0.07f, new V(1f, 0f, 0f));
            }
            m.Meshes.Add(b.Mesh(name + " legs", wood, MapCollide.None));
            b = new Builder();
            b.Bar(new V(t.X, bottom, t.Z), new V(t.X, top, t.Z), 2f * r, new V(1f, 0f, 0f));
            m.Meshes.Add(b.Mesh(name + " solid", stone, MapCollide.Only));
        }

        readonly struct V
        {
            public readonly float X, Y, Z;
            public V(float x, float y, float z) { X = x; Y = y; Z = z; }
            public static V operator +(V a, V b) => new V(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static V operator -(V a, V b) => new V(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static V operator *(V a, float s) => new V(a.X * s, a.Y * s, a.Z * s);
            public static V Cross(V a, V b) => new V(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
            public V Unit()
            {
                float l = (float)Math.Sqrt(X * X + Y * Y + Z * Z);
                return l > 1e-9f ? this * (1f / l) : this;
            }
        }

        /// <summary>Gathers quads with their normals and a 0..1 UV square each.</summary>
        sealed class Builder
        {
            readonly List<float> p = new List<float>(), n = new List<float>(), uv = new List<float>();
            readonly List<int> idx = new List<int>();

            /// <summary>A quad a b c d, counter-clockwise seen from outside, normals given per corner.</summary>
            public void Quad(V a, V b, V c, V d, V na, V nb, V nc, V nd)
            {
                int first = p.Count / 3;
                V[] at = { a, b, c, d }, normal = { na, nb, nc, nd };
                for (int i = 0; i < 4; i++)
                {
                    p.Add(at[i].X); p.Add(at[i].Y); p.Add(at[i].Z);
                    n.Add(normal[i].X); n.Add(normal[i].Y); n.Add(normal[i].Z);
                    uv.Add(i == 1 || i == 2 ? 1f : 0f); uv.Add(i >= 2 ? 1f : 0f);
                }
                idx.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
            }

            /// <summary>A square bar from a to b, width w, one pair of sides facing side.</summary>
            public void Bar(V a, V b, float w, V side)
            {
                V axis = (b - a).Unit();
                V s = (side - axis * (side.X * axis.X + side.Y * axis.Y + side.Z * axis.Z)).Unit() * (0.5f * w);
                V t = V.Cross(axis, s).Unit() * (0.5f * w);
                V[] ring = { s + t, s - t, s * -1f - t, s * -1f + t };
                for (int k = 0; k < 4; k++)
                {
                    V p0 = ring[k], p1 = ring[(k + 1) % 4];
                    V normal = (p0 + p1).Unit();
                    Quad(a + p1, a + p0, b + p0, b + p1, normal, normal, normal, normal);
                }
                V down = axis * -1f;
                Quad(a + ring[0], a + ring[1], a + ring[2], a + ring[3], down, down, down, down);
                Quad(b + ring[3], b + ring[2], b + ring[1], b + ring[0], axis, axis, axis, axis);
            }

            /// <summary>A profile of (radius, height) points turned round the vertical through c, in
            /// the given number of sectors; each profile step a ring of quads with smooth sides.</summary>
            public void Lathe(V c, (float R, float Y)[] profile, int sectors)
            {
                for (int i = 0; i + 1 < profile.Length; i++)
                {
                    (float r0, float y0) = profile[i];
                    (float r1, float y1) = profile[i + 1];
                    // The step's outward normal in the (radius, height) plane.
                    float nr = y1 - y0, ny = r0 - r1, l = (float)Math.Sqrt(nr * nr + ny * ny);
                    if (l < 1e-6f)
                        continue;
                    nr /= l;
                    ny /= l;
                    for (int k = 0; k < sectors; k++)
                    {
                        double a0 = 2 * Math.PI * k / sectors, a1 = 2 * Math.PI * (k + 1) / sectors;
                        var d0 = new V((float)Math.Cos(a0), 0f, (float)Math.Sin(a0));
                        var d1 = new V((float)Math.Cos(a1), 0f, (float)Math.Sin(a1));
                        V n0 = d0 * nr + new V(0f, ny, 0f), n1 = d1 * nr + new V(0f, ny, 0f);
                        Quad(c + d1 * r0 + new V(0f, y0, 0f), c + d0 * r0 + new V(0f, y0, 0f),
                             c + d0 * r1 + new V(0f, y1, 0f), c + d1 * r1 + new V(0f, y1, 0f), n1, n0, n0, n1);
                    }
                }
            }

            public MapMesh Mesh(string name, int material, MapCollide collide) => new MapMesh
            {
                Name = name, Material = material, Collide = collide, Shadows = MapShadows.Baked,
                Positions = p.ToArray(), Normals = n.ToArray(), Uv0 = uv.ToArray(), Indices = idx.ToArray(),
            };
        }
    }
}
