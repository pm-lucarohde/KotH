using System;
using System.Collections.Generic;
using KotH.Shared.Match;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering;

namespace KotH.Client.Player
{
    /// <summary>The duo chain between partners (Luca: metal, 3.5 m, at the belt, sagging, lying on the ground):
    /// per pair a rope of 32 links from hip to hip, moved by Verlet each frame, drawn as one instanced model.
    /// It lies on the map's ground (rays down). Presentation only: the simulation's chain is SimulationSystem's.</summary>
    [DataContract("KotH.ChainView")]
    public sealed class ChainView : SyncScript
    {
        const int Links = 32, Steps = 16, Probe = 4;
        const float Damping = 0.985f, Gravity = 9.81f, Thick = 0.012f, NearEye = 0.45f;
        const string HipsBone = "Hips", FeetBone = "Root";

        [DataMemberIgnore] public SimulationSystem Simulation { get; set; }
        [DataMemberIgnore] public Model Link { get; set; }

        /// <summary>The figure drawn for a body, null for none.</summary>
        [DataMemberIgnore] public Func<SimulationBody, Entity> VisualOf { get; set; }

        sealed class Rope
        {
            public SimulationBody A, B;
            public Entity Entity;
            public InstancingUserArray Instances;
            public ModelComponent Model;
            public readonly Vector3[] Now = new Vector3[Links + 1], Before = new Vector3[Links + 1];
            public readonly float[] Floor = new float[Links + 1];
            public readonly Matrix[] Matrices = new Matrix[Links];
            public bool Fresh = true, Seen;
        }

        readonly List<Rope> ropes = new List<Rope>();
        Vector3? hips, feet;
        PlayerCamera camera;

        public ChainView()
        {
            Priority = 1060;   // after the figures (500) and the camera (1000): hips and eye of this frame
        }

        public override void Update()
        {
            foreach (Rope r in ropes)
                r.Seen = false;
            IReadOnlyList<SimulationBody> bodies = Simulation?.Bodies;
            if (Simulation?.Match is { Duo: true } && bodies != null && Link != null)
            {
                float dt = Math.Clamp((float)Game.UpdateTime.Elapsed.TotalSeconds, 1e-4f, 1f / 30f);
                for (int i = 0; i < bodies.Count; i++)
                {
                    SimulationBody a = bodies[i];
                    if (a.Partner == 0 || HillMatch.PartnerOf(a, bodies) is not { } b || b.NetId < a.NetId)
                        continue;
                    Entity va = VisualOf?.Invoke(a), vb = VisualOf?.Invoke(b);
                    if (va == null || vb == null)
                        continue;
                    Rope r = RopeOf(a, b);
                    r.Seen = true;
                    Move(r, Belt(va), Belt(vb), Feet(va), Feet(vb), dt);
                    Draw(r);
                }
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

        /// <summary>The height of the soles: the Root bone (the entity sits at the body's centre, 0.9 m up).</summary>
        float Feet(Entity visual)
        {
            feet ??= Rest(visual.Get<ModelComponent>()?.Model?.Skeleton, FeetBone);
            return Vector3.TransformCoordinate(feet.Value, visual.Transform.WorldMatrix).Y;
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

        // Verlet with the ends held at the belts; the links keep their length, no node below the ground.
        void Move(Rope r, Vector3 from, Vector3 to, float footA, float footB, float dt)
        {
            Ground(r, from, to, footA, footB);
            float segment = Simulation.ChainLength / Links;
            Vector3[] p = r.Now, q = r.Before;
            if (r.Fresh || Vector3.DistanceSquared(p[0], from) > 4f || Vector3.DistanceSquared(p[Links], to) > 4f)
            {
                for (int k = 0; k <= Links; k++)
                    p[k] = q[k] = Vector3.Lerp(from, to, k / (float)Links);
                r.Fresh = false;
            }
            var fall = new Vector3(0f, -Gravity * dt * dt, 0f);
            for (int k = 1; k < Links; k++)
            {
                Vector3 now = p[k];
                p[k] += (now - q[k]) * Damping + fall;
                q[k] = now;
            }
            p[0] = q[0] = from;
            p[Links] = q[Links] = to;
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
                for (int k = 1; k < Links; k++)
                    if (p[k].Y < r.Floor[k])
                        p[k].Y = r.Floor[k];
            }
        }

        // The map's ground under every Probe-th node by a ray down from above both belts, linear in between;
        // where a ray finds none, the line between the feet. Update runs apart from the ticks (SimulationJob).
        void Ground(Rope r, Vector3 from, Vector3 to, float footA, float footB)
        {
            Vector3[] p = r.Now;
            float top = Math.Max(from.Y, to.Y) + 0.5f, reach = top - Math.Min(footA, footB) + 2f;
            for (int k = 0; k <= Links; k += Probe)
            {
                float line = MathUtil.Lerp(footA, footB, k / (float)Links);
                var start = new Vector3(p[k].X, Math.Max(top, p[k].Y + 0.5f), p[k].Z);
                bool hit = Simulation.World.Raycast(start, new Vector3(0f, -1f, 0f), reach, WorldQueries.WorldMask, out QueryHit h);
                r.Floor[k] = (hit ? start.Y - h.Distance : line) + Thick;
            }
            for (int k = 0; k < Links; k++)
                if (k % Probe != 0)
                {
                    int a = k - k % Probe;
                    r.Floor[k] = MathUtil.Lerp(r.Floor[a], r.Floor[a + Probe], (k - a) / (float)Probe);
                }
        }

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
                Vector3 side = Vector3.Cross(Vector3.UnitY, z);
                side = side.LengthSquared() > 1e-6f ? Vector3.Normalize(side) : Vector3.UnitX;
                Vector3 x = (k & 1) == 0 ? side : Vector3.Cross(z, side);
                Vector3 y = Vector3.Cross(z, x);
                Matrix m = Matrix.Identity;
                m.Right = x;
                m.Up = y;
                m.Backward = z;
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
