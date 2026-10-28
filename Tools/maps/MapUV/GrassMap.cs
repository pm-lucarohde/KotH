using System.IO.Compression;

/// <summary>
/// The grass map of Mount Destiny for the client's GrassField: the ground seen from above in
/// cells of 25 cm over ±128 m, per cell the topmost terrain face's height, its share
/// of grass that faces up (none round the trunks), its shade (TEXCOORD_0.x) and its
/// lightmap UV, so the blades take the light of the ground they stand on.
/// </summary>
static class GrassMap
{
    const int Size = 1024;
    const float Half = 128f;
    const float MaxHeight = 8f;
    // Blades stand only on ground flatter than this (normal's y), fully from the second.
    const float FlatFrom = 0.8f, FlatFull = 0.95f;
    // Bark below this height is a trunk's foot in the meadow (GRASS_Y 2); no grass this
    // close to it, nor inside.
    const float TrunkFootY = 2.6f, TrunkMargin = 0.08f;

    public static void Write(string path, List<Primitive> primitives)
    {
        float cell = 2 * Half / Size;
        var top = new float[Size * Size];
        Array.Fill(top, float.NegativeInfinity);
        var amount = new float[Size * Size];
        var shade = new float[Size * Size];
        var uv = new float[Size * Size * 2];

        foreach (Primitive prim in primitives.Where(p => p.Material == "Terrain"))
        {
            float[] pos = prim.Attributes["POSITION"], nrm = prim.Attributes["NORMAL"];
            float[] col = prim.Attributes["COLOR_0"], tc0 = prim.Attributes["TEXCOORD_0"], tc1 = prim.Attributes["TEXCOORD_1"];
            for (int t = 0; t < prim.Indices.Length; t += 3)
            {
                int a = prim.Indices[t], b = prim.Indices[t + 1], c = prim.Indices[t + 2];
                if (nrm[3 * a + 1] + nrm[3 * b + 1] + nrm[3 * c + 1] < 3 * FlatFrom)
                    continue;
                float ax = pos[3 * a], az = pos[3 * a + 2], bx = pos[3 * b], bz = pos[3 * b + 2], cx = pos[3 * c], cz = pos[3 * c + 2];
                float area = (bx - ax) * (cz - az) - (cx - ax) * (bz - az);
                if (Math.Abs(area) < 1e-9f)
                    continue;
                int x0 = Math.Max(0, (int)Math.Floor((Math.Min(ax, Math.Min(bx, cx)) + Half) / cell - 0.5f));
                int x1 = Math.Min(Size - 1, (int)Math.Ceiling((Math.Max(ax, Math.Max(bx, cx)) + Half) / cell - 0.5f));
                int z0 = Math.Max(0, (int)Math.Floor((Math.Min(az, Math.Min(bz, cz)) + Half) / cell - 0.5f));
                int z1 = Math.Min(Size - 1, (int)Math.Ceiling((Math.Max(az, Math.Max(bz, cz)) + Half) / cell - 0.5f));
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = (x + 0.5f) * cell - Half, pz = (z + 0.5f) * cell - Half;
                        float wb = ((px - ax) * (cz - az) - (cx - ax) * (pz - az)) / area;
                        float wc = ((bx - ax) * (pz - az) - (px - ax) * (bz - az)) / area;
                        float wa = 1 - wb - wc;
                        if (wa < -1e-5f || wb < -1e-5f || wc < -1e-5f)
                            continue;
                        float y = wa * pos[3 * a + 1] + wb * pos[3 * b + 1] + wc * pos[3 * c + 1];
                        int i = z * Size + x;
                        if (y <= top[i])
                            continue;
                        top[i] = y;
                        float up = wa * nrm[3 * a + 1] + wb * nrm[3 * b + 1] + wc * nrm[3 * c + 1];
                        float grass = wa * col[4 * a] + wb * col[4 * b] + wc * col[4 * c];
                        amount[i] = Math.Clamp(grass, 0, 1) * SmoothStep(FlatFrom, FlatFull, up);
                        shade[i] = wa * tc0[2 * a] + wb * tc0[2 * b] + wc * tc0[2 * c];
                        uv[2 * i] = wa * tc1[2 * a] + wb * tc1[2 * b] + wc * tc1[2 * c];
                        uv[2 * i + 1] = wa * tc1[2 * a + 1] + wb * tc1[2 * b + 1] + wc * tc1[2 * c + 1];
                    }
            }
        }

