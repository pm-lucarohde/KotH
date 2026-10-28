using KotH.Shared.Hosting;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Engine;

namespace KotH.Probes.Harness
{
    /// <summary>What opening Prototype.unity and ProbeSubject.TryBind gave a probe: the
    /// arena, the training dummy in the tick, and the player's motor out of it. Unity's
    /// probes switched the player's input reader off and ticked the motor themselves;
    /// here the player simply has no reader.</summary>
    public sealed class Prototype
    {
        public Entity Player { get; private set; }
        public PlayerMotor Motor { get; private set; }
        public Entity Dummy { get; private set; }

        public static Prototype Open(HeadlessWorld world)
        {
            world.LoadArena(Maps.Test);

            Entity playerSpawn = world.Find("PlayerSpawn");
            Entity dummySpawn = world.Find("DummySpawn");

            var p = new Prototype();
            p.Player = world.SpawnBody(HeadlessWorld.PlayerBodyUrl,
                playerSpawn.Transform.Position, playerSpawn.Transform.Rotation);
            p.Player.Name = "Player";
            p.Motor = SimulationBody.Wire(p.Player, world.Queries);

            // The dummy's own SimulationBody puts it into the tick.
            p.Dummy = world.SpawnBody(HeadlessWorld.DummyBodyUrl,
                dummySpawn.Transform.Position, dummySpawn.Transform.Rotation);
            p.Dummy.Name = "Dummy";
            return p;
        }
    }
}
