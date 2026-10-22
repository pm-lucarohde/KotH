using System;
using System.Collections.Generic;
using KotH.Shared.Match;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using Stride.Core.Mathematics;

namespace KotH.Shared.Bots
{
    /// <summary>
    /// The bots' map of the arena: columns of CellSize, each with the floors
    /// a body can stand on (flat enough, room above), and the moves between
    /// them: walking to a neighbour, dropping, jumping or double jumping up,
    /// jumping a gap. Cut from the arena's triangles once per world. For the
    /// hill it keeps the seconds to the zone from every floor, once plain and
    /// once per route variant. Everything is a pure function of the arena.
    /// </summary>
    public sealed class ClimbGrid
    {
        /// <summary>
        /// What one bot has learnt on its way (real-time adaptive A*): moves
        /// that failed cost it more, and floors its searches saw or it got
        /// stuck on count as further from the zone. Its own, so the bots
        /// learn apart.
        /// </summary>
        public sealed class Memory
        {
            internal readonly Dictionary<int, float> Extra = new Dictionary<int, float>();
            internal readonly Dictionary<long, float> Penalty = new Dictionary<long, float>();
            public int Lessons => Extra.Count + Penalty.Count;
        }

        /// <summary>A triangle of the arena, wound so its normal points out of the solid.</summary>
        public struct Triangle
        {
            public Vector3 A, B, C;
        }

        const int MaxFloors = 4;
        const float WalkDown = 0.6f;       // lower than this is a drop, not a step
        const float JumpReach = 2.5f;      // metres a jump up carries across
        const float MinRun = 0.75f;        // a jump from closer to the step than this pays for the lack of run-up
        const float Run = 6f;              // m/s for costs, about what the motor makes uphill
        const float NoiseScale = 15f;      // metres per wave of a variant's cost noise
        const float NoiseAmp = 0.3f;       // the noise adds up to this share to a variant's cost
        const float Spread = 40f;          // degrees round the zone the variants' sides span, each way
        const float SideWidth = 20f;       // degrees off its side at which a variant pays double
        const float SideAmp = 1f;

        static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] DZ = { 0, 0, 1, -1, 1, -1, 1, -1 };
        static readonly float[] UX = new float[16], UZ = new float[16];

        static ClimbGrid()
        {
            for (int d = 0; d < 16; d++)
            {
                double a = d * Math.PI / 8.0;
                UX[d] = (float)Math.Cos(a);
                UZ[d] = (float)Math.Sin(a);
            }
        }

        readonly float x0, z0, cell;
        readonly int nx, nz;
        readonly float stepUp, jumpUp, doubleUp, gapMax, standHeight;

        // Surfaces per column, top down; facing 1 floor-flat up, 0 steep up, -1 down.
        int[] surfStart;
        float[] surfY;
        sbyte[] surfFacing;

        // Floors per column, top down.
        int[] floorStart;
        float[] floorY;
        int[] floorCol;
        float[] headroom;
        byte[] edges;                       // bit per side (+x, -x, +z, -z): a fall of over a metre

        // Moves other than walking, by floor, and the same reversed.
        int[] linkStart, linkFrom, linkTo, revStart, revLink;
        LinkKind[] linkKind;
        float[] linkCost;

        float[][] time;                     // seconds to the zone; [0] plain, then the variants
        uint[] noiseSeed;
        float[] side;                       // each variant's favourite bearing round the zone, radians
        Vector3 hub;                        // the zone's middle

        // Search state, shared by all bots of a world (they think one after another).
        float[] gScore;
        int[] parent, stamp, shut;
        readonly List<int> closed = new List<int>();
        int generation;
        readonly Heap heap = new Heap();
        readonly List<int> chain = new List<int>();
        readonly List<LinkKind> chainKind = new List<LinkKind>();

        public int FloorCount => floorY.Length;
        public int LinkCount => linkTo.Length;

        /// <summary>A move other than walking, by index: where it starts and lands, for probes.</summary>
        public LinkKind LinkAt(int i, out Vector3 from, out Vector3 to)
        {
            from = FeetOn(linkFrom[i]);
            to = FeetOn(linkTo[i]);
            return linkKind[i];
        }
        /// <summary>The highest surface under a height at a point, MinValue for none; for probes.</summary>
        public float SurfaceAt(float x, float z, float under)
        {
            int col = ColumnAt(x, z);
            return col < 0 ? float.MinValue : Highest(col, under);
        }

        public int Variants => time.Length - 1;
        public int Drops { get; private set; }
        public int Jumps { get; private set; }
        public int DoubleJumps { get; private set; }
        public int Gaps { get; private set; }

        ClimbGrid(float x0, float z0, float cell, int nx, int nz, PlayerMotor motor, BotSettings s)
        {
            this.x0 = x0;
            this.z0 = z0;
            this.cell = cell;
            this.nx = nx;
            this.nz = nz;
            stepUp = motor.StandStepOffset;
            standHeight = motor.StandHeight;
            jumpUp = s.JumpUp;
            doubleUp = s.DoubleJumpUp;
            gapMax = s.GapMax;
        }

