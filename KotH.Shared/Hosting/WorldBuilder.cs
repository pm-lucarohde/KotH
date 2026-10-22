using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using KotH.Shared.Bots;
using KotH.Shared.Match;
using KotH.Shared.Physics;
using Stride.BepuPhysics;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;

namespace KotH.Shared.Hosting
{
    /// <summary>
    /// Puts the arena and the bodies into a scene, the same way for the game,
    /// the server and the probes. Colliders get their place in the stable
    /// query order in the order they are added, so every host that builds
    /// the same world in the same order gets bit-identical answers.
    /// </summary>
    public sealed class WorldBuilder
    {
        public const string PlayerBodyUrl = "/KotH.Shared/Bodies/PlayerBody";
        public const string DummyBodyUrl = "/KotH.Shared/Bodies/DummyBody";

        readonly ContentManager content;
        readonly WorldQueries queries;

        public WorldBuilder(Scene scene, ContentManager content, WorldQueries queries)
        {
            Scene = scene;
            this.content = content;
            this.queries = queries;
        }

        public Scene Scene { get; }

        /// <summary>The loaded map's name, null until one is loaded.</summary>
        public string Map { get; private set; }

        /// <summary>The arena's capture zone, null until it is loaded or if it has none.</summary>
        public CaptureZone Zone { get; private set; }

        /// <summary>The arena's bot settings, null until it is loaded or if it has none.</summary>
        public BotSettings Bots { get; private set; }

        /// <summary>Every entity of the arena prefab, in prefab order; the bots cut their navmesh from it.</summary>
        public IReadOnlyList<Entity> Arena => arena;

        readonly List<Entity> arena = new List<Entity>();

        public bool HasMap(string map) => !string.IsNullOrEmpty(map) && content.Exists(Maps.CollisionUrl(map));

        /// <summary>
        /// Adds every collider of the map's arena prefab, then of its generated
        /// terrain if it has one, in prefab order, and gives each its place in
        /// the stable query order. Once per world.
        /// </summary>
        public void LoadArena(string map)
        {
            if (Map != null)
                throw new InvalidOperationException($"Die Map {Map} ist schon geladen.");
            if (!HasMap(map))
                throw new ArgumentException($"Unbekannte Map: {map}");
            Map = map;
            foreach (Entity e in content.Load<Prefab>(Maps.CollisionUrl(map)).Instantiate())
            {
                AddStatic(e);
                arena.Add(e);
                Zone ??= e.Get<CaptureZone>();
                Bots ??= e.Get<BotSettings>();
            }
            AddTerrain(map);
        }

        /// <summary>
        /// The generated ground (Tools/maps/mountain.py), if the map has one:
        /// per chunk, in file order, one static compound of its triangles, wound
        /// into the solid like the test arena's. Too many for a prefab.
        /// </summary>
        void AddTerrain(string map)
        {
            using Stream stream = typeof(WorldBuilder).Assembly.GetManifestResourceStream(Maps.TerrainResource(map));
            if (stream == null)
                return;
            using var reader = new BinaryReader(new ZLibStream(stream, CompressionMode.Decompress));
            if (reader.ReadUInt32() != 0x4E52544B || reader.ReadUInt32() != 1)   // "KTRN", version 1
                throw new InvalidDataException($"{Maps.TerrainResource(map)} hat ein unbekanntes Format.");

            var vertices = new Vector3[reader.ReadUInt32()];
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

            uint chunks = reader.ReadUInt32();
            for (uint c = 0; c < chunks; c++)
            {
                int x = reader.ReadInt32(), z = reader.ReadInt32();
                uint count = reader.ReadUInt32();
                var compound = new CompoundCollider();
                for (uint t = 0; t < count; t++)
                    compound.Colliders.Add(new TriangleCollider
                    {
                        A = vertices[reader.ReadUInt32()],
                        B = vertices[reader.ReadUInt32()],
                        C = vertices[reader.ReadUInt32()],
                    });
                var entity = new Entity($"Terrain_{x}_{z}") { new StaticComponent { Collider = compound } };
                AddStatic(entity);
                arena.Add(entity);
            }
        }

        /// <summary>
        /// Adds world geometry, from the arena or built in code. Registered
        /// in the stable order right after whatever came before it.
        /// </summary>
        public void AddStatic(Entity entity)
        {
            Scene.Entities.Add(entity);
            RegisterColliders(entity);
        }

        /// <summary>
        /// Spawns a body prefab at a pose, with extra components added before
        /// it enters the scene, typically its SimulationBody.
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

        /// <summary>
        /// A component of a prefab's root as authored, never put into a scene:
        /// settings to read, such as the motor a bot's navmesh is cut for.
        /// </summary>
        public T Template<T>(string prefabUrl) where T : EntityComponent
            => content.Load<Prefab>(prefabUrl).Instantiate()[0].Get<T>();

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
                queries.Register(c);

            foreach (TransformComponent child in entity.Transform.Children)
                RegisterColliders(child.Entity);
        }
    }
}
