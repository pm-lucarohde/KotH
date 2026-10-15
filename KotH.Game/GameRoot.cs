using KotH.Client.Player;
using KotH.Shared.Hosting;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.BepuPhysics;
using Stride.Core;
using Stride.Engine;
using Stride.Rendering;

namespace KotH.Client
{
    /// <summary>
    /// Builds the playable world when the scene starts: the arena's collision,
    /// the player and the dummy, in exactly the order the server and the
    /// probes use, so the same inputs give the same ticks everywhere. The
    /// scene itself only carries what is drawn.
    /// </summary>
    [DataContract("KotH.GameRoot")]
    public sealed class GameRoot : StartupScript
    {
        [Display("Körpermodell", "Darstellung")] public Model BodyModel;

        public override void Start()
        {
            BepuSimulation physics = PhysicsWorld();
            var queries = new WorldQueries(physics);
            var system = new SimulationSystem(queries);
            system.Activate();

            // Before any body joins: their processor looks the system up.
            Services.AddService(system);

            var builder = new WorldBuilder(Entity.Scene, Content, queries);
            builder.LoadArena();

            var reader = new PlayerInputReader();
            Entity player = Spawn(builder, WorldBuilder.PlayerBodyUrl, "PlayerSpawn", "Player", reader);
            Entity dummy = Spawn(builder, WorldBuilder.DummyBodyUrl, "DummySpawn", "Dummy");

            var visuals = Entity.Get<PlayerVisualSystem>();
            AddVisual(visuals, player);
            AddVisual(visuals, dummy);

            Entity.Get<SimulationDriver>().LocalInput = reader;

            foreach (Entity e in Entity.Scene.Entities)
            {
                var camera = e.Get<FirstPersonCamera>();
                if (camera == null)
                    continue;
                camera.Motor = player.Get<PlayerMotor>();
                camera.Reader = reader;
            }
        }

        /// <summary>
        /// The scene's physics world. Stride creates it with the first
        /// collider; the arena is not loaded yet, so it may not exist.
        /// </summary>
        BepuSimulation PhysicsWorld()
        {
            var config = Services.GetService<BepuConfiguration>();
            if (config == null)
            {
                config = new BepuConfiguration { BepuSimulations = { new BepuSimulation() } };
                Services.AddService(config);
            }

            return config.BepuSimulations[0];
        }

        static Entity Spawn(WorldBuilder builder, string url, string spawn, string name,
                            params EntityComponent[] extra)
        {
            Entity marker = builder.Find(spawn);
            Entity body = builder.SpawnBody(url, marker.Transform.Position, marker.Transform.Rotation, extra);
            body.Name = name;
            return body;
        }

        void AddVisual(PlayerVisualSystem system, Entity body)
        {
            var visual = new Entity(body.Name + "Visual") { new ModelComponent(BodyModel) };
            Entity.Scene.Entities.Add(visual);
            system.Register(visual, body.Get<PlayerMotor>());
        }
    }
}
