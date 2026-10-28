using KotH.Shared.Physics;
using Stride.BepuPhysics;
using Stride.Core;
using Stride.Core.Diagnostics;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.Shared.Combat
{
    /// <summary>Marks a body as something a swing may hit. No health, no damage: the hit
    /// itself comes from the hull's collision layer, this only says the hull is meant to
    /// be found and complains when it cannot be.</summary>
    [DataContract("KotH.Hittable")]
    [Display("Hittable")]
    [ComponentCategory("KotH")]
    public sealed class Hittable : EntityComponent
    {
        static readonly Logger Log = GlobalLogger.GetLogger("KotH");

        /// <summary>Reports a hull that no swing could ever find.</summary>
        public void Check(StaticComponent hull)
        {
            if (hull == null)
                Log.Warning($"Hittable without a collider is never hit: {Entity.Name}.");
            else if (hull.CollisionLayer != WorldQueries.HittableLayer)
                Log.Warning($"Hittable is not on layer 6: {Entity.Name}.");
        }
    }
}
