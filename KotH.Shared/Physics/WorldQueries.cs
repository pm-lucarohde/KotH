using System.Collections.Generic;
using Stride.BepuPhysics;
using Stride.BepuPhysics.Definitions;
using Stride.Core.Mathematics;
using KotH.Shared.Mathematics;
using BepuCapsule = BepuPhysics.Collidables.Capsule;
using BepuSphere = BepuPhysics.Collidables.Sphere;

namespace KotH.Shared.Physics
{
    /// <summary>One hit of a query, in the shape the simulation code expects.</summary>
    public struct QueryHit
    {
        public float Distance;
        public Vector3 Normal;
        public Vector3 Point;
        public CollidableComponent Collider;
    }

    /// <summary>
    /// The physics queries the simulation asks, answered by Bepu but with the
    /// PhysX semantics the motor was tuned on: a cast that starts inside a
    /// collider reports it at distance zero with the normal against the cast,
    /// and a ray that starts inside a collider does not see it at all.
    /// Every query is a pure function of the world, no state is kept here.
    /// </summary>
    public sealed class WorldQueries
    {
        /// <summary>Layer of every body's hit hull, Unity's "Hittable" (6).</summary>
        public const CollisionLayer HittableLayer = CollisionLayer.Layer6;

        public const CollisionMask HittableMask = CollisionMask.Layer6;

        /// <summary>What movement collides with: everything but bodies.</summary>
        public const CollisionMask WorldMask = CollisionMask.Everything & ~CollisionMask.Layer6;

        // Bepu reports a face that is merely touched with a depth of float
        // noise, up to about 1e-5 far from the origin; PhysX reported none,
        // and its smallest real depth in 1 733 dumped cases was 6.4e-5.
        const float TouchingDepth = 1e-5f;

        readonly BepuSimulation simulation;

        // Reused, so a query allocates nothing once they have grown.
        readonly List<HitInfo> hitBuffer = new List<HitInfo>(64);
        readonly List<OverlapInfo> overlapBuffer = new List<OverlapInfo>(64);
        readonly QueryHit[] anyHits = new QueryHit[16];

        // Stable order for colliders, by registration. Bepu reports overlaps
        // in no defined order, and float sums depend on the order they run in.
        readonly Dictionary<CollidableComponent, int> ids = new Dictionary<CollidableComponent, int>();

        public WorldQueries(BepuSimulation simulation)
        {
            this.simulation = simulation;
        }

        public BepuSimulation Simulation => simulation;

        /// <summary>
        /// Gives a collider its place in the stable order. World geometry is
        /// registered in load order, which is the same on every run.
        /// </summary>
        public void Register(CollidableComponent collider)
        {
            if (!ids.ContainsKey(collider))
                ids.Add(collider, ids.Count);
        }

        public int IdOf(CollidableComponent collider)
            => ids.TryGetValue(collider, out int id) ? id : int.MaxValue;

        /// <summary>
        /// Closest hit along a ray, PhysX style: colliders the ray starts in
        /// are not reported. One can be skipped explicitly, for a swing that
        /// starts inside its own body's hull.
        /// </summary>
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance,
                            CollisionMask mask, out QueryHit hit,
                            CollidableComponent ignore = null)
        {
            hit = default;
            hitBuffer.Clear();
            simulation.RayCastPenetrating(origin, direction, maxDistance, hitBuffer, mask);

            float nearest = float.MaxValue;
            for (int i = 0; i < hitBuffer.Count; i++)
            {
                HitInfo h = hitBuffer[i];
                if (h.Collidable == ignore || h.Distance <= 0f)
                    continue;

                if (h.Distance < nearest)
                {
                    nearest = h.Distance;
                    hit = ToHit(h);
                }
            }

            return nearest < float.MaxValue;
        }

        /// <summary>Every hit of a sphere swept along direction, like SphereCastNonAlloc.</summary>
        public int SphereCastAll(Vector3 origin, float radius, Vector3 direction,
                                 float maxDistance, CollisionMask mask, QueryHit[] results)
        {
            var shape = new BepuSphere(radius);
            var pose = new RigidPose(origin, Quaternion.Identity);
            return SweepAll(shape, pose, direction, maxDistance, mask, results);
        }

        /// <summary>
        /// Every hit of an upright capsule swept along direction, like
        /// CapsuleCastNonAlloc. bottom and top are the centres of its end
        /// spheres, top straight above bottom.
        /// </summary>
        public int CapsuleCastAll(Vector3 bottom, Vector3 top, float radius,
                                  Vector3 direction, float maxDistance,
                                  CollisionMask mask, QueryHit[] results)
        {
            Capsule(bottom, top, radius, out BepuCapsule shape, out RigidPose pose);
            return SweepAll(shape, pose, direction, maxDistance, mask, results);
        }

        /// <summary>
        /// Whether an upright capsule sweep hits anything, like the single
        /// Physics.CapsuleCast: colliders it starts inside are not reported,
        /// measured in Unity (CapsuleCastAll gives them at distance 0, the
        /// single cast returns false). Anything further along still counts.
        /// </summary>
        public bool CapsuleCastAny(Vector3 bottom, Vector3 top, float radius,
                                   Vector3 direction, float maxDistance, CollisionMask mask)
        {
            Capsule(bottom, top, radius, out BepuCapsule shape, out RigidPose pose);
            int count = SweepAll(shape, pose, direction, maxDistance, mask, anyHits);
            for (int i = 0; i < count; i++)
                if (anyHits[i].Distance > 0f)
                    return true;
            return false;
        }

