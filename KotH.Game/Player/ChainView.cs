using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KotH.MapFormat;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Threading;
using Stride.Engine;
using Stride.Rendering;

namespace KotH.Client.Player
{
    /// <summary>The team chain (Luca: metal, 5 m per link of the row, at the belt, sagging, lying on the ground):
    /// per pair of neighbours a rope of 44 links from hip to hip, moved by Verlet each frame, drawn as one instanced model.
    /// Every node stops at the map and slides along it (Luca: never through floors). Presentation only.</summary>
    [DataContract("KotH.ChainView")]
    public sealed class ChainView : SyncScript
    {
        const int Links = 44, Steps = 16, CollideEvery = 8;
        const float Damping = 0.985f, Gravity = 9.81f, Thick = 0.012f, NearEye = 0.45f, MaxStretch = 1.5f;
        const string HipsBone = "Hips";

        [DataMemberIgnore] public SimulationSystem Simulation { get; set; }

        /// <summary>Its map's ground triangles, for the rays (ChainGround).</summary>
        [DataMemberIgnore] public WorldBuilder Builder { get; set; }
        [DataMemberIgnore] public Model Link { get; set; }

        /// <summary>The figure drawn for a body, null for none.</summary>
        [DataMemberIgnore] public Func<SimulationBody, Entity> VisualOf { get; set; }

        sealed class Rope
        {
            public SimulationBody A, B;
            public Entity Entity;
            public InstancingUserArray Instances;
            public ModelComponent Model;
            public readonly Vector3[] Now = new Vector3[Links + 1], Before = new Vector3[Links + 1], Safe = new Vector3[Links + 1];
            public readonly Matrix[] Matrices = new Matrix[Links];
            public Vector3 From, To;                 // the belts this frame
            public bool Fresh = true, Seen;
        }

        // Ropes share nothing but the map's grid, which they only read: they move on the workers.
        struct MoveJob : Dispatcher.IBatchJob
        {
            public ChainView View;

            public void Process(int start, int endExclusive)
            {
                for (int i = start; i < endExclusive; i++)
                    View.Move(View.moving[i]);
            }
        }

        readonly List<Rope> ropes = new List<Rope>(), moving = new List<Rope>();
        float dt, segment;
        Vector3? hips;
        PlayerCamera camera;

        public ChainView()
        {
            Priority = 1060;   // after the figures (500) and the camera (1000): hips and eye of this frame
        }

        public override void Update()
        {
            foreach (Rope r in ropes)
                r.Seen = false;
            moving.Clear();
            IReadOnlyList<SimulationBody> bodies = Simulation?.Bodies;
            if (Simulation?.Match is { Teamed: true } && bodies != null && Link != null)
            {
                Watch();
                dt = Math.Clamp((float)Game.UpdateTime.Elapsed.TotalSeconds, 1e-4f, 1f / 30f);
                segment = Simulation.ChainLength / Links;
                for (int i = 0; i < bodies.Count; i++)
                {
                    SimulationBody b = bodies[i];
                    if (b.Team == 0 || HillMatch.AheadOf(b, bodies) is not { } a)
                        continue;
                    Entity va = VisualOf?.Invoke(a), vb = VisualOf?.Invoke(b);
                    if (va == null || vb == null)
                        continue;
                    Rope r = RopeOf(a, b);
                    r.Seen = true;
                    r.From = Belt(va);
                    r.To = Belt(vb);
                    moving.Add(r);
                }
                // Bepu's rays (before the grid is done) stay on this thread.
                if (grid != null)
                    Dispatcher.ForBatched(moving.Count, new MoveJob { View = this });
                else
                    foreach (Rope r in moving)
                        Move(r);
                foreach (Rope r in moving)
                    Draw(r);
            }
            for (int i = ropes.Count - 1; i >= 0; i--)
                if (!ropes[i].Seen)
                {
                    Entity.Scene?.Entities.Remove(ropes[i].Entity);
                    ropes.RemoveAt(i);
                }
        }

        Rope RopeOf(SimulationBody a, SimulationBody b)
        {
            foreach (Rope r in ropes)
                if (r.A == a && r.B == b)
                    return r;
            var rope = new Rope { A = a, B = b, Instances = new InstancingUserArray() };
            rope.Model = new ModelComponent(Link) { IsShadowCaster = true };
            rope.Entity = new Entity("Chain") { rope.Model, new InstancingComponent { Type = rope.Instances } };
            Entity.Scene.Entities.Add(rope.Entity);
            ropes.Add(rope);
            return rope;
        }

