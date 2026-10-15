using System;
using KotH.Shared.Physics;
using KotH.Shared.Simulation;
using Stride.BepuPhysics;
using Stride.Core;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Core.Storage;
using Stride.Engine;

namespace KotH.Shared.Hosting
{
    /// <summary>
    /// A simulation without a game: no window, no GPU, no game loop. Loads
    /// compiled prefabs, keeps them in a scene whose processors register
    /// colliders and bodies, and owns the SimulationSystem. The server and
    /// the probes both run on this; the game builds the same parts itself.
    /// </summary>
    public sealed class HeadlessWorld : IDisposable
    {
        public const string ArenaUrl = WorldBuilder.ArenaUrl;
        public const string PlayerBodyUrl = WorldBuilder.PlayerBodyUrl;
        public const string DummyBodyUrl = WorldBuilder.DummyBodyUrl;

        readonly ServiceRegistry services = new ServiceRegistry();
        readonly ContentManager content;
        readonly SceneInstance sceneInstance;
        readonly WorldBuilder builder;

        public HeadlessWorld()
        {
            // The physics world is pure CPU. Registering the configuration
            // up front keeps Stride from looking for a game to attach it to.
            Physics = new BepuSimulation();
            services.AddService(new BepuConfiguration { BepuSimulations = { Physics } });

            var database = ObjectDatabase.CreateDefaultDatabase();
            var provider = new DatabaseFileProvider(database);
            services.AddService<IDatabaseFileProviderService>(new DatabaseFileProviderService(provider));
            content = new ContentManager(services);

            Queries = new WorldQueries(Physics);
            Simulation = new SimulationSystem(Queries);
            Simulation.Activate();
            services.AddService(Simulation);

            // Starts empty: entities added afterwards enter in exactly the
            // order they are added, so collider handles are the same every run.
            Scene = new Scene();
            sceneInstance = new SceneInstance(services, Scene);
            builder = new WorldBuilder(Scene, content, Queries);
        }

        public BepuSimulation Physics { get; }
        public WorldQueries Queries { get; }
        public SimulationSystem Simulation { get; }
        public Scene Scene { get; }
        public ContentManager Content => content;

        public void LoadArena() => builder.LoadArena();

        public void AddStatic(Entity entity) => builder.AddStatic(entity);

        public Entity SpawnBody(string prefabUrl, Vector3 position, Quaternion rotation,
                                params EntityComponent[] extra)
            => builder.SpawnBody(prefabUrl, position, rotation, extra);

        public Entity Find(string name) => builder.Find(name);

        public void SetActive(Entity entity, bool active) => builder.SetActive(entity, active);

        public void Dispose()
        {
            Simulation.Deactivate();
            sceneInstance.Dispose();
        }
    }
}