        /// <summary>
        /// Colliders an upright capsule overlaps, each once, in stable order.
        /// Touching counts, as with OverlapCapsuleNonAlloc.
        /// </summary>
        public int OverlapCapsule(Vector3 bottom, Vector3 top, float radius,
                                  CollisionMask mask, CollidableComponent[] results)
        {
            Capsule(bottom, top, radius, out BepuCapsule shape, out RigidPose pose);

            overlapBuffer.Clear();
            simulation.Overlap(shape, pose, overlapBuffer, mask);

            int count = 0;
            for (int i = 0; i < overlapBuffer.Count && count < results.Length; i++)
            {
                CollidableComponent c = overlapBuffer[i].Collidable;
                if (!Contains(results, count, c))
                    results[count++] = c;
            }

            SortById(results, count);
            return count;
        }

        /// <summary>
        /// How far an upright capsule has to move to leave one collider, like
        /// Physics.ComputePenetration. Bepu answers per contact; the deepest
        /// contact of that collider stands for the whole overlap.
        /// </summary>
        public bool ComputePenetration(Vector3 bottom, Vector3 top, float radius,
                                       CollidableComponent other,
                                       out Vector3 direction, out float depth)
        {
            direction = Vector3.Zero;
            depth = 0f;

            Capsule(bottom, top, radius, out BepuCapsule shape, out RigidPose pose);

            overlapBuffer.Clear();
            simulation.Overlap(shape, pose, overlapBuffer, other.CollisionLayer.ToMask());

            bool found = false;
            for (int i = 0; i < overlapBuffer.Count; i++)
            {
                OverlapInfo o = overlapBuffer[i];
                if (o.Collidable != other || o.PenetrationLength <= TouchingDepth)
                    continue;

                if (!found || o.PenetrationLength > depth)
                {
                    depth = o.PenetrationLength;
                    // Bepu points into the other collider, PhysX out of
                    // it: the way the capsule has to move.
                    direction = UMath.Neg(o.PenetrationDirection);
                    found = true;
                }
            }

            return found;
        }

        int SweepAll<TShape>(TShape shape, RigidPose pose, Vector3 direction,
                             float maxDistance, CollisionMask mask, QueryHit[] results)
            where TShape : unmanaged, BepuPhysics.Collidables.IConvexShape
        {
            int count = 0;

            // Started inside: Bepu stays silent about those, PhysX reports
            // them at distance zero with the normal facing the cast.
            overlapBuffer.Clear();
            simulation.Overlap(shape, pose, overlapBuffer, mask);
            for (int i = 0; i < overlapBuffer.Count && count < results.Length; i++)
            {
                CollidableComponent c = overlapBuffer[i].Collidable;
                if (ContainsHit(results, count, c))
                    continue;

                results[count++] = new QueryHit
                {
                    Distance = 0f,
                    Normal = UMath.Neg(direction),
                    Point = pose.Position,
                    Collider = c,
                };
            }

            // The whole move as velocity over one unit of time. Bepu's compound
            // sweep prefilters its children with a ray of sweep * maximumT,
            // the sweep already scaled by maximumT: with unit speed it only
            // looked maximumT squared ahead and missed close boxes on short
            // moves. At maximumT 1 both agree; its thresholds scale along.
            int started = count;
            hitBuffer.Clear();
            var velocity = new BodyVelocity(UMath.Mul(direction, maxDistance), Vector3.Zero);
            simulation.SweepCastPenetrating(shape, pose, velocity, 1f, hitBuffer, mask);

            for (int i = 0; i < hitBuffer.Count && count < results.Length; i++)
            {
                HitInfo h = hitBuffer[i];
                if (ContainsHit(results, started, h.Collidable))
                    continue;

                QueryHit hit = ToHit(h);
                hit.Distance = h.Distance * maxDistance;
                results[count++] = hit;
            }

            return count;
        }

        static void Capsule(Vector3 bottom, Vector3 top, float radius,
                            out BepuCapsule shape, out RigidPose pose)
        {
            float length = top.Y - bottom.Y;
            shape = new BepuCapsule(radius, length);
            Vector3 middle = new Vector3(bottom.X, bottom.Y + length * 0.5f, bottom.Z);
            pose = new RigidPose(middle, Quaternion.Identity);
        }

        static QueryHit ToHit(in HitInfo h) => new QueryHit
        {
            Distance = h.Distance,
            Normal = h.Normal,
            Point = h.Point,
            Collider = h.Collidable,
        };

        static bool Contains(CollidableComponent[] items, int count, CollidableComponent c)
        {
            for (int i = 0; i < count; i++)
                if (items[i] == c)
                    return true;
            return false;
        }

        static bool ContainsHit(QueryHit[] items, int count, CollidableComponent c)
        {
            for (int i = 0; i < count; i++)
                if (items[i].Collider == c)
                    return true;
            return false;
        }

        void SortById(CollidableComponent[] items, int count)
        {
            for (int i = 1; i < count; i++)
            {
                CollidableComponent c = items[i];
                int id = IdOf(c);
                int j = i - 1;

                while (j >= 0 && IdOf(items[j]) > id)
                {
                    items[j + 1] = items[j];
                    j--;
                }

                items[j + 1] = c;
            }
        }
    }
}
