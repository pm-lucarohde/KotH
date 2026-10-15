using Stride.Core.Diagnostics;
using Stride.Engine;

namespace KotH.Shared.Simulation
{
    /// <summary>
    /// Puts bodies into the tick when their entity enters a scene and takes
    /// them out when it leaves. The SimulationSystem is found as a service,
    /// so the same processor serves the game, the server and the probes.
    /// </summary>
    public sealed class SimulationBodyProcessor : EntityProcessor<SimulationBody>
    {
        static readonly Logger Log = GlobalLogger.GetLogger("KotH");

        protected override void OnEntityComponentAdding(Entity entity, SimulationBody component, SimulationBody data)
        {
            SimulationSystem system = Services.GetService<SimulationSystem>();
            if (system == null)
            {
                // Without a driver nothing moves at all and nothing says why.
                Log.Warning($"Kein SimulationSystem, {entity.Name} wird nicht getickt.");
                return;
            }

            component.Attach(system.World);
            SimulationSystem.Register(component);
        }

        protected override void OnEntityComponentRemoved(Entity entity, SimulationBody component, SimulationBody data)
        {
            SimulationSystem.Unregister(component);
        }
    }
}
