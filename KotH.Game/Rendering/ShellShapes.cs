using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// The three shells ShellField scatters on the beach, built in a unit
    /// space: lying on y = 0, the widest extent across x and z is 1. A scallop
    /// (fan of ribs from the hinge, with its ears), a cockle (round, ribs from
    /// the off-centre umbo, higher) and a small spiral snail on its side. Soft
    /// colours, darker grooves and growth bands painted per vertex.
    /// </summary>
    static class ShellShapes
    {
        /// <summary>One shell: corners with their normal and colour, triangles.</summary>
        public sealed class Piece
        {
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Color4> Colors = new List<Color4>();
            public readonly List<int> Indices = new List<int>();

            int Add(Vector3 position, Color4 color)
            {
                Positions.Add(position);
                Normals.Add(Vector3.Zero);
                Colors.Add(color);
                return Positions.Count - 1;
            }

            void Triangle(int a, int b, int c) => Indices.AddRange(new[] { a, b, c });

            // Smooth normals of a shell seen from above: every face counted facing up.
            void UpNormals()
            {
                for (int t = 0; t < Indices.Count; t += 3)
                {
                    int a = Indices[t], b = Indices[t + 1], c = Indices[t + 2];
                    Vector3 n = Vector3.Cross(Positions[b] - Positions[a], Positions[c] - Positions[a]);
                    if (n.Y < 0f)
                        n = -n;
                    Normals[a] += n;
                    Normals[b] += n;
                    Normals[c] += n;
                }
                for (int i = 0; i < Normals.Count; i++)
                    Normals[i] = Normals[i].LengthSquared() > 0f ? Vector3.Normalize(Normals[i]) : Vector3.UnitY;
            }

            // Every triangle wound clockwise seen from where its corners' normals
            // point: Stride's front face; a face seen from behind is culled.
            void Orient()
            {
                for (int t = 0; t < Indices.Count; t += 3)
                {
                    int a = Indices[t], b = Indices[t + 1], c = Indices[t + 2];
                    Vector3 n = Vector3.Cross(Positions[b] - Positions[a], Positions[c] - Positions[a]);
                    if (Vector3.Dot(n, Normals[a] + Normals[b] + Normals[c]) > 0f)
                        (Indices[t + 1], Indices[t + 2]) = (c, b);
                }
            }

            // Lying on y = 0, the widest extent across 1, centred over the origin.
            void Fit()
            {
                Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);
                foreach (Vector3 p in Positions)
                {
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                float scale = 1f / Math.Max(max.X - min.X, max.Z - min.Z);
                var shift = new Vector3(-(min.X + max.X) / 2f, -min.Y, -(min.Z + max.Z) / 2f);
                for (int i = 0; i < Positions.Count; i++)
                    Positions[i] = (Positions[i] + shift) * scale;
            }

            /// <summary>
            /// A bivalve as a height field over a fan round its umbo at the origin:
            /// round all the way (cockle) or a fan of ±62° (scallop, with ears).
            /// </summary>
            public static Piece Bivalve(bool round, Color4 main, Color4 band, Random random)
            {
                var piece = new Piece();
                int ribs = round ? 16 + random.Next(5) : 9 + random.Next(3);
                int per = round ? 2 : 3, rings = 5;
                int columns = ribs * per + (round ? 0 : 1);
                float span = round ? MathF.PI : MathUtil.DegreesToRadians(62f);
                float height = round ? 0.40f : 0.17f, groove = round ? 0.02f : 0.035f;

                // The scallop's dome peaks half way out, the cockle's at the umbo.
                float Dome(float s, float angle) => round
                    ? height * MathF.Pow(1f - s * s, 0.7f)
                    : height * 4f * s * (1f - s) * MathF.Sqrt(Math.Max(0f, MathF.Cos(angle / span * MathUtil.PiOverTwo)));
                Color4 Paint(float s, float rib)
                {
                    float stripe = round ? (MathF.Abs(s - 0.55f) < 0.06f || MathF.Abs(s - 0.8f) < 0.05f ? 1f : 0f) : ((int)(s * 4.99f) & 1);
                    Color4 c = Color4.Lerp(main, band, (round ? 0.5f : 0.4f) * stripe);
                    float light = 0.84f + 0.2f * rib - (s > 0.9f ? 0.12f : 0f);
                    return new Color4(c.R * light, c.G * light, c.B * light, 1f);
                }

                int umbo = piece.Add(new Vector3(0f, round ? height : 0.01f, 0f), Paint(0f, 1f));
                var grid = new int[rings, columns];
                for (int k = 0; k < rings; k++)
                {
                    float s = (k + 1) / (float)rings;
                    for (int c = 0; c < columns; c++)
                    {
                        float angle = round ? MathUtil.TwoPi * c / columns : -span + 2f * span * c / (columns - 1);
                        float rib = 0.5f + 0.5f * MathF.Cos(MathUtil.TwoPi * c / per);
                        float reach = (round ? 0.62f + 0.38f * MathF.Cos(angle) : 1f - 0.1f * (angle / span) * (angle / span)) * (1f + 0.04f * rib * s);
                        float y = Dome(s, angle) + groove * rib * s * (1.2f - s);
                        grid[k, c] = piece.Add(new Vector3(MathF.Sin(angle) * s * reach, y, MathF.Cos(angle) * s * reach), Paint(s, rib));
                    }
                }
                int last = round ? columns : columns - 1;
                for (int c = 0; c < last; c++)
                {
                    int d = (c + 1) % columns;
                    piece.Triangle(umbo, grid[0, c], grid[0, d]);
                    for (int k = 0; k + 1 < rings; k++)
                    {
                        piece.Triangle(grid[k, c], grid[k + 1, c], grid[k + 1, d]);
                        piece.Triangle(grid[k, c], grid[k + 1, d], grid[k, d]);
                    }
                }
                if (!round)
                    for (int side = -1; side <= 1; side += 2)
                    {
                        // The ear: from the hinge out along it, back to the fan's edge.
                        Color4 ear = Paint(0.2f, 0.5f);
                        int hinge = piece.Add(new Vector3(0f, 0.012f, -0.01f), ear);
                        int tip = piece.Add(new Vector3(side * 0.3f, 0.01f, -0.02f), ear);
                        int corner = piece.Add(new Vector3(side * 0.31f, 0.012f, 0.14f), ear);
                        int edge = grid[1, side < 0 ? 0 : columns - 1];
                        piece.Triangle(hinge, tip, corner);
                        piece.Triangle(hinge, corner, edge);
                    }
                piece.UpNormals();
                piece.Orient();
                piece.Fit();
                return piece;
            }

            /// <summary>
            /// A small spiral snail: a tube growing round its axis by a factor
            /// a whorl, apex up; then laid on its side and its mouth closed.
            /// </summary>
            public static Piece Snail(Color4 main, Color4 band, Random random)
            {
                var piece = new Piece();
                const int sides = 6, perWhorl = 8;
                const float axisOut = 0.5f, tube = 0.62f, drop = 4f, growth = 1.3f;
                float whorls = 4f + 0.8f * (float)random.NextDouble();
                int steps = (int)(whorls * perWhorl);
                int stripe = random.Next(sides);
                Color4 inside = new Color4(0.82f, 0.52f, 0.42f, 1f);

                var centres = new Vector3[steps + 1];
                var rings = new int[steps + 1, sides];
                for (int i = 0; i <= steps; i++)
                {
                    float turn = MathUtil.TwoPi * whorls * i / steps;
                    float size = MathF.Pow(growth, (turn - MathUtil.TwoPi * whorls) / MathUtil.TwoPi);
                    var outward = new Vector3(MathF.Cos(turn), 0f, MathF.Sin(turn));
                    centres[i] = new Vector3(outward.X * axisOut * size, -drop * size, outward.Z * axisOut * size);
                    for (int j = 0; j < sides; j++)
                    {
                        float around = MathUtil.TwoPi * j / sides;
                        Vector3 normal = outward * MathF.Cos(around) + Vector3.UnitY * MathF.Sin(around);
                        float shade = i < perWhorl ? 0.75f : 1f;
                        Color4 c = j == stripe ? band : main;
                        rings[i, j] = piece.Add(centres[i] + normal * (tube * size), new Color4(c.R * shade, c.G * shade, c.B * shade, 1f));
                        piece.Normals[rings[i, j]] = normal;
                    }
                }
                for (int i = 0; i < steps; i++)
                    for (int j = 0; j < sides; j++)
                    {
                        int k = (j + 1) % sides;
                        piece.Triangle(rings[i, j], rings[i + 1, j], rings[i + 1, k]);
                        piece.Triangle(rings[i, j], rings[i + 1, k], rings[i, k]);
                    }
                // The mouth: a fan facing on along the tube.
                Vector3 ahead = Vector3.Normalize(centres[steps] - centres[steps - 1]);
                int middle = piece.Add(centres[steps], inside);
                piece.Normals[middle] = ahead;
                var mouth = new int[sides];
                for (int j = 0; j < sides; j++)
                {
                    mouth[j] = piece.Add(piece.Positions[rings[steps, j]], inside);
                    piece.Normals[mouth[j]] = ahead;
                }
                for (int j = 0; j < sides; j++)
                    piece.Triangle(middle, mouth[j], mouth[(j + 1) % sides]);

                // On its side: the axis level, then tipped by the cone's half angle so a flank rests on the ground.
                float lean = MathUtil.PiOverTwo + MathF.Atan((axisOut + tube) / drop);
                Matrix lay = Matrix.RotationZ(lean);
                for (int i = 0; i < piece.Positions.Count; i++)
                {
                    piece.Positions[i] = Vector3.TransformCoordinate(piece.Positions[i], lay);
                    piece.Normals[i] = Vector3.Normalize(Vector3.TransformNormal(piece.Normals[i], lay));
                }
                piece.Orient();
                piece.Fit();
                return piece;
            }
        }

        // Soft beach shades (linear): white, cream, pink, ochre; the second a darker or warmer partner for bands.
        static readonly (Color4 Main, Color4 Band)[] Palette =
        {
            (new Color4(0.86f, 0.82f, 0.70f, 1f), new Color4(0.78f, 0.56f, 0.40f, 1f)),
            (new Color4(0.80f, 0.68f, 0.48f, 1f), new Color4(0.62f, 0.38f, 0.20f, 1f)),
            (new Color4(0.86f, 0.56f, 0.50f, 1f), new Color4(0.90f, 0.78f, 0.70f, 1f)),
            (new Color4(0.70f, 0.40f, 0.16f, 1f), new Color4(0.86f, 0.72f, 0.52f, 1f)),
        };

        /// <summary>A shell of the given kind (0 scallop, 1 cockle, 2 snail) in one of the colours.</summary>
        public static Piece Make(int kind, Random random)
        {
            var (main, band) = Palette[random.Next(Palette.Length)];
            return kind switch
            {
                0 => Piece.Bivalve(false, main, band, random),
                1 => Piece.Bivalve(true, main, band, random),
                _ => Piece.Snail(main, new Color4(0.38f, 0.2f, 0.09f, 1f), random),
            };
        }
    }
}
