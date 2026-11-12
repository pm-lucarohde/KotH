using System;
using System.Collections.Generic;
using System.Linq;

namespace KotH.Tools.Maps;

/// <summary>One material's triangles as they go into the glb: positions, normals, indices,
/// COLOR_0 (terrain weights, leaf data) and TEXCOORD_0 (x: the shade, or a leaf's atlas uv).</summary>
public sealed class Piece(string name)
{
    public readonly string Name = name;
    public readonly List<V3> Pos = new(), Nrm = new();
    public readonly List<(double, double, double, double)> Col = new();
    public readonly List<(double, double)> Uv = new();
    public readonly List<int> Idx = new();

    public int Add(V3 p, V3 n, (double, double, double, double) col, (double, double) uv)
    {
        Pos.Add(p);
        Nrm.Add(n);
        Uv.Add(uv);
        Col.Add(col);
        return Pos.Count - 1;
    }

    public Output.Part ToPart() => new(Name, Pos, Nrm, Idx, Col, Uv);
}

/// <summary>Atrium's shapes, made for a built arena rather than a landscape: lathes (an (r, y)
/// profile turned round an axis), straight bevelled bars, rock (a lathe pushed about by noise)
/// and MountDestiny's trees. Each goes into a material's piece and, when solid, the collision.</summary>
public sealed class Arena
{
    public readonly SortedDictionary<string, Piece> Pieces = new(StringComparer.Ordinal);
    public readonly List<float> SolidPos = new();
    public readonly List<int> SolidIdx = new();

    public static readonly (double, double, double, double) Plain = (0, 0, 0, 0);
    // TerrainTriplanar's COLOR_0: grass, sand, snow, cap; none of them is rock.
    public static readonly (double, double, double, double) Grass = (1, 0, 0, 0);

    public Piece PieceOf(string name)
    {
        if (!Pieces.TryGetValue(name, out Piece piece))
            Pieces[name] = piece = new Piece(name);
        return piece;
    }