        int trunks = ClearTrunks(primitives, amount, cell);
        int grassy = amount.Count(v => v > 0);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var output = new BinaryWriter(new ZLibStream(File.Create(path), CompressionLevel.SmallestSize)))
        {
            output.Write(0x5352474Bu);   // "KGRS"
            output.Write(1u);
            output.Write(Size);
            output.Write(Half);
            output.Write(MaxHeight);
            for (int i = 0; i < Size * Size; i++)
            {
                bool ground = top[i] > float.NegativeInfinity;
                output.Write(Unorm16(uv[2 * i]));
                output.Write(Unorm16(uv[2 * i + 1]));
                output.Write(Unorm16(ground ? top[i] / MaxHeight : 0));
                output.Write((ushort)(Unorm8(amount[i]) << 8 | Unorm8(ground ? shade[i] : 1)));
            }
        }
        Console.WriteLine($"{path}: {new FileInfo(path).Length / 1e6:F1} MB, grass on {grassy * cell * cell:F0} m², {trunks} trunks left out");
    }

    /// <summary>
    /// The trunks' feet in the meadow: the bark's corners near the ground, gathered
    /// by trunk (2 m cells), each its middle and the radius of its widest corner.
    /// </summary>
    static List<(double X, double Z, double R)> Feet(List<Primitive> primitives)
    {
        var feet = new List<(double X, double Z, List<(float X, float Z)> Corners)>();
        foreach (Primitive prim in primitives.Where(p => p.Material == "Bark"))
        {
            float[] pos = prim.Attributes["POSITION"];
            for (int v = 0; v < pos.Length / 3; v++)
            {
                if (pos[3 * v + 1] > TrunkFootY)
                    continue;
                float x = pos[3 * v], z = pos[3 * v + 2];
                int k = feet.FindIndex(f => Math.Abs(f.X - x) < 2 && Math.Abs(f.Z - z) < 2);
                if (k < 0)
                    feet.Add((x, z, [(x, z)]));
                else
                    feet[k].Corners.Add((x, z));
            }
        }
        return [.. feet.Select(f =>
        {
            double mx = f.Corners.Average(c => c.X), mz = f.Corners.Average(c => c.Z);
            return (mx, mz, f.Corners.Max(c => Math.Sqrt((c.X - mx) * (c.X - mx) + (c.Z - mz) * (c.Z - mz))));
        })];
    }

    // The contact map: cells of 12.5 cm over the grass map's square; the ground this much
    // darker right at a trunk, fading out with ContactFall metres away from it (Luca).
    const int ContactSize = 2048;
    const double ContactDepth = 0.6, ContactFall = 0.7, ContactReach = 2.2;

    /// <summary>
    /// Writes the contact map (MountDestinyContact.png, grey): the ground's darkening round the
    /// trunks' feet, 255 untouched. TerrainTriplanar darkens the ground with it; seen from
    /// above like the grass map, row 0 at z = -Half.
    /// </summary>
    public static void WriteContact(string path, List<Primitive> primitives)
    {
        double cell = 2.0 * Half / ContactSize;
        var light = new double[ContactSize * ContactSize];
        Array.Fill(light, 1.0);
        var feet = Feet(primitives);
        foreach (var (mx, mz, foot) in feet)
        {
            double r = foot + ContactReach;
            int x0 = (int)Math.Floor((mx - r + Half) / cell), x1 = (int)Math.Ceiling((mx + r + Half) / cell);
            int z0 = (int)Math.Floor((mz - r + Half) / cell), z1 = (int)Math.Ceiling((mz + r + Half) / cell);
            for (int z = Math.Max(0, z0); z <= Math.Min(ContactSize - 1, z1); z++)
                for (int x = Math.Max(0, x0); x <= Math.Min(ContactSize - 1, x1); x++)
                {
                    double px = (x + 0.5) * cell - Half - mx, pz = (z + 0.5) * cell - Half - mz;
                    double away = Math.Max(0, Math.Sqrt(px * px + pz * pz) - foot);
                    if (away < ContactReach)
                        light[z * ContactSize + x] *= 1 - ContactDepth * Math.Exp(-away / ContactFall) * (1 - away / ContactReach);
                }
        }
        var png = new KotH.Tools.Png(ContactSize, ContactSize, 1);
        for (int i = 0; i < light.Length; i++)
            png.Pixels[i] = (byte)Math.Round(Math.Clamp(light[i], 0, 1) * 255);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        png.Write(path);
        Console.WriteLine($"{path}: {feet.Count} trunk feet darkened");
    }

    /// <summary>Clears the grass in and round every trunk: a disc as wide as its foot.</summary>
    static int ClearTrunks(List<Primitive> primitives, float[] amount, float cell)
    {
        var feet = Feet(primitives);
        foreach (var (mx, mz, foot) in feet)
        {
            double r = foot + TrunkMargin;
            int x0 = (int)Math.Floor((mx - r + Half) / cell), x1 = (int)Math.Ceiling((mx + r + Half) / cell);
            int z0 = (int)Math.Floor((mz - r + Half) / cell), z1 = (int)Math.Ceiling((mz + r + Half) / cell);
            for (int z = Math.Max(0, z0); z <= Math.Min(Size - 1, z1); z++)
                for (int x = Math.Max(0, x0); x <= Math.Min(Size - 1, x1); x++)
                {
                    double px = (x + 0.5) * cell - Half - mx, pz = (z + 0.5) * cell - Half - mz;
                    if (px * px + pz * pz <= r * r)
                        amount[z * Size + x] = 0;
                }
        }
        return feet.Count;
    }

    static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    static ushort Unorm16(float v) => (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535);
    static int Unorm8(float v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
}
