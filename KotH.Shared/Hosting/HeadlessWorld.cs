using System;
using System.Collections.Generic;
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
        public const string ArenaUrl = "/KotH.Shared/Arena/ArenaCollision";
        public const string PlayerBodyUrl = "/KotH.Shared/Bodies/PlayerBody";
        public const string DummyBodyUrl = "/KotH.Shared/Bodies/DummyBody";

        readonly ServiceRegistry services = new ServiceRegistry();
        readonly ContentManager content;
        readonly SceneInstance sceneInstance;

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
        }

        public BepuSimulation Physics { get; }
        public WorldQueries Queries { get; }
        public SimulationSystem Simulation { get; }
        public Scene Scene { get; }
        public ContentManager Content => content;

        /// <summary>
        /// Adds every collider of the arena prefab, in prefab order, and gives
        /// each its place in the stable query order.
        /// </summary>
        public void LoadArena()
        {
            foreach (Entity e in content.Load<Prefab>(ArenaUrl).Instantiate())
                AddStatic(e);
        }

        /// <summary>
        /// Adds world geometry built in code, as the probes do. Registered
        /// in the stable order right after whatever came before it.
        /// </summary>
        public void AddStatic(Entity entity)
        {
            Scene.Entities.Add(entity);
            RegisterColliders(entity);
        }

        /// <summary>
        /// Spawns a body prefab at a pose, with an extra component added
        /// before it enters the scene, typically its SimulationBody.
        /// </summary>
        public Entity SpawnBody(string prefabUrl, Vector3 position, Quaternion rotation,
                                params EntityComponent[] extra)
        {
            List<Entity> parts = content.Load<Prefab>(prefabUrl).Instantiate();
            Entity root = parts[0];
            root.Transform.Position = position;
            root.Transform.Rotation = rotation;

            foreach (EntityComponent c in extra)
                root.Add(c);

            foreach (Entity e in parts)
                Scene.Entities.Add(e);

            return root;
        }

        /// <summary>A top-level entity by name, GameObject.Find for the arena.</summary>
        public Entity Find(string name)
        {
            foreach (Entity e in Scene.Entities)
                if (e.Name == name)
                    return e;
            return null;
        }

        /// <summary>Takes world geometry out, or puts it back: SetActive.</summary>
        public void SetActive(Entity entity, bool active)
        {
            bool inside = entity.Scene != null;
            if (active && !inside)
                Scene.Entities.Add(entity);
            else if (!active && inside)
                Scene.Entities.Remove(entity);
        }

        void RegisterColliders(Entity entity)
        {
            foreach (CollidableComponent c in entity.GetAll<CollidableComponent>())
                Queries.Register(c);

            foreach (TransformComponent child in entity.Transform.Children)
                RegisterColliders(child.Entity);
        }

        public void Dispose()
        {
            Simulation.Deactivate();
            sceneInstance.Dispose();
        }
    }
}
