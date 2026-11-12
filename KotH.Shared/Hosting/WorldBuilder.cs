using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using KotH.MapFormat;
using KotH.Shared.Bots;
using KotH.Shared.Mathematics;
using KotH.Shared.Match;
using KotH.Shared.Net;
using KotH.Shared.Physics;
using Stride.BepuPhysics;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;

namespace KotH.Shared.Hosting
{
    /// <summary>Puts the arena and the bodies into a scene, the same way for the game,
    /// the server and the probes. Colliders get their place in the stable query order in
    /// the order they are added, so every host that builds the same world in the same
    /// order gets bit-identical answers.</summary>
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
            NetProtocol.AddAssets(content);
            Library.AddRoot(MapPackage.UserFolder());
        }

        /// <summary>The custom map packages this builder finds by id.</summary>
        public MapLibrary Library { get; } = new MapLibrary();

        /// <summary>The loaded custom map's package, null for a built-in map or none.</summary>
        public MapPackage Package { get; private set; }

        /// <summary>The loaded map's ground triangles (terrain or collision.bin), null without; read only.
        /// The client's chain view casts its rays against them (ChainGround).</summary>
        public MapCollision Collision { get; private set; }

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

        public bool HasMap(string map)
            => MapPackage.IsId(map) ? Library.Find(map) != null
             : !string.IsNullOrEmpty(map) && map.IndexOf(':') < 0 && content.Exists(Maps.CollisionUrl(map));

        /// <summary>Adds every collider of the map's arena prefab, then of its generated
        /// terrain if it has one, in prefab order, and gives each its place in the stable
        /// query order. Once per map: another one needs <see cref="UnloadArena"/> first.</summary>
        public void LoadArena(string map)
        {
            if (Map != null)
                throw new InvalidOperationException($"Map {Map} is already loaded.");
            if (!HasMap(map))
                throw new ArgumentException($"Unknown map: {map}");
            Map = map;
            if (MapPackage.IsId(map))
            {
                LoadPackage(Library.Find(map));
                return;
            }
            foreach (Entity e in content.Load<Prefab>(Maps.CollisionUrl(map)).Instantiate())
            {
                AddStatic(e);
                arena.Add(e);
                Zone ??= e.Get<CaptureZone>();
                Bots ??= e.Get<BotSettings>();
            }
            AddTerrain(map);
        }

        /// <summary>Takes the map's colliders out again, with no body left in the world: the
        /// stable order starts afresh, so the next map loads as in a new process.</summary>
        public void UnloadArena()
        {
            if (Map == null)
                return;
            foreach (Entity e in arena)
                if (e.Scene != null)
                    Scene.Entities.Remove(e);
            arena.Clear();
            if (Package == null)
                content.Unload(Maps.CollisionUrl(Map));
            Package = null;
            Collision = null;
            queries.Clear();
            Map = null;
            Zone = null;
            Bots = null;
        }

        /// <summary>The generated ground (Tools/maps/Mountain.cs), if the map has one:
        /// per chunk, in file order, one static compound of its triangles, wound into the
        /// solid like the test arena's. Too many for a prefab.</summary>
        void AddTerrain(string map)
        {
            using Stream stream = typeof(WorldBuilder).Assembly.GetManifestResourceStream(Maps.TerrainResource(map));
            if (stream != null)
                AddCollision(MapCollision.Read(stream));
        }

        /// <summary>Per chunk, in file order, one static compound of its triangles.</summary>
        void AddCollision(MapCollision collision)
        {
            Collision = collision;
            float[] v = collision.Vertices;
            foreach (MapCollision.Chunk chunk in collision.Chunks)
            {
                var compound = new CompoundCollider();
                int[] t = chunk.Triangles;
                for (int i = 0; i < t.Length; i += 3)
                    compound.Colliders.Add(new TriangleCollider
                    {
                        A = new Vector3(v[3 * t[i]], v[3 * t[i] + 1], v[3 * t[i] + 2]),
                        B = new Vector3(v[3 * t[i + 1]], v[3 * t[i + 1] + 1], v[3 * t[i + 1] + 2]),
                        C = new Vector3(v[3 * t[i + 2]], v[3 * t[i + 2] + 1], v[3 * t[i + 2] + 2]),
                    });
                var entity = new Entity($"Terrain_{chunk.X}_{chunk.Z}") { new StaticComponent { Collider = compound } };
                AddStatic(entity);
                arena.Add(entity);
            }
        }

        /// <summary>A custom map built in code as its prefab would be: zone, spawn markers under the
        /// built-in names (PlayerSpawn, PlayerSpawn0..11, DummySpawn), bot settings, collision.</summary>
        void LoadPackage(MapPackage package)
        {
            Package = package;
            MapManifest m = package.Manifest;
            MapZone z = m.Zone;
            var zoneAt = new Vector3(z.X, z.Y, z.Z);
            Zone = new CaptureZone
            {
                Size = new Vector3(z.SizeX, z.SizeY, z.SizeZ),
                WarmupSeconds = z.CaptureSeconds,
                WinPoints = z.WinPoints,
                TimeLimitMinutes = z.TimeLimitMinutes,
                WinPauseSeconds = z.WinPauseSeconds,
                CountdownSeconds = z.CountdownSeconds,
                JoinSeconds = z.JoinSeconds,
                RespawnBelow = z.RespawnBelow,
                SpawnRingInner = z.RingInner,
                SpawnRingOuter = z.RingOuter,
                SpawnSpacing = z.RingSpacing,
                Shape = z.Shape == "cylinder" ? ZoneShape.Cylinder : z.Shape == "polygon" ? ZoneShape.Polygon : ZoneShape.Box,
            };
            for (int i = 0; i + 1 < z.Corners.Length; i += 2)
                Zone.Corners.Add(new Vector2(z.Corners[i], z.Corners[i + 1]));
            Mark(new Entity("CaptureZone") { Zone }, zoneAt, Quaternion.Identity);

            Mark(new Entity("PlayerSpawn"), Feet(m.Spawns[0]), Facing(m.Spawns[0], zoneAt));
            MapSpawn d = m.Dummies;
            if (d == null)
            {
                Quaternion f = Facing(m.Spawns[0], zoneAt);
                Vector3 ahead = UMath.Mul(UMath.Rotate(f, new Vector3(0f, 0f, -1f)), 5f);
                Mark(new Entity("DummySpawn"), UMath.Add(Feet(m.Spawns[0]), ahead), Quaternion.Identity);
            }
            else
                Mark(new Entity("DummySpawn"), Feet(d), Facing(d, zoneAt));
            // Twelve slots over the spawns in turn; a spawn used again puts the next one 1.5 m
            // aside, alternating right and left of its facing.
            for (int slot = 0; slot < NetWorld.MaxPlayers; slot++)
            {
                MapSpawn s = m.Spawns[slot % m.Spawns.Count];
                int again = slot / m.Spawns.Count;
                Quaternion f = Facing(s, zoneAt);
                float side = again == 0 ? 0f : ((again + 1) / 2) * (again % 2 == 1 ? 1.5f : -1.5f);
                Vector3 at = UMath.Add(Feet(s), UMath.Mul(UMath.Rotate(f, UMath.Right), side));
                Mark(new Entity("PlayerSpawn" + slot), at, f);
            }

            Bots = new BotSettings();
            foreach (KeyValuePair<string, double> kv in m.Bots)
                Bots.Set(kv.Key, kv.Value);   // unknown names: from a newer kit, ignored
            Mark(new Entity("Bots") { Bots }, Vector3.Zero, Quaternion.Identity);

            using Stream file = File.OpenRead(package.CollisionPath);
            AddCollision(MapCollision.Read(file));
        }

        // A spawn stands on the ground; the body's centre is half a body above its feet.
        const float SpawnLift = 0.95f;

        static Vector3 Feet(MapSpawn s) => new Vector3(s.X, s.Y + SpawnLift, s.Z);

        /// <summary>The spawn's yaw, or towards the zone; Stride's yaw looks along (-sin, -cos).</summary>
        static Quaternion Facing(MapSpawn s, Vector3 zone)
        {
            float yaw = s.Yaw.HasValue ? s.Yaw.Value * (MathF.PI / 180f)
                      : UMath.Atan2(-(zone.X - s.X), -(zone.Z - s.Z));
            return new Quaternion(0f, DetMath.SinF(yaw * 0.5f), 0f, DetMath.CosF(yaw * 0.5f));
        }

        void Mark(Entity e, Vector3 at, Quaternion facing)
        {
            e.Transform.Position = at;
            e.Transform.Rotation = facing;
            AddStatic(e);
            arena.Add(e);
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
