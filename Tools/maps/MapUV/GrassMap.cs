using System.IO.Compression;

/// <summary>The grass map of Mount Destiny for the client's GrassField: the ground seen from
/// above in cells of 25 cm over ±128 m, per cell the topmost terrain face's height, its
/// share of grass that faces up (none round the trunks), its shade (TEXCOORD_0.x) and its
/// lightmap UV, so the blades take the light of the ground they stand on.</summary>
static class GrassMap
{
    const int Size = 1024;
    const float Half = 128f;
    // Blades grow up to this height (Atrium: its garden ring at 6.4 m); none above it.
    public static float MaxHeight = 8f;
    // Blades stand only on ground flatter than this (normal's y), fully from the second.
    const float FlatFrom = 0.8f, FlatFull = 0.95f;
    // Bark below this height is a trunk's foot in the meadow (GRASS_Y 2); no grass this
    // close to it, nor inside.
    public static float TrunkFootY = 2.6f;
    // Bark below this is no foot in the grass (Atrium: the logs and beams under its garden).
    public static float TrunkFootLow = float.NegativeInfinity;
    const float TrunkMargin = 0.08f;
    // GrassBlade lerps the height between four cells: next to a cell without grass at another
    // height (Atrium's ledges) a blade would hang in the air, dark from that cell's lightmap uv.
    public static bool Borders;
    // Parts lying on the ground that no blade grows through (Pyramid: its lamps, gold plates, pond), below 0.5 m.
    public static string[] Covers = [];

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
                        amount[i] = y > MaxHeight ? 0 : Math.Clamp(grass, 0, 1) * SmoothStep(FlatFrom, FlatFull, up);
                        shade[i] = wa * tc0[2 * a] + wb * tc0[2 * b] + wc * tc0[2 * c];
                        uv[2 * i] = wa * tc1[2 * a] + wb * tc1[2 * b] + wc * tc1[2 * c];
                        uv[2 * i + 1] = wa * tc1[2 * a + 1] + wb * tc1[2 * b + 1] + wc * tc1[2 * c + 1];
                    }
            }
        }

        int trunks = ClearTrunks(primitives, amount, cell);
        ClearCovers(primitives, amount, cell);
        if (Borders)
            Border(top, amount, shade, uv);
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

    // Every cell under a cover's low triangles (their footprint and 10 cm round it) left bare.
    static void ClearCovers(List<Primitive> primitives, float[] amount, float cell)
    {
        foreach (Primitive prim in primitives.Where(p => Covers.Contains(p.Material)))
        {
            float[] pos = prim.Attributes["POSITION"];
            for (int t = 0; t < prim.Indices.Length; t += 3)
            {
                int[] v = [prim.Indices[t], prim.Indices[t + 1], prim.Indices[t + 2]];
                if (v.Any(i => pos[3 * i + 1] > 0.5f))
                    continue;
                float minX = v.Min(i => pos[3 * i]) - 0.1f, maxX = v.Max(i => pos[3 * i]) + 0.1f;
                float minZ = v.Min(i => pos[3 * i + 2]) - 0.1f, maxZ = v.Max(i => pos[3 * i + 2]) + 0.1f;
                for (int z = Math.Max(0, (int)((minZ + Half) / cell)); z <= Math.Min(Size - 1, (int)((maxZ + Half) / cell)); z++)
                    for (int x = Math.Max(0, (int)((minX + Half) / cell)); x <= Math.Min(Size - 1, (int)((maxX + Half) / cell)); x++)
                        amount[z * Size + x] = 0;
            }
        }
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
                if (pos[3 * v + 1] > TrunkFootY || pos[3 * v + 1] < TrunkFootLow)
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

    /// <summary>Writes the contact map (MountDestinyContact.png, grey): the ground's darkening
    /// round the trunks' feet, 255 untouched. TerrainTriplanar darkens the ground with it; seen
    /// from above like the grass map, row 0 at z = -Half.</summary>
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

    /// <summary>Gives every cell without grass next to grass that grass's height, shade and
    /// lightmap uv (of the neighbour with the most grass), so blades at the border stand on it.</summary>
    static void Border(float[] top, float[] amount, float[] shade, float[] uv)
    {
        var grassy = (float[])amount.Clone();
        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                int i = z * Size + x, from = -1;
                if (grassy[i] > 0)
                    continue;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, nz = z + dz;
                        if (nx < 0 || nz < 0 || nx >= Size || nz >= Size)
                            continue;
                        int n = nz * Size + nx;
                        if (grassy[n] > 0 && (from < 0 || grassy[n] > grassy[from]))
                            from = n;
                    }
                if (from < 0)
                    continue;
                top[i] = top[from];
                shade[i] = shade[from];
                uv[2 * i] = uv[2 * from];
                uv[2 * i + 1] = uv[2 * from + 1];
            }
    }

    static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    // The surface map: a coarser top view over the same square, per cell the topmost terrain face
    // that faces up at all, the surface its COLOR_0 weighs most (grass, sand, snow, snow cap,
    // the rest rock) and its height; the client picks the dash's shards by it. Faces of the
    // material Water names (Pyramid's pond; null on the other maps) are water, kind 5.
    const int SurfaceSize = 512;
    const float FacesUp = 0.3f;
    public static string? Water;

    public static void WriteSurface(string path, List<Primitive> primitives)
    {
        float cell = 2 * Half / SurfaceSize;
        var top = new float[SurfaceSize * SurfaceSize];
        Array.Fill(top, float.NegativeInfinity);
        var kind = new byte[SurfaceSize * SurfaceSize];

        foreach (Primitive prim in primitives.Where(p => p.Material == "Terrain" || Water != null && p.Material == Water))
        {
            bool wet = prim.Material != "Terrain";
            float[] pos = prim.Attributes["POSITION"], nrm = prim.Attributes["NORMAL"], col = wet ? null : prim.Attributes["COLOR_0"];
            for (int t = 0; t < prim.Indices.Length; t += 3)
            {
                int a = prim.Indices[t], b = prim.Indices[t + 1], c = prim.Indices[t + 2];
                if (nrm[3 * a + 1] + nrm[3 * b + 1] + nrm[3 * c + 1] < 3 * FacesUp)
                    continue;
                float ax = pos[3 * a], az = pos[3 * a + 2], bx = pos[3 * b], bz = pos[3 * b + 2], cx = pos[3 * c], cz = pos[3 * c + 2];
                float area = (bx - ax) * (cz - az) - (cx - ax) * (bz - az);
                if (Math.Abs(area) < 1e-9f)
                    continue;
                int x0 = Math.Max(0, (int)Math.Floor((Math.Min(ax, Math.Min(bx, cx)) + Half) / cell - 0.5f));
                int x1 = Math.Min(SurfaceSize - 1, (int)Math.Ceiling((Math.Max(ax, Math.Max(bx, cx)) + Half) / cell - 0.5f));
                int z0 = Math.Max(0, (int)Math.Floor((Math.Min(az, Math.Min(bz, cz)) + Half) / cell - 0.5f));
                int z1 = Math.Min(SurfaceSize - 1, (int)Math.Ceiling((Math.Max(az, Math.Max(bz, cz)) + Half) / cell - 0.5f));
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
                        int i = z * SurfaceSize + x;
                        if (y <= top[i])
                            continue;
                        top[i] = y;
                        if (wet)
                        {
                            kind[i] = 5;
                            continue;
                        }
                        // Grass, sand, snow and the snow cap; rock is what they leave.
                        var w = new float[5];
                        for (int k = 0; k < 4; k++)
                            w[k] = wa * col[4 * a + k] + wb * col[4 * b + k] + wc * col[4 * c + k];
                        w[2] += w[3];
                        w[3] = 0;
                        w[4] = 1 - w[0] - w[1] - w[2];
                        int best = 0;
                        for (int k = 1; k < 5; k++)
                            if (w[k] > w[best])
                                best = k;
                        kind[i] = (byte)(best == 4 ? 4 : best + 1);
                    }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var output = new BinaryWriter(new ZLibStream(File.Create(path), CompressionLevel.SmallestSize)))
        {
            output.Write(0x4652534Bu);   // "KSRF"
            output.Write(1u);
            output.Write(SurfaceSize);
            output.Write(Half);
            for (int i = 0; i < SurfaceSize * SurfaceSize; i++)
            {
                bool ground = top[i] > float.NegativeInfinity;
                output.Write(ground ? kind[i] : (byte)0);
                output.Write((short)(ground ? Math.Clamp(Math.Round(top[i] * 100), short.MinValue, short.MaxValue) : short.MinValue));
            }
        }
        var counts = new int[6];
        foreach (byte k in kind)
            counts[k]++;
        Console.WriteLine($"{path}: {new FileInfo(path).Length / 1e3:F0} kB, cells grass {counts[1]}, sand {counts[2]}, snow {counts[3]}, rock {counts[4]}" +
                          (Water != null ? $", water {counts[5]}" : ""));
    }

    static ushort Unorm16(float v) => (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535);
    static int Unorm8(float v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
}
