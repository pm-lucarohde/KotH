using System;
using System.Collections.Generic;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast;
using DotRecast.Recast.Geom;
using KotH.Shared.Mathematics;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using Stride.BepuPhysics;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Shared.Bots
{
    /// <summary>How a stretch of a bot's path is crossed.</summary>
    public enum LinkKind : byte { None, Drop, Jump, DoubleJump }

    /// <summary>A turn of a path at the feet; Link says how the stretch after it is crossed.</summary>
    public struct Corner
    {
        public Vector3 Position;
        public LinkKind Link;
    }

    /// <summary>
    /// The bots' navmesh, cut once per world from the arena's static
    /// colliders with Recast and searched with Detour. Ledges a body can
    /// drop from, jump onto or double jump onto become off-mesh links.
    /// Every query is a pure function of the arena, so all runs agree.
    /// </summary>
    public sealed class BotNavigation
    {
        // Poly areas; Detour multiplies a stretch's length by its cost.
        const int GroundArea = 1, DropArea = 2, JumpArea = 3, DoubleJumpArea = 4;
        const int WalkFlag = 1;
        const int MaxPath = 256, MaxCorners = 32;

        static readonly RcVec3f Extents = new RcVec3f(1.5f, 3f, 1.5f);

        readonly DtNavMesh mesh;
        readonly DtNavMeshQuery query;
        readonly DtQueryDefaultFilter filter;
        readonly List<(Vector3 From, Vector3 To, LinkKind Kind)> links;
        readonly long[] path = new long[MaxPath];
        readonly DtStraightPath[] straight = new DtStraightPath[MaxCorners];

        BotNavigation(DtNavMesh mesh, List<(Vector3, Vector3, LinkKind)> links, int polys)
        {
            this.mesh = mesh;
            this.links = links;
            PolyCount = polys;
            query = new DtNavMeshQuery(mesh);

            var cost = new float[DtDetour.DT_MAX_AREAS];
            Array.Fill(cost, 1f);
            cost[DropArea] = 1.2f;
            cost[JumpArea] = 2f;
            cost[DoubleJumpArea] = 3f;
            filter = new DtQueryDefaultFilter(0xffff, 0, cost);
        }

        public int PolyCount { get; }
        public int LinkCount => links.Count;
        public IReadOnlyList<(Vector3 From, Vector3 To, LinkKind Kind)> Links => links;

        /// <summary>
        /// Cuts the navmesh for a body shaped like the motor: its radius,
        /// height, step and slope limit. The physics world only checks that
        /// a link's flight is clear, against the arena and nothing else.
        /// </summary>
        public static BotNavigation Build(IEnumerable<Entity> arena, PlayerMotor motor,
                                          WorldQueries world, BotSettings s)
        {
            var verts = new List<float>();
            var tris = new List<int>();
            foreach (Entity e in arena)
                if (e.Get<StaticComponent>()?.Collider is CompoundCollider compound)
                    foreach (ColliderBase c in compound.Colliders)
                        AddShape(e.Transform.Position, e.Transform.Rotation, c, verts, tris);

            var geom = new SimpleInputGeomProvider(verts.ToArray(), tris.ToArray());
            var cfg = new RcConfig(RcPartition.WATERSHED, s.CellSize, s.CellHeight,
                                   motor.SlopeLimit, motor.StandHeight, motor.BodyRadius, motor.StandStepOffset,
                                   8, 20, 12f, 1.3f, 6, 6f, 1f, true, true, true,
                                   new RcAreaModification(GroundArea), true);
            RcBuilderResult built = new RcBuilder().Build(geom, new RcBuilderConfig(cfg, geom.GetMeshBoundsMin(), geom.GetMeshBoundsMax()), false);

            // Twice: the links are found on the bare mesh, then cut in with it.
            DtNavMesh bare = Create(built, cfg, motor, null);
            List<(Vector3, Vector3, LinkKind)> found = FindLinks(bare, world, s);
            DtNavMesh linked = Create(built, cfg, motor, found);
            return new BotNavigation(linked, found, built.Mesh.npolys);
        }

        // A box as 12 triangles, a triangle as itself, wound so the face
        // normal points out (Recast judges the slope from the winding).
        static void AddShape(Vector3 at, Quaternion turn, ColliderBase c, List<float> verts, List<int> tris)
        {
            Vector3 World(Vector3 local)
                => UMath.Add(at, UMath.Rotate(turn, UMath.Add(c.PositionLocal, UMath.Rotate(c.RotationLocal, local))));

            if (c is BoxCollider box)
            {
                Vector3 h = UMath.Mul(box.Size, 0.5f);
                Vector3 centre = World(Vector3.Zero);
                for (int axis = 0; axis < 3; axis++)
                    for (int side = -1; side <= 1; side += 2)
                    {
                        Vector3 n = Axis(axis, side), u = Axis((axis + 1) % 3, 1), v = Axis((axis + 2) % 3, 1);
                        Vector3 mid = new Vector3(n.X * h.X, n.Y * h.Y, n.Z * h.Z);
                        Vector3 du = new Vector3(u.X * h.X, u.Y * h.Y, u.Z * h.Z);
                        Vector3 dv = new Vector3(v.X * h.X, v.Y * h.Y, v.Z * h.Z);
                        Vector3 a = World(UMath.Sub(UMath.Sub(mid, du), dv)), b = World(UMath.Sub(UMath.Add(mid, du), dv));
                        Vector3 d = World(UMath.Add(UMath.Add(mid, du), dv)), e = World(UMath.Add(UMath.Sub(mid, du), dv));
                        Vector3 outward = UMath.Sub(World(mid), centre);
                        AddTriangle(a, b, d, outward, verts, tris);
                        AddTriangle(a, d, e, outward, verts, tris);
                    }
            }
            else if (c is TriangleCollider tri)
                AddTriangle(World(tri.A), World(tri.B), World(tri.C), UMath.Up, verts, tris);
        }

        static Vector3 Axis(int axis, int sign)
            => axis == 0 ? new Vector3(sign, 0f, 0f) : axis == 1 ? new Vector3(0f, sign, 0f) : new Vector3(0f, 0f, sign);

        static void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, List<float> verts, List<int> tris)
        {
            if (UMath.Dot(UMath.Cross(UMath.Sub(b, a), UMath.Sub(c, a)), outward) < 0f)
                (b, c) = (c, b);
            foreach (Vector3 p in new[] { a, b, c })
            {
                tris.Add(verts.Count / 3);
                verts.Add(p.X);
                verts.Add(p.Y);
                verts.Add(p.Z);
            }
        }

        static DtNavMesh Create(RcBuilderResult built, RcConfig cfg, PlayerMotor motor,
                                List<(Vector3 From, Vector3 To, LinkKind Kind)> links)
        {
            RcPolyMesh pm = built.Mesh;
            RcPolyMeshDetail dm = built.MeshDetail;
            var flags = new int[pm.npolys];
            Array.Fill(flags, WalkFlag);

            var p = new DtNavMeshCreateParams
            {
                verts = pm.verts, vertCount = pm.nverts,
                polys = pm.polys, polyAreas = pm.areas, polyFlags = flags, polyCount = pm.npolys, nvp = pm.nvp,
                detailMeshes = dm.meshes, detailVerts = dm.verts, detailVertsCount = dm.nverts,
                detailTris = dm.tris, detailTriCount = dm.ntris,
                walkableHeight = motor.StandHeight, walkableRadius = motor.BodyRadius, walkableClimb = motor.StandStepOffset,
                bmin = pm.bmin, bmax = pm.bmax, cs = cfg.Cs, ch = cfg.Ch, buildBvTree = true,
            };

            int n = links?.Count ?? 0;
            p.offMeshConCount = n;
            p.offMeshConVerts = new float[n * 6];
            p.offMeshConRad = new float[n];
            p.offMeshConFlags = new int[n];
            p.offMeshConAreas = new int[n];
            p.offMeshConDir = new int[n];
            p.offMeshConUserID = new int[n];
            for (int i = 0; i < n; i++)
            {
                (Vector3 from, Vector3 to, LinkKind kind) = links[i];
                p.offMeshConVerts[i * 6 + 0] = from.X;
                p.offMeshConVerts[i * 6 + 1] = from.Y;
                p.offMeshConVerts[i * 6 + 2] = from.Z;
                p.offMeshConVerts[i * 6 + 3] = to.X;
                p.offMeshConVerts[i * 6 + 4] = to.Y;
                p.offMeshConVerts[i * 6 + 5] = to.Z;
                p.offMeshConRad[i] = motor.BodyRadius;
                p.offMeshConFlags[i] = WalkFlag;
                p.offMeshConAreas[i] = kind == LinkKind.Drop ? DropArea : kind == LinkKind.Jump ? JumpArea : DoubleJumpArea;
                p.offMeshConUserID[i] = i;
            }

            var nav = new DtNavMesh();
            nav.Init(DtNavMeshBuilder.CreateNavMeshData(p), pm.nvp, 0);
            return nav;
        }

        /// <summary>
        /// Walks every open edge of the mesh in 1 m steps and looks out
        /// across it for another level within reach: lower is a drop,
        /// higher a jump or double jump. A flight that would meet the arena
        /// is no link.
        /// </summary>
        static List<(Vector3, Vector3, LinkKind)> FindLinks(DtNavMesh nav, WorldQueries world, BotSettings s)
        {
            var found = new List<(Vector3, Vector3, LinkKind)>();
            var q = new DtNavMeshQuery(nav);
            var any = new DtQueryDefaultFilter();
            var near = new long[16];
            float[] reach = { 0.5f, 0.8f, 1.2f, 1.6f, 2f, 2.5f, s.GapMax };

            DtMeshTile tile = nav.GetTile(0);
            float[] v = tile.data.verts;
            for (int pi = 0; pi < tile.data.header.polyCount; pi++)
            {
                DtPoly poly = tile.data.polys[pi];
                if (poly.GetPolyType() != DtPolyTypes.DT_POLYTYPE_GROUND)
                    continue;

                Vector3 centre = Vector3.Zero;
                for (int j = 0; j < poly.vertCount; j++)
                    centre = UMath.Add(centre, Vert(v, poly.verts[j]));
                centre = UMath.Mul(centre, 1f / poly.vertCount);

                for (int j = 0; j < poly.vertCount; j++)
                {
                    if (poly.neis[j] != 0)
                        continue;
                    Vector3 a = Vert(v, poly.verts[j]), b = Vert(v, poly.verts[(j + 1) % poly.vertCount]);
                    Vector3 edge = new Vector3(b.X - a.X, 0f, b.Z - a.Z);
                    float length = UMath.Magnitude(edge);
                    if (length < 0.3f)
                        continue;
                    Vector3 outward = UMath.Normalized(new Vector3(edge.Z, 0f, -edge.X));
                    if (UMath.Dot(outward, UMath.Sub(UMath.Lerp(a, b, 0.5f), centre)) < 0f)
                        outward = UMath.Neg(outward);

                    int samples = Math.Max(1, (int)(length / 1f));
                    for (int k = 0; k < samples; k++)
                    {
                        Vector3 from = UMath.Lerp(a, b, (k + 0.5f) / samples);
                        bool dropped = false, jumped = false;
                        foreach (float d in reach)
                        {
                            Vector3 to = UMath.Add(from, UMath.Mul(outward, d));
                            q.QueryPolygons(new RcVec3f(to.X, from.Y, to.Z), new RcVec3f(0.1f, s.DoubleJumpUp + 3f, 0.1f),
                                            any, near, out int count, near.Length);
                            for (int i = 0; i < count; i++)
                            {
                                if (q.GetPolyHeight(near[i], new RcVec3f(to.X, from.Y, to.Z), out float h).Failed())
                                    continue;
                                float rise = h - from.Y;
                                var land = new Vector3(to.X, h, to.Z);
                                if (!dropped && rise < -0.6f && Clear(world, from, land, from.Y + 1.2f))
                                {
                                    found.Add((from, land, LinkKind.Drop));
                                    dropped = true;
                                }
                                else if (!jumped && rise > 0.5f && rise <= s.DoubleJumpUp &&
                                         Clear(world, from, land, h + 0.4f) && Headroom(world, from, rise + 1.9f))
                                {
                                    found.Add((from, land, rise <= s.JumpUp ? LinkKind.Jump : LinkKind.DoubleJump));
                                    jumped = true;
                                }
                            }
                            if (dropped || jumped)
                                break;
                        }
                    }
                }
            }
            return found;
        }

        static Vector3 Vert(float[] v, int i) => new Vector3(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);

        // Level at the given height from over the take-off to over the landing.
        static bool Clear(WorldQueries world, Vector3 from, Vector3 to, float height)
        {
            Vector3 a = new Vector3(from.X, height, from.Z), b = new Vector3(to.X, height, to.Z);
            Vector3 d = UMath.Sub(b, a);
            float length = UMath.Magnitude(d);
            return length < 1e-4f || !world.Raycast(a, UMath.Mul(d, 1f / length), length, WorldQueries.WorldMask, out _);
        }

        static bool Headroom(WorldQueries world, Vector3 from, float up)
            => !world.Raycast(UMath.Add(from, new Vector3(0f, 0.1f, 0f)), UMath.Up, up, WorldQueries.WorldMask, out _);

        /// <summary>The closest point of the mesh, at most 1,5 m aside and 3 m up or down.</summary>
        public bool Nearest(Vector3 feet, out Vector3 onMesh)
        {
            onMesh = feet;
            if (query.FindNearestPoly(V(feet), Extents, filter, out long poly, out RcVec3f p, out _).Failed() || poly == 0)
                return false;
            onMesh = new Vector3(p.X, p.Y, p.Z);
            return true;
        }

        /// <summary>
        /// The corners from the feet to the goal, the first being the start.
        /// When the goal is out of reach the path ends as close as it gets.
        /// </summary>
        public bool Plan(Vector3 feet, Vector3 goal, List<Corner> corners)
        {
            corners.Clear();
            if (query.FindNearestPoly(V(feet), Extents, filter, out long start, out RcVec3f a, out _).Failed() || start == 0 ||
                query.FindNearestPoly(V(goal), Extents, filter, out long end, out RcVec3f b, out _).Failed() || end == 0)
                return false;

            if (query.FindPath(start, end, a, b, filter, path, out int count, MaxPath).Failed() || count == 0)
                return false;

            // A partial path ends on the polygon closest to the goal, not at it.
            if (path[count - 1] != end)
                query.ClosestPointOnPoly(path[count - 1], b, out b, out _);

            if (query.FindStraightPath(a, b, path, count, straight, out int n, MaxCorners, 0).Failed())
                return false;

            for (int i = 0; i < n; i++)
            {
                DtStraightPath c = straight[i];
                corners.Add(new Corner
                {
                    Position = new Vector3(c.pos.X, c.pos.Y, c.pos.Z),
                    Link = (c.flags & DtStraightPathFlags.DT_STRAIGHTPATH_OFFMESH_CONNECTION) != 0 ? KindOf(c.refs) : LinkKind.None,
                });
            }
            return n > 0;
        }

        LinkKind KindOf(long poly)
        {
            if (mesh.GetTileAndPolyByRef(poly, out DtMeshTile tile, out DtPoly p).Failed() ||
                p.GetPolyType() != DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION)
                return LinkKind.None;
            DtOffMeshConnection con = tile.data.offMeshCons[p.index - tile.data.header.offMeshBase];
            return links[con.userId].Kind;
        }

        /// <summary>
        /// Where the mesh ends nearest to a point and in which direction,
        /// level only; false when no edge is within the radius.
        /// </summary>
        public bool NearestEdge(Vector3 feet, float radius, out Vector3 edge, out float distance)
        {
            edge = Vector3.Zero;
            distance = radius;
            if (query.FindNearestPoly(V(feet), Extents, filter, out long poly, out RcVec3f p, out _).Failed() || poly == 0)
                return false;
            if (query.FindDistanceToWall(poly, p, radius, filter, out float hit, out RcVec3f at, out _).Failed() || hit >= radius)
                return false;
            distance = hit;
            edge = new Vector3(at.X, at.Y, at.Z);
            return true;
        }

        static RcVec3f V(Vector3 v) => new RcVec3f(v.X, v.Y, v.Z);
    }
}