        /// <summary>
        /// Cuts the grid from the arena's triangles and, with a zone, lays the
        /// plain field and the route variants, their noise seeded from seed.
        /// </summary>
        public static ClimbGrid Build(List<Triangle> tris, PlayerMotor motor, BotSettings s, HillMatch zone, int seed,
                                      Vector3 start)
        {
            float cell = UMath.Max(0.25f, s.CellSize);
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (Triangle t in tris)
                foreach (Vector3 p in new[] { t.A, t.B, t.C })
                {
                    minX = UMath.Min(minX, p.X);
                    minZ = UMath.Min(minZ, p.Z);
                    maxX = UMath.Max(maxX, p.X);
                    maxZ = UMath.Max(maxZ, p.Z);
                }
            if (tris.Count == 0)
                minX = minZ = maxX = maxZ = 0f;
            int nx = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / cell));
            int nz = Math.Max(1, (int)MathF.Ceiling((maxZ - minZ) / cell));
            var grid = new ClimbGrid(minX, minZ, cell, nx, nz, motor, s);
            grid.Rasterize(tris, MathF.Cos(motor.SlopeLimit * (MathF.PI / 180f)));
            grid.FindFloors();
            grid.FindLinks();
            grid.Fields(zone, Math.Max(0, s.RouteVariants), seed, start);
            return grid;
        }

        // ---------- the columns ----------

        int Columns => nx * nz;

        float CentreX(int col) => x0 + (col % nx + 0.5f) * cell;
        float CentreZ(int col) => z0 + (col / nx + 0.5f) * cell;

        int ColumnAt(float x, float z)
        {
            int ix = (int)MathF.Floor((x - x0) / cell), iz = (int)MathF.Floor((z - z0) / cell);
            return ix < 0 || iz < 0 || ix >= nx || iz >= nz ? -1 : iz * nx + ix;
        }

        struct Hit : IComparable<Hit>
        {
            public int Col;
            public float Y, Ny;

            public int CompareTo(Hit o)
                => Col != o.Col ? Col.CompareTo(o.Col) : Y != o.Y ? o.Y.CompareTo(Y) : o.Ny.CompareTo(Ny);
        }

        // Every triangle drawn into the columns whose centre it covers, seen from above.
        void Rasterize(List<Triangle> tris, float flat)
        {
            var hits = new List<Hit>();
            foreach (Triangle t in tris)
            {
                Vector3 n = UMath.Cross(UMath.Sub(t.B, t.A), UMath.Sub(t.C, t.A));
                float length = UMath.Magnitude(n);
                if (length < 1e-9f || UMath.Abs(n.Y) < 0.02f * length)
                    continue;
                float ny = n.Y / length;
                float d = (t.B.Z - t.C.Z) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Z - t.C.Z);
                int ix0 = Math.Max(0, (int)MathF.Ceiling((UMath.Min(t.A.X, UMath.Min(t.B.X, t.C.X)) - x0) / cell - 0.5f));
                int ix1 = Math.Min(nx - 1, (int)MathF.Floor((UMath.Max(t.A.X, UMath.Max(t.B.X, t.C.X)) - x0) / cell - 0.5f));
                int iz0 = Math.Max(0, (int)MathF.Ceiling((UMath.Min(t.A.Z, UMath.Min(t.B.Z, t.C.Z)) - z0) / cell - 0.5f));
                int iz1 = Math.Min(nz - 1, (int)MathF.Floor((UMath.Max(t.A.Z, UMath.Max(t.B.Z, t.C.Z)) - z0) / cell - 0.5f));
                for (int iz = iz0; iz <= iz1; iz++)
                    for (int ix = ix0; ix <= ix1; ix++)
                    {
                        float px = x0 + (ix + 0.5f) * cell, pz = z0 + (iz + 0.5f) * cell;
                        float l1 = ((t.B.Z - t.C.Z) * (px - t.C.X) + (t.C.X - t.B.X) * (pz - t.C.Z)) / d;
                        float l2 = ((t.C.Z - t.A.Z) * (px - t.C.X) + (t.A.X - t.C.X) * (pz - t.C.Z)) / d;
                        float l3 = 1f - l1 - l2;
                        if (l1 < -1e-5f || l2 < -1e-5f || l3 < -1e-5f)
                            continue;
                        hits.Add(new Hit { Col = iz * nx + ix, Y = l1 * t.A.Y + l2 * t.B.Y + l3 * t.C.Y, Ny = ny });
                    }
            }
            hits.Sort();

            // Seams give a point twice: one surface, the flatter facing wins.
            surfStart = new int[Columns + 1];
            var ys = new List<float>(hits.Count);
            var facing = new List<sbyte>(hits.Count);
            int col = 0;
            for (int i = 0; i < hits.Count; i++)
            {
                Hit h = hits[i];
                while (col < h.Col)
                    surfStart[++col] = ys.Count;
                sbyte f = h.Ny >= flat ? (sbyte)1 : h.Ny > 0f ? (sbyte)0 : (sbyte)-1;
                int last = ys.Count - 1;
                if (last >= surfStart[col] && ys[last] - h.Y < 0.01f && (facing[last] < 0) == (f < 0))
                {
                    facing[last] = (sbyte)Math.Max(facing[last], f);
                    continue;
                }
                ys.Add(h.Y);
                facing.Add(f);
            }
            while (col < Columns)
                surfStart[++col] = ys.Count;
            surfY = ys.ToArray();
            surfFacing = facing.ToArray();
        }

        // A floor is a flat surface with free room above it: under the sky,
        // or under a face looking down, at least a body's height away.
        void FindFloors()
        {
            floorStart = new int[Columns + 1];
            var ys = new List<float>();
            var cols = new List<int>();
            var room = new List<float>();
            for (int c = 0; c < Columns; c++)
            {
                floorStart[c] = ys.Count;
                int found = 0;
                for (int i = surfStart[c]; i < surfStart[c + 1] && found < MaxFloors; i++)
                {
                    if (surfFacing[i] != 1)
                        continue;
                    bool open = i == surfStart[c] || surfFacing[i - 1] < 0;
                    float above = i == surfStart[c] ? float.MaxValue : surfY[i - 1] - surfY[i];
                    if (!open || above < standHeight - 0.05f)
                        continue;
                    ys.Add(surfY[i]);
                    cols.Add(c);
                    room.Add(above);
                    found++;
                }
            }
            floorStart[Columns] = ys.Count;
            floorY = ys.ToArray();
            floorCol = cols.ToArray();
            headroom = room.ToArray();

            edges = new byte[floorY.Length];
            for (int f = 0; f < floorY.Length; f++)
                for (int d = 0; d < 4; d++)
                {
                    int n = Neighbour(floorCol[f], d);
                    float y = floorY[f];
                    if (n >= 0 && FloorIn(n, y, stepUp, 1f) >= 0)
                        continue;
                    if (n < 0 || Highest(n, y + 0.5f) < y - 1f)
                        edges[f] |= (byte)(1 << d);
                }
        }

        int Neighbour(int col, int d)
        {
            int ix = col % nx + DX[d], iz = col / nx + DZ[d];
            return ix < 0 || iz < 0 || ix >= nx || iz >= nz ? -1 : iz * nx + ix;
        }

        // The floor of a column nearest to y, from down below to up above it; -1 for none.
        int FloorIn(int col, float y, float up, float down)
        {
            int best = -1;
            float gap = float.MaxValue;
            for (int f = floorStart[col]; f < floorStart[col + 1]; f++)
            {
                float d = floorY[f] - y;
                if (d > up || d < -down)
                    continue;
                if (UMath.Abs(d) < gap)
                {
                    gap = UMath.Abs(d);
                    best = f;
                }
            }
            return best;
        }

        // The highest surface of a column under a height; MinValue for none.
        float Highest(int col, float under)
        {
            for (int i = surfStart[col]; i < surfStart[col + 1]; i++)
                if (surfY[i] < under)
                    return surfY[i];
            return float.MinValue;
        }

        // ---------- the moves ----------

        int Walk(int f, int d)
        {
            int col = floorCol[f];
            int n = Neighbour(col, d);
            if (n < 0)
                return -1;
            float y = floorY[f];
            int g = FloorIn(n, y, stepUp, WalkDown);
            if (g < 0 || d < 4)
                return g;
            // Diagonally only past two open sides, no corner cut.
            int a = Neighbour(col, DX[d] > 0 ? 0 : 1), b = Neighbour(col, DZ[d] > 0 ? 2 : 3);
            return a >= 0 && b >= 0 && FloorIn(a, y, stepUp, WalkDown) >= 0 && FloorIn(b, y, stepUp, WalkDown) >= 0 ? g : -1;
        }

        float WalkCost(int f, int g, int d)
        {
            float flat = d < 4 ? cell : cell * 1.41421356f;
            float up = floorY[g] - floorY[f];
            return UMath.Sqrt(flat * flat + up * up) / Run;
        }

        /// <summary>
        /// Looks out from every floor in 16 directions, a quarter metre at a
        /// time. Where the own level ends: the first lower column is a drop,
        /// a floor higher within reach a jump or double jump, a floor at the
        /// own level after a hole a gap jump. Anything in the flight is a wall.
        /// </summary>
        void FindLinks()
        {
            var from = new List<int>();
            var to = new List<int>();
            var kinds = new List<LinkKind>();
            var costs = new List<float>();
            linkStart = new int[floorY.Length + 1];
            float reach = UMath.Max(JumpReach, gapMax);
            int steps = (int)(reach / 0.25f);
            for (int f = 0; f < floorY.Length; f++)
            {
                linkStart[f] = to.Count;
                float y = floorY[f], fx = CentreX(floorCol[f]), fz = CentreZ(floorCol[f]);
                for (int d = 0; d < 16; d++)
                {
                    int last = floorCol[f];
                    bool dropped = false;
                    float obstacle = float.MinValue;
                    for (int k = 1; k <= steps; k++)
                    {
                        float out_ = k * 0.25f;
                        int col = ColumnAt(fx + UX[d] * out_, fz + UZ[d] * out_);
                        if (col < 0)
                            break;
                        if (col == last)
                            continue;
                        last = col;

                        int level = FloorIn(col, y, stepUp, WalkDown);
                        float top = Highest(col, y + doubleUp + standHeight);
                        if (level >= 0)
                        {
                            if (dropped)
                            {
                                if (out_ >= 1f && obstacle <= y + 0.3f)
                                    Add(f, level, LinkKind.Jump, 0.4f + out_ / Run, LinkGap);
                                break;
                            }
                            // Still the own level: a run-up to a step further on.
                            obstacle = UMath.Max(obstacle, top);
                            continue;
                        }

                        int up = Lowest(col, y + stepUp, y + doubleUp);
                        if (up >= 0 && out_ <= JumpReach && obstacle <= floorY[up] + 0.1f)
                        {
                            // Land half a metre past the lip where the top goes on.
                            int beyond = ColumnAt(fx + UX[d] * (out_ + 0.5f), fz + UZ[d] * (out_ + 0.5f));
                            int further = beyond >= 0 && beyond != col ? FloorIn(beyond, floorY[up], 0.3f, 0.3f) : -1;
                            if (further >= 0 && floorY[further] - y <= doubleUp &&
                                Highest(beyond, floorY[further] + standHeight) <= floorY[further] + 0.05f)
                                up = further;
                            float rise = floorY[up] - y;
                            if (headroom[f] >= rise + 1.9f)
                            {
                                bool single = rise <= jumpUp;
                                float cost = (single ? 0.65f : 1f) + UMath.Max(0f, out_ - 1f) / Run + (out_ < MinRun ? 0.4f : 0f);
                                Add(f, up, single ? LinkKind.Jump : LinkKind.DoubleJump, cost, single ? LinkJump : LinkDouble);
                            }
                            break;
                        }

                        if (top > y + stepUp)
                            break;  // a wall too high to get onto
                        if (!dropped)
                        {
                            dropped = true;
                            int low = HighestFloor(col, y - WalkDown);
                            if (low >= 0)
                                Add(f, low, LinkKind.Drop, out_ / Run + UMath.Sqrt(2f * (y - floorY[low]) / 20f) + 0.15f, LinkDrop);
                        }
                        obstacle = UMath.Max(obstacle, top);
                    }
                }
            }
            linkStart[floorY.Length] = to.Count;
            linkFrom = from.ToArray();
            linkTo = to.ToArray();
            linkKind = kinds.ToArray();
            linkCost = costs.ToArray();

            // Reversed, for the fields: by target floor, in order of the forward list.
            revStart = new int[floorY.Length + 1];
            foreach (int g in linkTo)
                revStart[g + 1]++;
            for (int i = 0; i < floorY.Length; i++)
                revStart[i + 1] += revStart[i];
            revLink = new int[linkTo.Length];
            var fill = (int[])revStart.Clone();
            for (int l = 0; l < linkTo.Length; l++)
                revLink[fill[linkTo[l]]++] = l;

            void Add(int a, int b, LinkKind kind, float cost, int counter)
            {
                // Two directions often find the same floor: the cheaper stays.
                for (int i = linkStart[a]; i < to.Count; i++)
                    if (to[i] == b)
                    {
                        if (cost < costs[i])
                        {
                            costs[i] = cost;
                            kinds[i] = kind;
                        }
                        return;
                    }
                from.Add(a);
                to.Add(b);
                kinds.Add(kind);
                costs.Add(cost);
                if (counter == LinkDrop) Drops++;
                else if (counter == LinkJump) Jumps++;
                else if (counter == LinkDouble) DoubleJumps++;
                else Gaps++;
            }
        }

        const int LinkDrop = 0, LinkJump = 1, LinkDouble = 2, LinkGap = 3;

        // The lowest floor of a column between two heights; -1 for none.
        int Lowest(int col, float above, float upTo)
        {
            for (int f = floorStart[col + 1] - 1; f >= floorStart[col]; f--)
                if (floorY[f] > above && floorY[f] <= upTo)
                    return f;
            return -1;
        }

        int HighestFloor(int col, float under)
        {
            for (int f = floorStart[col]; f < floorStart[col + 1]; f++)
                if (floorY[f] < under)
                    return f;
            return -1;
        }

        // ---------- the fields ----------

        // A variant's cost: dearer the further a floor lies round the zone
        // from the variant's side (not near the zone, where all ways meet),
        // and a little noise for lines of its own.
        float Factor(int v, int f)
        {
            if (v == 0)
                return 1f;
            float px = CentreX(floorCol[f]), pz = CentreZ(floorCol[f]);
            float dx = px - hub.X, dz = pz - hub.Z;
            float r = UMath.Sqrt(dx * dx + dz * dz);
            float off = MathF.IEEERemainder((float)Math.Atan2(dz, dx) - side[v], 2f * MathF.PI) * (180f / MathF.PI) / SideWidth;
            float near = UMath.Clamp((r - 10f) / 20f, 0f, 1f);
            float away = SideAmp * UMath.Min(1f, off * off) * near * near * (3f - 2f * near);

            float x = px / NoiseScale, z = pz / NoiseScale;
            int ix = (int)MathF.Floor(x), iz = (int)MathF.Floor(z);
            float u = x - ix, w = z - iz;
            u = u * u * (3f - 2f * u);
            w = w * w * (3f - 2f * w);
            uint s = noiseSeed[v];
            float a = UMath.Lerp(Lattice(s, ix, iz), Lattice(s, ix + 1, iz), u);
            float b = UMath.Lerp(Lattice(s, ix, iz + 1), Lattice(s, ix + 1, iz + 1), u);
            return 1f + away + NoiseAmp * UMath.Lerp(a, b, w);
        }

        static float Lattice(uint seed, int x, int z)
            => (Mix(seed ^ (uint)x * 73856093u ^ (uint)z * 19349663u) >> 8) * (1f / 16777216f);

        static uint Mix(uint h)
        {
            h ^= h >> 16;
            h *= 0x7feb352du;
            h ^= h >> 15;
            h *= 0x846ca68bu;
            h ^= h >> 16;
            return h;
        }

        // Dijkstra backwards from every floor in the zone. The variants'
        // sides fan out from the start's bearing, turned a little by the seed.
        void Fields(HillMatch zone, int variants, int seed, Vector3 start)
        {
            time = new float[variants + 1][];
            noiseSeed = new uint[variants + 1];
            side = new float[variants + 1];
            hub = zone?.Center ?? Vector3.Zero;
            float bearing = (float)Math.Atan2(start.Z - hub.Z, start.X - hub.X);
            for (int v = 0; v <= variants; v++)
            {
                noiseSeed[v] = Mix((uint)seed * 2654435761u + (uint)v * 40503u + 1u);
                float fan = variants > 1 ? (v - 1) / (float)(variants - 1) * 2f - 1f : 0f;
                float jitter = ((Mix(noiseSeed[v]) >> 8) * (1f / 16777216f) - 0.5f) * 0.3f;
                side[v] = bearing + (fan * Spread * (MathF.PI / 180f)) + jitter;
                float[] t = time[v] = new float[floorY.Length];
                Array.Fill(t, float.MaxValue);
                if (zone == null)
                    continue;
                heap.Clear();
                for (int f = 0; f < floorY.Length; f++)
                    if (zone.Contains(Centre(f)))
                    {
                        t[f] = 0f;
                        heap.Push(0f, f);
                    }
                while (heap.Count > 0)
                {
                    heap.Pop(out float at, out int g);
                    if (at > t[g])
                        continue;
                    int col = floorCol[g];
                    for (int d = 0; d < 8; d++)
                    {
                        int back = Neighbour(col, d ^ 1);
                        if (back < 0)
                            continue;
                        for (int p = floorStart[back]; p < floorStart[back + 1]; p++)
                            if (Walk(p, d) == g)
                                Relax(t, p, at + WalkCost(p, g, d) * Factor(v, p));
                    }
                    for (int r = revStart[g]; r < revStart[g + 1]; r++)
                    {
                        int l = revLink[r];
                        Relax(t, linkFrom[l], at + linkCost[l] * Factor(v, linkFrom[l]));
                    }
                }
            }
        }

        void Relax(float[] t, int p, float value)
        {
            if (value < t[p])
            {
                t[p] = value;
                heap.Push(value, p);
            }
        }

        /// <summary>Where a body stands on the floor, its centre half a body above.</summary>
        Vector3 Centre(int f) => new Vector3(CentreX(floorCol[f]), floorY[f] + standHeight * 0.5f, CentreZ(floorCol[f]));

        Vector3 FeetOn(int f) => new Vector3(CentreX(floorCol[f]), floorY[f], CentreZ(floorCol[f]));

        // ---------- queries ----------

        /// <summary>The floor under the feet, else the nearest within 1,5 m aside and 3 m up or down.</summary>
        public int FloorAt(Vector3 feet)
        {
            int col = ColumnAt(feet.X, feet.Z);
            if (col >= 0)
            {
                int f = FloorIn(col, feet.Y, 0.8f, 1.2f);
                if (f >= 0)
                    return f;
            }
            int best = -1;
            float bestD = float.MaxValue;
            int r = (int)MathF.Ceiling(1.5f / cell);
            int cx = (int)MathF.Floor((feet.X - x0) / cell), cz = (int)MathF.Floor((feet.Z - z0) / cell);
            for (int iz = cz - r; iz <= cz + r; iz++)
                for (int ix = cx - r; ix <= cx + r; ix++)
                {
                    if (ix < 0 || iz < 0 || ix >= nx || iz >= nz)
                        continue;
                    int c = iz * nx + ix;
                    for (int f = floorStart[c]; f < floorStart[c + 1]; f++)
                    {
                        float dy = floorY[f] - feet.Y;
                        if (dy > 3f || dy < -3f)
                            continue;
                        float dx = CentreX(c) - feet.X, dz = CentreZ(c) - feet.Z;
                        float dd = dx * dx + dz * dz + dy * dy * 0.25f;
                        if (dd < bestD && dx * dx + dz * dz <= 2.25f)
                        {
                            bestD = dd;
                            best = f;
                        }
                    }
                }
            return best;
        }

        public bool Nearest(Vector3 feet, out Vector3 onGrid)
        {
            int f = FloorAt(feet);
            onGrid = f >= 0 ? FeetOn(f) : feet;
            return f >= 0;
        }

        /// <summary>The seconds to the zone along a variant, MaxValue where it cannot be reached.</summary>
        public float TimeToZone(Vector3 feet, int variant)
        {
            int f = FloorAt(feet);
            return f < 0 ? float.MaxValue : time[Math.Clamp(variant, 0, Variants)][f];
        }

        /// <summary>The plain seconds to the zone from a floor index; MaxValue where it cannot be reached.</summary>
        public float TimeOf(int floor) => floor < 0 ? float.MaxValue : time[0][floor];

        /// <summary>Where a fall of over a metre is nearest, level only; false when none within the radius.</summary>
        public bool NearestEdge(Vector3 feet, float radius, out Vector3 edge, out float distance)
        {
            edge = Vector3.Zero;
            distance = radius;
            bool found = false;
            int r = (int)MathF.Ceiling(radius / cell);
            int cx = (int)MathF.Floor((feet.X - x0) / cell), cz = (int)MathF.Floor((feet.Z - z0) / cell);
            for (int iz = cz - r; iz <= cz + r; iz++)
                for (int ix = cx - r; ix <= cx + r; ix++)
                {
                    if (ix < 0 || iz < 0 || ix >= nx || iz >= nz)
                        continue;
                    int c = iz * nx + ix;
                    int f = FloorIn(c, feet.Y, 0.6f, 0.6f);
                    if (f < 0 || edges[f] == 0)
                        continue;
                    for (int d = 0; d < 4; d++)
                    {
                        if ((edges[f] & (1 << d)) == 0)
                            continue;
                        float ex = CentreX(c) + DX[d] * cell * 0.5f, ez = CentreZ(c) + DZ[d] * cell * 0.5f;
                        float dx = ex - feet.X, dz = ez - feet.Z;
                        float dist = UMath.Sqrt(dx * dx + dz * dz);
                        if (dist < distance)
                        {
                            distance = dist;
                            edge = new Vector3(ex, floorY[f], ez);
                            found = true;
                        }
                    }
                }
            return found;
        }

        // Every move out of a floor: walks first (as directions 0-7), then links.
        int MoveCount(int f) => 8 + linkStart[f + 1] - linkStart[f];

        int Move(int f, int i, out float cost, out LinkKind kind)
        {
            if (i < 8)
            {
                kind = LinkKind.None;
                int g = Walk(f, i);
                cost = g >= 0 ? WalkCost(f, g, i) : 0f;
                return g;
            }
            int l = linkStart[f] + i - 8;
            kind = linkKind[l];
            cost = linkCost[l];
            return linkTo[l];
        }

        static float ExtraOf(Memory m, int f) => m != null && m.Extra.TryGetValue(f, out float e) ? e : 0f;

        static float PenaltyOf(Memory m, int f, int g)
            => m != null && m.Penalty.TryGetValue((long)f << 32 | (uint)g, out float p) ? p : 0f;

        // The next floor down a variant's plain field: the cheapest move plus
        // the time left from there, always strictly closer.
        int Next(int f, int v, out float plain)
        {
            float[] t = time[v];
            int best = -1;
            float bestScore = float.MaxValue;
            plain = 0f;
            float factor = Factor(v, f);
            for (int i = 0, n = MoveCount(f); i < n; i++)
            {
                int g = Move(f, i, out float cost, out _);
                if (g < 0 || t[g] >= t[f])
                    continue;
                float score = cost * factor + t[g];
                if (score < bestScore)
                {
                    bestScore = score;
                    best = g;
                    plain = cost;
                }
            }
            return best;
        }

        /// <summary>A move that went wrong costs this bot that much more from now on.</summary>
        public void Punish(int f, int g, float seconds, Memory m)
        {
            if (f < 0 || g < 0)
                return;
            long key = (long)f << 32 | (uint)g;
            m.Penalty[key] = PenaltyOf(m, f, g) + seconds;
        }

        /// <summary>A floor where the bot got nowhere counts as that much further from the zone.</summary>
        public void Stuck(Vector3 feet, float seconds, Memory m)
        {
            int f = FloorAt(feet);
            if (f >= 0)
                m.Extra[f] = ExtraOf(m, f) + seconds;
        }

        public bool SameFloor(Vector3 feet, int floor) => floor >= 0 && FloorAt(feet) == floor;

        /// <summary>
        /// The seconds a route variant really takes from the feet to the
        /// zone, in plain costs, against the best there is; false when no
        /// way leads up.
        /// </summary>
        public bool RouteTime(Vector3 feet, int variant, out float seconds, out float best)
        {
            seconds = best = float.MaxValue;
            int f = FloorAt(feet);
            if (f < 0 || time[0][f] == float.MaxValue)
                return false;
            best = time[0][f];
            int v = Math.Clamp(variant, 0, Variants);
            float sum = 0f;
            for (int k = 0; k < 100000 && time[v][f] > 0f; k++)
            {
                f = Next(f, v, out float plain);
                if (f < 0)
                    return false;
                sum += plain;
            }
            seconds = sum;
            return true;
        }

        /// <summary>
        /// The way from the feet towards the zone down a variant's field, by
        /// real-time adaptive A* (RTAA*): an A* over at most budget floors
        /// with the field plus the bot's lessons as the estimate and its
        /// penalties on the moves; the way leads to the best floor it got
        /// to, and every floor it looked at learns how far it really is.
        /// </summary>
        public bool PlanToZone(Vector3 feet, int variant, Memory memory, int budget, List<Corner> corners)
        {
            corners.Clear();
            int start = FloorAt(feet);
            int v = Math.Clamp(variant, 0, Variants);
            float[] t = time[v];
            if (start < 0 || t[start] == float.MaxValue)
                return false;
            EnsureSearch();
            generation++;
            float H(int f) => t[f] + ExtraOf(memory, f);

            heap.Clear();
            closed.Clear();
            gScore[start] = 0f;
            parent[start] = -1;
            stamp[start] = generation;
            heap.Push(H(start), start);
            int best = start;
            float bestF = H(start);
            while (heap.Count > 0)
            {
                heap.Pop(out float key, out int f);
                if (shut[f] == generation || key > gScore[f] + H(f) + 1e-4f)
                    continue;
                best = f;
                bestF = key;
                if (t[f] == 0f || closed.Count >= budget)
                    break;
                shut[f] = generation;
                closed.Add(f);
                float factor = Factor(v, f);
                for (int i = 0, n = MoveCount(f); i < n; i++)
                {
                    int g = Move(f, i, out float cost, out _);
                    if (g < 0 || t[g] == float.MaxValue || shut[g] == generation)
                        continue;
                    float score = gScore[f] + cost * factor + PenaltyOf(memory, f, g);
                    if (stamp[g] == generation && score >= gScore[g])
                        continue;
                    stamp[g] = generation;
                    gScore[g] = score;
                    parent[g] = f;
                    heap.Push(score + H(g), g);
                }
            }

            // RTAA*: what the search saw lifts its floors to the best estimate it found.
            if (memory != null)
                foreach (int f in closed)
                {
                    float lifted = bestF - gScore[f];
                    if (lifted > H(f) + 0.01f)
                        memory.Extra[f] = lifted - t[f];
                }

            chain.Clear();
            for (int f = best; f >= 0; f = parent[f])
                chain.Add(f);
            chain.Reverse();
            chainKind.Clear();
            for (int i = 0; i + 1 < chain.Count; i++)
                chainKind.Add(KindBetween(chain[i], chain[i + 1]));
            chainKind.Add(LinkKind.None);
            ToCorners(feet, null, corners);
            return true;
        }

        void EnsureSearch()
        {
            if (gScore != null)
                return;
            gScore = new float[floorY.Length];
            parent = new int[floorY.Length];
            stamp = new int[floorY.Length];
            shut = new int[floorY.Length];
        }

        /// <summary>
        /// A* from the feet to the goal over at most maxNodes floors; a goal
        /// out of reach gives the way to the floor closest to it.
        /// </summary>
        public bool Plan(Vector3 feet, Vector3 goal, int maxNodes, List<Corner> corners)
        {
            corners.Clear();
            int start = FloorAt(feet), end = FloorAt(goal);
            if (start < 0 || end < 0)
                return false;
            EnsureSearch();
            generation++;
            Vector3 target = FeetOn(end);
            // Level distance at the cost speed: no move is cheaper, so A* stays exact.
            float H(int f)
            {
                float dx = CentreX(floorCol[f]) - target.X, dz = CentreZ(floorCol[f]) - target.Z;
                return UMath.Sqrt(dx * dx + dz * dz) / Run;
            }

            heap.Clear();
            gScore[start] = 0f;
            parent[start] = -1;
            stamp[start] = generation;
            heap.Push(H(start), start);
            int closest = start, expanded = 0;
            float closestH = H(start);
            while (heap.Count > 0 && expanded < maxNodes)
            {
                heap.Pop(out float key, out int f);
                if (key - H(f) > gScore[f] + 1e-4f)
                    continue;
                expanded++;
                float h = H(f);
                if (h < closestH)
                {
                    closestH = h;
                    closest = f;
                }
                if (f == end)
                    break;
                for (int i = 0, n = MoveCount(f); i < n; i++)
                {
                    int g = Move(f, i, out float cost, out _);
                    if (g < 0)
                        continue;
                    float score = gScore[f] + cost;
                    if (stamp[g] == generation && score >= gScore[g])
                        continue;
                    stamp[g] = generation;
                    gScore[g] = score;
                    parent[g] = f;
                    heap.Push(score + H(g), g);
                }
            }

            int last = stamp[end] == generation ? end : closest;
            chain.Clear();
            for (int f = last; f >= 0; f = parent[f])
                chain.Add(f);
            chain.Reverse();
            chainKind.Clear();
            for (int i = 0; i + 1 < chain.Count; i++)
                chainKind.Add(KindBetween(chain[i], chain[i + 1]));
            chainKind.Add(LinkKind.None);
            ToCorners(feet, last == end ? goal : null, corners);
            return true;
        }

        LinkKind KindBetween(int f, int g)
        {
            LinkKind best = LinkKind.None;
            float bestCost = float.MaxValue;
            for (int i = 0, n = MoveCount(f); i < n; i++)
                if (Move(f, i, out float cost, out LinkKind k) == g && cost < bestCost)
                {
                    bestCost = cost;
                    best = k;
                }
            return best;
        }

        // The chain as corners: straight walks merged, each link starting at its
        // own corner and landing on one; a goal on the last floor ends there.
        void ToCorners(Vector3 feet, Vector3? goal, List<Corner> corners)
        {
            corners.Add(new Corner { Position = feet, Floor = -1 });
            for (int i = 0; i < chain.Count; i++)
            {
                int f = chain[i];
                Vector3 p = FeetOn(f);
                if (i + 1 == chain.Count && goal.HasValue && ColumnAt(goal.Value.X, goal.Value.Z) == floorCol[f])
                    p = new Vector3(goal.Value.X, floorY[f], goal.Value.Z);

                // The corner before goes when a walk passes straight through it.
                bool through = i >= 1 && i + 1 < chain.Count && chainKind[i - 1] == LinkKind.None &&
                               chainKind[i] == LinkKind.None && (i == 1 || chainKind[i - 2] == LinkKind.None) &&
                               Step(chain[i - 1], f) == Step(f, chain[i + 1]);
                if (through && corners.Count > 1)
                    corners[^1] = new Corner { Position = p, Floor = f };
                else
                    corners.Add(new Corner { Position = p, Link = chainKind[i], Floor = f });
            }
        }

        int Step(int f, int g)
            => (floorCol[g] % nx - floorCol[f] % nx) * 4096 + floorCol[g] / nx - floorCol[f] / nx;

        /// <summary>A binary heap of floors by key, ties by floor index, so all runs pop alike.</summary>
        sealed class Heap
        {
            float[] keys = new float[1024];
            int[] ids = new int[1024];

            public int Count { get; private set; }

            public void Clear() => Count = 0;

            bool Less(int a, int b) => keys[a] < keys[b] || keys[a] == keys[b] && ids[a] < ids[b];

            void Swap(int a, int b)
            {
                (keys[a], keys[b]) = (keys[b], keys[a]);
                (ids[a], ids[b]) = (ids[b], ids[a]);
            }

            public void Push(float key, int id)
            {
                if (Count == keys.Length)
                {
                    Array.Resize(ref keys, Count * 2);
                    Array.Resize(ref ids, Count * 2);
                }
                int i = Count++;
                keys[i] = key;
                ids[i] = id;
                while (i > 0)
                {
                    int up = (i - 1) / 2;
                    if (!Less(i, up))
                        break;
                    Swap(i, up);
                    i = up;
                }
            }

            public void Pop(out float key, out int id)
            {
                key = keys[0];
                id = ids[0];
                Count--;
                keys[0] = keys[Count];
                ids[0] = ids[Count];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < Count && Less(l, m))
                        m = l;
                    if (r < Count && Less(r, m))
                        m = r;
                    if (m == i)
                        break;
                    Swap(i, m);
                    i = m;
                }
            }
        }
    }
}