        /// <summary>The hips bone at rest, carried by the figure as it stands this frame (crouch squash too).</summary>
        Vector3 Belt(Entity visual)
        {
            visual.Transform.UpdateWorldMatrix();
            hips ??= Rest(visual.Get<ModelComponent>()?.Model?.Skeleton, HipsBone);
            return Vector3.TransformCoordinate(hips.Value, visual.Transform.WorldMatrix);
        }

        static Vector3 Rest(Skeleton skeleton, string bone)
        {
            int i = skeleton == null ? -1 : Array.FindIndex(skeleton.Nodes, n => n.Name == bone);
            if (i < 0)
                return Vector3.Zero;
            // Up to the root, without the root's own: Stride puts the figure's world matrix in its place.
            Matrix m = Matrix.Identity;
            for (; i > 0; i = skeleton.Nodes[i].ParentIndex)
            {
                TransformTRS node = skeleton.Nodes[i].Transform;
                m *= Matrix.Scaling(node.Scale) * Matrix.RotationQuaternion(node.Rotation) * Matrix.Translation(node.Position);
            }
            return m.TranslationVector;
        }

        // Verlet with the ends held at the belts; the links keep their length, then every node is
        // stopped at the map on its way from where it last was clear (Collide). Taut round an edge the
        // rope gets longer than the chain instead of cutting through.
        void Move(Rope r)
        {
            Vector3 from = r.From, to = r.To;
            Vector3[] p = r.Now, q = r.Before;
            if (r.Fresh || Vector3.DistanceSquared(p[0], from) > 4f || Vector3.DistanceSquared(p[Links], to) > 4f)
            {
                Lay(r, from, to);
                r.Fresh = false;
            }
            var fall = new Vector3(0f, -Gravity * dt * dt, 0f);
            for (int k = 1; k < Links; k++)
            {
                Vector3 now = p[k];
                p[k] += (now - q[k]) * Damping + fall;
                q[k] = now;
            }
            p[0] = q[0] = r.Safe[0] = from;
            p[Links] = q[Links] = r.Safe[Links] = to;
            for (int s = 0; s < Steps; s++)
            {
                for (int k = 0; k < Links; k++)
                {
                    Vector3 d = p[k + 1] - p[k];
                    float length = d.Length();
                    if (length < 1e-6f)
                        continue;
                    Vector3 fix = d * ((length - segment) / length);
                    // An end belongs to its belt: the free node takes the whole correction there.
                    float wa = k == 0 ? 0f : k + 1 == Links ? 1f : 0.5f;
                    p[k] += fix * wa;
                    p[k + 1] -= fix * (1f - wa);
                }
                // Within the steps, so the nodes slide round an edge as they relax and a taut
                // rope spreads its stretch instead of piling it up at the edge.
                if ((s + 1) % CollideEvery == 0)
                    for (int k = 1; k < Links; k++)
                        Collide(r, k);
            }
            for (int k = 0; k < Links; k++)
                Unhook(r, k);
        }

        // A link whose straight piece runs into the map (round an edge or a wall's end): its free nodes
        // are lifted off the face it enters, as far as the far node lies behind it.
        void Unhook(Rope r, int k)
        {
            Vector3 a = r.Now[k], d = r.Now[k + 1] - a;
            float length = d.Length();
            if (length < 1e-5f || !Hit(a, d / length, length, out QueryHit h))
                return;
            float behind = Vector3.Dot(h.Point - r.Now[k + 1], h.Normal) + Thick;
            if (behind <= 0f)
                return;
            Vector3 lift = h.Normal * behind;
            if (k > 0)
                r.Now[k] = r.Safe[k] = Clear(a, lift);
            if (k + 1 < Links)
                r.Now[k + 1] = r.Safe[k + 1] = Clear(r.Now[k + 1], lift);
        }

        // A new rope from belt to belt, node after node from the previous one, each stopped at the map.
        void Lay(Rope r, Vector3 from, Vector3 to)
        {
            r.Now[0] = r.Before[0] = r.Safe[0] = from;
            for (int k = 1; k <= Links; k++)
            {
                Vector3 at = r.Now[k - 1], want = Vector3.Lerp(from, to, k / (float)Links);
                r.Now[k] = r.Before[k] = r.Safe[k] = k == Links ? to : Clear(at, want - at);
            }
        }