    static V3 Add(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    static V3 Scale(V3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static V3 Unit(V3 v) => Mesh.Unit(v);

    /// <summary>A triangle wound counter-clockwise from outside: turned round when its face
    /// points against its corners' normals; slivers are left out.</summary>
    public void Triangle(Piece piece, int a, int b, int c, bool solid)
    {
        V3 pa = piece.Pos[a], pb = piece.Pos[b], pc = piece.Pos[c];
        V3 n = V3.Cross(V3.Sub(pb, pa), V3.Sub(pc, pa));
        if (Dot(n, n) < 1e-14)
            return;
        if (Dot(n, Add(Add(piece.Nrm[a], piece.Nrm[b]), piece.Nrm[c])) < 0)
            (b, c) = (c, b);
        piece.Idx.AddRange([a, b, c]);
        if (solid)
            foreach (int i in new[] { a, b, c })
            {
                SolidIdx.Add(SolidPos.Count / 3);
                SolidPos.AddRange([(float)piece.Pos[i].X, (float)piece.Pos[i].Y, (float)piece.Pos[i].Z]);
            }
    }

    static int Sides(double angle, double radius, double chord, int least)
        => Math.Max(least, (int)Math.Ceiling(angle * radius / chord));

    /// <summary>Turns a profile of (r, y) points round the vertical axis through (cx, cz), from
    /// angle a0 to a1 (radians; x = r cos, z = r sin). The profile runs counter-clockwise round
    /// the solid (r to the right, y up); a corner sharper than 40° keeps its edge. A part of a
    /// circle is closed at both ends by its profile (convex profiles only).</summary>
    public void Lathe(string material, (double R, double Y)[] profile, bool solid, double cx = 0, double cz = 0,
                      double a0 = 0, double a1 = 2 * Math.PI, double chord = 1.0, int least = 12,
                      (double, double, double, double)? colour = null, double shade = 1.0)
    {
        Piece piece = PieceOf(material);
        var col = colour ?? Plain;
        int sides = Sides(a1 - a0, profile.Max(p => p.R), chord, least);
        // Rings round the arena's middle share one angular grid, so where they meet their edges match.
        double[] turn = cx == 0 && cz == 0 ? Angles(a0, a1) : [.. Enumerable.Range(0, sides + 1).Select(j => a0 + (a1 - a0) * j / sides)];
        // Each segment's outward normal in the profile's plane, then per end blended or not.
        var seg = new (double R, double Y)[profile.Length - 1];
        for (int k = 0; k < seg.Length; k++)
        {
            double dr = profile[k + 1].R - profile[k].R, dy = profile[k + 1].Y - profile[k].Y, l = Math.Sqrt(dr * dr + dy * dy);
            seg[k] = l < 1e-9 ? (0, 0) : (dy / l, -dr / l);
        }
        (double R, double Y) End(int k, int at)
        {
            int other = at == 0 ? k - 1 : k + 1;
            if (other < 0 || other >= seg.Length || seg[other] == (0, 0) || seg[k].R * seg[other].R + seg[k].Y * seg[other].Y < Math.Cos(40 * Math.PI / 180))
                return seg[k];
            double r = seg[k].R + seg[other].R, y = seg[k].Y + seg[other].Y, l = Math.Sqrt(r * r + y * y);
            return (r / l, y / l);
        }
        for (int k = 0; k < seg.Length; k++)
        {
            if (seg[k] == (0, 0))
                continue;
            int first = piece.Pos.Count;
            foreach (double a in turn)
            {
                double c = Math.Cos(a), s = Math.Sin(a);
                foreach (int at in new[] { 0, 1 })
                {
                    var (r, y) = profile[k + at];
                    var (nr, ny) = End(k, at);
                    piece.Add(new V3(cx + r * c, y, cz + r * s), Unit(new V3(nr * c, ny, nr * s)), col, (shade, 0.0));
                }
            }
            for (int j = 0; j + 1 < turn.Length; j++)
            {
                int a = first + 2 * j, b = a + 1, d = a + 2, e = a + 3;
                Triangle(piece, a, b, e, solid);
                Triangle(piece, a, e, d, solid);
            }
        }
        if (a1 - a0 < 2 * Math.PI - 1e-9)
            foreach (double a in new[] { a0, a1 })
            {
                double c = Math.Cos(a), s = Math.Sin(a);
                // Facing away from the turned part.
                var n = a == a0 ? new V3(s, 0, -c) : new V3(-s, 0, c);
                Fan(piece, [.. profile.Select(p => new V3(cx + p.R * c, p.Y, cz + p.R * s))], n, col, shade, solid);
            }
    }

    // A convex polygon as a fan round its middle, all facing n.
    void Fan(Piece piece, V3[] ring, V3 n, (double, double, double, double) col, double shade, bool solid)
    {
        V3 mid = Scale(ring.Aggregate(new V3(0, 0, 0), Add), 1.0 / ring.Length);
        int m = piece.Add(mid, n, col, (shade, 0.0));
        int first = piece.Pos.Count;
        foreach (V3 p in ring)
            piece.Add(p, n, col, (shade, 0.0));
        for (int i = 0; i < ring.Length; i++)
            Triangle(piece, m, first + i, first + (i + 1) % ring.Length, solid);
    }

    /// <summary>A straight bar: a convex section (u across, v up, counter-clockwise) swept from a
    /// to b, flat faces, both ends closed. up tilts the section (default: world up).</summary>
    public void Bar(string material, V3 a, V3 b, (double U, double V)[] section, bool solid, V3? up = null,
                    (double, double, double, double)? colour = null, double shade = 1.0)
    {
        Piece piece = PieceOf(material);
        var col = colour ?? Plain;
        V3 along = Unit(V3.Sub(b, a));
        V3 u0 = up ?? new V3(0, 1, 0);
        V3 vUp = Unit(V3.Sub(u0, Scale(along, Dot(u0, along))));
        V3 across = V3.Cross(vUp, along);
        V3 At(V3 o, (double U, double V) p) => Add(o, Add(Scale(across, p.U), Scale(vUp, p.V)));
        for (int k = 0; k < section.Length; k++)
        {
            var p = section[k];
            var q = section[(k + 1) % section.Length];
            double du = q.U - p.U, dv = q.V - p.V, l = Math.Sqrt(du * du + dv * dv);
            if (l < 1e-9)
                continue;
            V3 n = Unit(Add(Scale(across, dv / l), Scale(vUp, -du / l)));
            int i = piece.Add(At(a, p), n, col, (shade, 0.0));
            piece.Add(At(a, q), n, col, (shade, 0.0));
            piece.Add(At(b, q), n, col, (shade, 0.0));
            piece.Add(At(b, p), n, col, (shade, 0.0));
            Triangle(piece, i, i + 1, i + 2, solid);
            Triangle(piece, i, i + 2, i + 3, solid);
        }
        Fan(piece, [.. section.Select(p => At(a, p))], Scale(along, -1), col, shade, solid);
        Fan(piece, [.. section.Select(p => At(b, p))], along, col, shade, solid);
    }

    /// <summary>A flat band round the vertical axis between the outlines rIn(angle) and rOut(angle)
    /// (radians, x = r cos, z = r sin), from bottom to top, at the given rising angles; a part of a
    /// circle gets end caps. Walls keep an edge where the outline turns by more than 30°.</summary>
    public void Band(string material, Func<double, double> rIn, Func<double, double> rOut, double bottom, double top,
                     double[] angles, bool solid, (double, double, double, double)? colour = null, double shade = 1.0)
    {
        Piece piece = PieceOf(material);
        var col = colour ?? Plain;
        bool whole = angles[^1] - angles[0] >= 2 * Math.PI - 1e-9;
        int n = angles.Length;
        V3 At(Func<double, double> r, int j, double y) => new(r(angles[j]) * Math.Cos(angles[j]), y, r(angles[j]) * Math.Sin(angles[j]));
        void Quad(V3 a, V3 b, V3 c, V3 d, V3 na, V3 nb, V3 nc, V3 nd)
        {
            int i = piece.Add(a, na, col, (shade, 0.0));
            piece.Add(b, nb, col, (shade, 0.0));
            piece.Add(c, nc, col, (shade, 0.0));
            piece.Add(d, nd, col, (shade, 0.0));
            Triangle(piece, i, i + 1, i + 2, solid);
            Triangle(piece, i, i + 2, i + 3, solid);
        }
        V3 up = new(0, 1, 0), down = new(0, -1, 0);
        for (int j = 0; j + 1 < n; j++)
        {
            Quad(At(rIn, j, top), At(rOut, j, top), At(rOut, j + 1, top), At(rIn, j + 1, top), up, up, up, up);
            Quad(At(rIn, j, bottom), At(rIn, j + 1, bottom), At(rOut, j + 1, bottom), At(rOut, j, bottom), down, down, down, down);
        }
        // The walls: outward on rOut, inward on rIn, normals blended over gentle turns.
        foreach (var (r, sign) in new[] { (rOut, -1.0), (rIn, 1.0) })
        {
            V3 Edge(int j) => Scale(Unit(V3.Cross(V3.Sub(At(r, j + 1, 0), At(r, j, 0)), up)), sign);
            V3 Normal(int j, int at)
            {
                V3 e = Edge(j);
                int k = at == 0 ? j - 1 : j + 1;
                if (whole)
                    k = (k + n - 1) % (n - 1);
                if (k < 0 || k + 1 >= n || Dot(Edge(k), e) < Math.Cos(30 * Math.PI / 180))
                    return e;
                return Unit(Add(Edge(k), e));
            }
            for (int j = 0; j + 1 < n; j++)
            {
                V3 n0 = Normal(j, 0), n1 = Normal(j, 1);
                Quad(At(r, j, bottom), At(r, j + 1, bottom), At(r, j + 1, top), At(r, j, top), n0, n1, n1, n0);
            }
        }
        if (!whole)
            foreach (int j in new[] { 0, n - 1 })
            {
                double c = Math.Cos(angles[j]), s = Math.Sin(angles[j]);
                V3 nc = j == 0 ? new V3(s, 0, -c) : new V3(-s, 0, c);
                Fan(piece, [At(rIn, j, bottom), At(rOut, j, bottom), At(rOut, j, top), At(rIn, j, top)], nc, col, shade, solid);
            }
    }

    /// <summary>An octagon's outline: sides at distance s facing the axes, diagonal sides at t.</summary>
    public static Func<double, double> Octagon(double s, double t)
        => a => Math.Min(Math.Min(s / Math.Max(Math.Abs(Math.Cos(a)), 1e-9), s / Math.Max(Math.Abs(Math.Sin(a)), 1e-9)),
                         t * Math.Sqrt(2) / (Math.Abs(Math.Cos(a)) + Math.Abs(Math.Sin(a))));

    /// <summary>The angular grid (radians) of every ring round the arena's middle.</summary>
    public const double Step = 1.5 * Math.PI / 180;

    /// <summary>The rising angles from a0 to a1: both ends, the grid in between and the octagon (s, t)'s corners.</summary>
    public static double[] Angles(double a0, double a1, double s = 0, double t = 0)
    {
        var set = new SortedSet<double> { a0, a1 };
        for (long k = (long)Math.Ceiling(a0 / Step); k * Step < a1; k++)
            if (k * Step > a0 + 1e-9 && k * Step < a1 - 1e-9)
                set.Add(k * Step);
        if (s > 0)
        {
            double c = Math.Atan2(t * Math.Sqrt(2) - s, s);
            for (int q = -8; q < 12; q++)
                foreach (double a in new[] { q * Math.PI / 2 + c, q * Math.PI / 2 + Math.PI / 2 - c })
                    if (a > a0 + 1e-9 && a < a1 - 1e-9)
                        set.Add(a);
        }
        return [.. set];
    }

    /// <summary>A w x h rectangle with its corners cut by c, standing on v = 0, for Bar.</summary>
    public static (double U, double V)[] Chamfered(double w, double h, double c)
        => [(-w / 2 + c, 0), (w / 2 - c, 0), (w / 2, c), (w / 2, h - c), (w / 2 - c, h), (-w / 2 + c, h), (-w / 2, h - c), (-w / 2, c)];

    /// <summary>Rock: a lathe of the profile (points about chord apart), each pushed along its normal
    /// by two octaves of noise (amp metres, features of scale metres); the first and last fix points
    /// stay (they meet built parts), the push grows in over the next two. colour gives the terrain
    /// weights and shade per point.</summary>
    public void Rock((double R, double Y)[] profile, int seed, double amp, double scale, bool solid, double cx = 0, double cz = 0,
                     double chord = 1.0, int fix = 1, Func<V3, V3, ((double, double, double, double) Col, double Shade)> colour = null)
    {
        Piece piece = PieceOf("Terrain");
        // Resample the profile so no step is longer than chord.
        var pts = new List<(double R, double Y)> { profile[0] };
        var fixedRows = new List<bool> { true };
        for (int k = 0; k + 1 < profile.Length; k++)
        {
            double dr = profile[k + 1].R - profile[k].R, dy = profile[k + 1].Y - profile[k].Y;
            int n = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dr * dr + dy * dy) / chord));
            for (int i = 1; i <= n; i++)
            {
                pts.Add((profile[k].R + dr * i / n, profile[k].Y + dy * i / n));
                fixedRows.Add(i == n && (k + 1 < fix || k + 1 > profile.Length - 1 - fix));
            }
        }
        int rows = pts.Count, sides = Sides(2 * Math.PI, pts.Max(p => p.R), chord, 16);
        // How far from a fixed row: the push grows in over two rows.
        var weight = new double[rows];
        for (int k = 0; k < rows; k++)
        {
            int d = int.MaxValue;
            for (int m = 0; m < rows; m++)
                if (fixedRows[m])
                    d = Math.Min(d, Math.Abs(m - k));
            weight[k] = Math.Min(1.0, d / 3.0);
        }
        var pos = new V3[rows * sides];
        var lathe = new V3[rows * sides];   // the unpushed lathe's outward normal, to orient the faces
        for (int k = 0; k < rows; k++)
        {
            // The profile's own normal at the row, for the push.
            var (pr, py) = pts[Math.Max(0, k - 1)];
            var (qr, qy) = pts[Math.Min(rows - 1, k + 1)];
            double dr = qr - pr, dy = qy - py, l = Math.Sqrt(dr * dr + dy * dy);
            double nr = l < 1e-9 ? 1 : dy / l, ny = l < 1e-9 ? 0 : -dr / l;
            for (int j = 0; j < sides; j++)
            {
                double a = 2 * Math.PI * j / sides, c = Math.Cos(a), s = Math.Sin(a);
                var p = new V3(cx + pts[k].R * c, pts[k].Y, cz + pts[k].R * s);
                double noise = M.Noise3(seed, p.X / scale, p.Y / scale, p.Z / scale)
                               + 0.45 * M.Noise3(seed + 1, p.X * 2.3 / scale, p.Y * 2.3 / scale, p.Z * 2.3 / scale);
                double push = amp * noise * weight[k] * Math.Min(1.0, pts[k].R / 0.5);
                lathe[k * sides + j] = new V3(nr * c, ny, nr * s);
                pos[k * sides + j] = Add(p, Scale(lathe[k * sides + j], push));
            }
        }
        // Normals from the faces round each point (area weighted), turned outward as the lathe's.
        var nrm = new V3[pos.Length];
        var tris = new List<(int, int, int)>();
        for (int k = 0; k + 1 < rows; k++)
            for (int j = 0; j < sides; j++)
            {
                int a = k * sides + j, b = (k + 1) * sides + j, d = k * sides + (j + 1) % sides, e = (k + 1) * sides + (j + 1) % sides;
                tris.Add((a, b, e));
                tris.Add((a, e, d));
            }
        foreach (var (a, b, c) in tris)
        {
            V3 n = V3.Cross(V3.Sub(pos[b], pos[a]), V3.Sub(pos[c], pos[a]));
            nrm[a] = Add(nrm[a], n);
            nrm[b] = Add(nrm[b], n);
            nrm[c] = Add(nrm[c], n);
        }
        double flip = Enumerable.Range(0, pos.Length).Sum(i => Dot(nrm[i], lathe[i])) < 0 ? -1 : 1;
        int first = piece.Pos.Count;
        for (int i = 0; i < pos.Length; i++)
        {
            V3 n = Unit(Scale(nrm[i], flip));
            var (col, shade) = colour?.Invoke(pos[i], n) ?? (Plain, 1.0);
            piece.Add(pos[i], n, col, (shade, 0.0));
        }
        foreach (var (a, b, c) in tris)
            Triangle(piece, first + a, first + b, first + c, solid);
    }

    /// <summary>Copies a MountainTerrain mesh (trees, leaf clumps) in, moved by offset: bark into
    /// Bark (solid but for the thin branches), single leaves into Leaves, cards into LeafCards.</summary>
    public void Take(Mesh mesh, V3 offset)
    {
        var moved = new Dictionary<int, int>();
        for (int t = 0; t < mesh.Idx.Count / 3; t++)
        {
            int kind = mesh.Keys[mesh.Idx[3 * t]].Kind;
            Piece piece = PieceOf(kind == M.Trunk ? "Bark" : kind == M.Leaf ? "Leaves" : "LeafCards");
            var tri = new int[3];
            for (int k = 0; k < 3; k++)
            {
                int v = mesh.Idx[3 * t + k];
                if (!moved.TryGetValue(v, out int w))
                    moved[v] = w = piece.Add(Add(mesh.Pos[v], offset), mesh.Nrm[v], mesh.Col[v], mesh.Uv[v]);
                tri[k] = w;
            }
            // Kept as wound: leaf quads come both ways round on purpose.
            piece.Idx.AddRange(tri);
            if (kind == M.Trunk && !mesh.Unsolid.Contains(t))
                foreach (int i in tri)
                {
                    SolidIdx.Add(SolidPos.Count / 3);
                    SolidPos.AddRange([(float)piece.Pos[i].X, (float)piece.Pos[i].Y, (float)piece.Pos[i].Z]);
                }
        }
    }
}
