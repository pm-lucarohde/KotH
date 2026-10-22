using System;
using System.Collections.Generic;
using KotH.Shared.Match;
using KotH.Shared.Mathematics;
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
        public int Floor;            // the grid floor, -1 for the feet the path starts at
    }

    /// <summary>
    /// Where the bots can go: the climbing grid of the arena's static
    /// colliders (ClimbGrid), with a field of seconds to the zone for the
    /// hill and a bounded A* for every other goal. Every query is a pure
    /// function of the arena and the seed, so all runs agree.
    /// </summary>
    public sealed class BotNavigation
    {
        const int MaxNodes = 4000;
        const int ZoneLookahead = 300;

        readonly ClimbGrid grid;

        BotNavigation(ClimbGrid grid) => this.grid = grid;

        public ClimbGrid Grid => grid;

        /// <summary>
        /// Cuts the grid for a body shaped like the motor: its height, step,
        /// slope limit and jumps. With a zone it also lays the fields to it,
        /// the variants' noise seeded from seed, their sides round the start.
        /// </summary>
        public static BotNavigation Build(IEnumerable<Entity> arena, PlayerMotor motor, BotSettings s, HillMatch zone, int seed,
                                          Vector3 start)
        {
            var tris = new List<ClimbGrid.Triangle>();
            foreach (Entity e in arena)
                if (e.Get<StaticComponent>()?.Collider is CompoundCollider compound)
                    foreach (ColliderBase c in compound.Colliders)
                        AddShape(e.Transform.Position, e.Transform.Rotation, c, tris);
            return new BotNavigation(ClimbGrid.Build(tris, motor, s, zone, seed, start));
        }

        // A box as 12 triangles, a triangle as itself, wound so the face
        // normal points out of the solid (the grid tells floors from ceilings).
        static void AddShape(Vector3 at, Quaternion turn, ColliderBase c, List<ClimbGrid.Triangle> tris)
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
                        AddTriangle(a, b, d, outward, tris);
                        AddTriangle(a, d, e, outward, tris);
                    }
            }
            else if (c is TriangleCollider tri)
            {
                // Wound into the solid, as the generators lay them.
                Vector3 a = World(tri.A), b = World(tri.B), d = World(tri.C);
                tris.Add(new ClimbGrid.Triangle { A = a, B = d, C = b });
            }
        }

        static Vector3 Axis(int axis, int sign)
            => axis == 0 ? new Vector3(sign, 0f, 0f) : axis == 1 ? new Vector3(0f, sign, 0f) : new Vector3(0f, 0f, sign);

        static void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, List<ClimbGrid.Triangle> tris)
        {
            if (UMath.Dot(UMath.Cross(UMath.Sub(b, a), UMath.Sub(c, a)), outward) < 0f)
                (b, c) = (c, b);
            tris.Add(new ClimbGrid.Triangle { A = a, B = b, C = c });
        }

        /// <summary>The closest floor, at most 1,5 m aside and 3 m up or down.</summary>
        public bool Nearest(Vector3 feet, out Vector3 onMesh) => grid.Nearest(feet, out onMesh);

        /// <summary>
        /// The corners from the feet to the goal, the first being the start.
        /// When the goal is out of reach the path ends as close as it gets.
        /// </summary>
        public bool Plan(Vector3 feet, Vector3 goal, List<Corner> corners)
            => grid.Plan(feet, goal, MaxNodes, corners);

        /// <summary>The next stretch towards the zone down a route variant (0 the plain one), learning on the way.</summary>
        public bool PlanToZone(Vector3 feet, int variant, ClimbGrid.Memory memory, List<Corner> corners)
            => grid.PlanToZone(feet, variant, memory, ZoneLookahead, corners);

        /// <summary>
        /// The variant a bot takes from its start: the preferred one if it is
        /// at most detour slower than the best way, else the next that is,
        /// else the plain field.
        /// </summary>
        public int ChooseVariant(Vector3 feet, int preferred, float detour)
        {
            int n = grid.Variants;
            for (int k = 0; k < n; k++)
            {
                int v = 1 + (preferred + k) % n;
                if (grid.RouteTime(feet, v, out float seconds, out float best) && seconds <= best * (1f + detour))
                    return v;
            }
            return 0;
        }

        /// <summary>
        /// Where a fall of over a metre is nearest to a point, level only;
        /// false when none is within the radius.
        /// </summary>
        public bool NearestEdge(Vector3 feet, float radius, out Vector3 edge, out float distance)
            => grid.NearestEdge(feet, radius, out edge, out distance);
    }
}
