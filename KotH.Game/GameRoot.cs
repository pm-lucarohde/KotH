using KotH.Client.Hud;
using KotH.Client.Player;
using KotH.Shared.Combat;
using KotH.Shared.Hosting;
using KotH.Shared.Mathematics;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.BepuPhysics;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace KotH.Client
{
    /// <summary>
    /// Builds the playable world when the scene starts: the arena's collision,
    /// the player and three dummies charged to 0, 50 and 100 percent, the
    /// player and the middle dummy in the order the server and the probes
    /// use. The scene itself only carries what is drawn.
    /// </summary>
    [DataContract("KotH.GameRoot")]
    public sealed class GameRoot : StartupScript
    {
        [Display("Körpermodell", "Darstellung")] public Model BodyModel;
        [Display("Schrift der Schadensanzeige", "Darstellung")] public SpriteFont LabelFont;

        /// <summary>Render group of the own figure, drawn into shadow maps only.</summary>
        const RenderGroup OwnFigureGroup = RenderGroup.Group1;

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

            // Side by side across the dummy marker's facing, 3 m apart, so
            // each percent's knockback can be tried right next to the others.
            Entity marker = builder.Find("DummySpawn");
            Vector3 side = UMath.Mul(UMath.Rotate(marker.Transform.Rotation, UMath.Right), 3f);
            var dummies = new[]
            {
                SpawnDummy(builder, marker, Vector3.Zero, 0f),
                SpawnDummy(builder, marker, UMath.Neg(side), 50f),
                SpawnDummy(builder, marker, side, 100f),
            };

            // The own figure only casts its shadow: the eyes sit inside its
            // cap, so the camera skips its render group. Shadow views keep
            // their own mask, which takes every group.
            var visuals = Entity.Get<PlayerVisualSystem>();
            AddVisual(visuals, player).Get<ModelComponent>().RenderGroup = OwnFigureGroup;
            if (SceneSystem.GraphicsCompositor?.Game is SceneCameraRenderer cameraRenderer)
                cameraRenderer.RenderMask &= ~(RenderGroupMask)(1u << (int)OwnFigureGroup);
            foreach (Entity dummy in dummies)
                AddVisual(visuals, dummy);

            Entity.Get<SimulationDriver>().LocalInput = reader;

            TransformComponent view = null;
            foreach (Entity e in Entity.Scene.Entities)
            {
                var camera = e.Get<FirstPersonCamera>();
                if (camera == null)
                    continue;
                camera.Motor = player.Get<PlayerMotor>();
                camera.Reader = reader;
                view = e.Transform;
            }

            foreach (Entity dummy in dummies)
                AddLabel(dummy, view);
        }

        static Entity SpawnDummy(WorldBuilder builder, Entity marker, Vector3 offset, float percent)
        {
            Entity body = builder.SpawnBody(WorldBuilder.DummyBodyUrl,
                UMath.Add(marker.Transform.Position, offset), marker.Transform.Rotation);
            body.Name = $"Dummy {percent:F0} %";
            body.Get<DamagePercent>().Restore(percent);
            return body;
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

        void AddLabel(Entity body, TransformComponent camera)
        {
            var label = new DamageLabel
            {
                Font = LabelFont,
                Target = body.Get<DamagePercent>(),
                Motor = body.Get<PlayerMotor>(),
                Camera = camera,
            };
            Entity.Scene.Entities.Add(new Entity(body.Name + "Label") { label });
        }

        Entity AddVisual(PlayerVisualSystem system, Entity body)
        {
            var visual = new Entity(body.Name + "Visual") { new ModelComponent(BodyModel) };
            Entity.Scene.Entities.Add(visual);
            system.Register(visual, body.Get<PlayerMotor>());
            return visual;
        }
    }
}
