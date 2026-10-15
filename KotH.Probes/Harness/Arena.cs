using KotH.Shared.Hosting;
using KotH.Shared.Mathematics;
using Stride.BepuPhysics;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Harness
{
    /// <summary>
    /// A transform as Unity's probes saw it: position, rotation and scale in
    /// Unity's coordinates. Boxes in the arena carry their size in the
    /// collider with scale 1, so a box's size stands in for localScale.
    /// </summary>
    public readonly struct UnityTransform
    {
        public readonly Vector3 position;
        public readonly Quaternion rotation;
        public readonly Vector3 localScale;

        public UnityTransform(Vector3 position, Quaternion rotation, Vector3 localScale)
        {
            this.position = position;
            this.rotation = rotation;
            this.localScale = localScale;
        }

        public Vector3 up => UMath.Rotate(rotation, new Vector3(0f, 1f, 0f));
        public Vector3 forward => UMath.Rotate(rotation, new Vector3(0f, 0f, 1f));
        public Vector3 right => UMath.Rotate(rotation, new Vector3(1f, 0f, 0f));
    }

    /// <summary>
    /// The arena seen from a probe: GameObject.Find and CreatePrimitive, in
    /// Unity's coordinates. Every value crosses the mirror exactly.
    /// </summary>
    public static class Arena
    {
        /// <summary>GameObject.Find(name).transform, null-safe by throwing.</summary>
        public static UnityTransform Find(HeadlessWorld world, string name)
        {
            Entity e = world.Find(name) ?? throw new System.InvalidOperationException("Kein Objekt " + name + " in der Arena");
            Vector3 scale = new Vector3(1f, 1f, 1f);
            if (e.Get<StaticComponent>()?.Collider is CompoundCollider compound)
                foreach (ColliderBase c in compound.Colliders)
                    if (c is BoxCollider box)
                        scale = box.Size;

            return new UnityTransform(Ref.ToUnity(e.Transform.Position),
                                      Ref.ToUnity(e.Transform.Rotation), scale);
        }

        /// <summary>
        /// GameObject.CreatePrimitive(Cube) with its pose and scale, already
        /// synced: the unit cube's collider takes the scale as its size.
        /// Registered after everything before it in the stable query order.
        /// </summary>
        public static Entity AddBox(HeadlessWorld world, string name, Vector3 unityPosition,
                                    Quaternion unityRotation, Vector3 unityScale)
        {
            var entity = new Entity(name);
            entity.Transform.Position = Ref.Pos(unityPosition);
            entity.Transform.Rotation = Ref.Rot(unityRotation);

            var compound = new CompoundCollider();
            compound.Colliders.Add(new BoxCollider { Size = unityScale });
            entity.Add(new StaticComponent { Collider = compound });

            world.AddStatic(entity);
            return entity;
        }

        public static Entity AddBox(HeadlessWorld world, string name, Vector3 unityPosition, Vector3 unityScale)
            => AddBox(world, name, unityPosition, Quaternion.Identity, unityScale);
    }
}