        // The node's move this frame, from its last clear place: stopped short of the map and the rest
        // slid along the surface; touching, it loses its speed (friction). Rays hit the map only.
        void Collide(Rope r, int k)
        {
            Vector3 from = r.Safe[k], move = r.Now[k] - from;
            if (!Blocked(from, move, out QueryHit h, out Vector3 at))
            {
                r.Safe[k] = r.Now[k];
                return;
            }
            Vector3 rest = r.Now[k] - at;
            rest -= h.Normal * Vector3.Dot(rest, h.Normal);
            at = Clear(at, rest);
            r.Now[k] = r.Before[k] = r.Safe[k] = at;
        }

        // How far a node gets along move: to Thick off the first surface, on its front (rays see one side).
        Vector3 Clear(Vector3 from, Vector3 move) => Blocked(from, move, out _, out Vector3 at) ? at : from + move;

        bool Blocked(Vector3 from, Vector3 move, out QueryHit h, out Vector3 at)
        {
            float length = move.Length();
            at = from + move;
            h = default;
            if (length < 1e-6f)
                return false;
            Vector3 dir = move / length;
            if (!Hit(from, dir, length + Thick, out h))
                return false;
            at = h.Point + h.Normal * Thick;
            return true;
        }

        // The map's own triangles once built for it (in the background, once per map); the physics until then.
        bool Hit(Vector3 from, Vector3 dir, float length, out QueryHit h)
        {
            h = default;
            if (grid == null)
                return Simulation.World.Raycast(from, dir, length, WorldQueries.WorldMask, out h);
            return grid.Raycast(from, dir, length, out h.Distance, out h.Point, out h.Normal);
        }

        // A new map's triangles start a grid; it is taken once done, if the map is still the same.
        void Watch()
        {
            MapCollision now = Builder?.Collision;
            if (grid != null && grid.Source != now)
                grid = null;
            if (building != null && building.IsCompleted)
            {
                if (building.Status == TaskStatus.RanToCompletion && building.Result.Source == now)
                    grid = building.Result;
                building = null;
            }
            if (now != null && grid == null && building == null)
                building = Task.Run(() => new ChainGround(now));
        }

        ChainGround grid;
        Task<ChainGround> building;

        // Each link between two nodes, every other turned a quarter round its length; the ones right at the
        // eye (the own belt in first person) are left out.
        void Draw(Rope r)
        {
            if (camera?.Entity.Scene == null)
                camera = FindCamera();
            Vector3 eye = camera?.Entity.Transform.WorldMatrix.TranslationVector ?? new Vector3(float.MaxValue);
            int count = 0;
            for (int k = 0; k < Links; k++)
            {
                Vector3 a = r.Now[k], b = r.Now[k + 1], mid = (a + b) * 0.5f;
                if (Vector3.DistanceSquared(mid, eye) < NearEye * NearEye)
                    continue;
                Vector3 z = b - a;
                float length = z.Length();
                z = length > 1e-6f ? z / length : Vector3.UnitZ;
                // Taut round an edge the links are longer: stretched to close the gaps, up to a limit.
                float stretch = Math.Clamp(length * Links / Simulation.ChainLength, 1f, MaxStretch);
                Vector3 side = Vector3.Cross(Vector3.UnitY, z);
                side = side.LengthSquared() > 1e-6f ? Vector3.Normalize(side) : Vector3.UnitX;
                Vector3 x = (k & 1) == 0 ? side : Vector3.Cross(z, side);
                Vector3 y = Vector3.Cross(z, x);
                Matrix m = Matrix.Identity;
                m.Right = x;
                m.Up = y;
                m.Backward = z * stretch;
                m.TranslationVector = mid;
                r.Matrices[count++] = m;
            }
            r.Instances.UpdateWorldMatrices(r.Matrices, count);
            r.Model.Enabled = count > 0;
        }

        PlayerCamera FindCamera()
        {
            foreach (Entity e in Entity.Scene.Entities)
                if (e.Get<PlayerCamera>() is { } c)
                    return c;
            return null;
        }

        public override void Cancel()
        {
            foreach (Rope r in ropes)
                Entity.Scene?.Entities.Remove(r.Entity);
            ropes.Clear();
        }
    }
}
