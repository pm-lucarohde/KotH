using System.Collections.Generic;
using KotH.Client.Hud;
using KotH.Client.Match;
using KotH.Client.Net;
using KotH.Client.Player;
using KotH.Shared.Combat;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Mathematics;
using KotH.Shared.Net;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Animations;
using Stride.BepuPhysics;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

namespace KotH.Client
{
    /// <summary>
    /// Builds the playable world when the scene starts: the arena's collision,
    /// the player and three dummies charged to 0, 50 and 100 percent, the
    /// player and the middle dummy in the order the server and the probes
    /// use. Started with --host or --join, the dummies come first and the
    /// players arrive through the network session, see ClientNetWorld.
    /// The scene itself only carries what is drawn.
    /// </summary>
    [DataContract("KotH.GameRoot")]
    public sealed class GameRoot : StartupScript, INetWorld
    {
        [Display("Körpermodell", "Darstellung")] public Model BodyModel;
        [Display("Schrift der Schadensanzeige", "Darstellung")] public SpriteFont LabelFont;

        /// <userdoc>Atmen und leichtes Wiegen, solange eine Figur still steht.</userdoc>
        [Display("Ruhe", "Animation")] public AnimationClip IdleAnimation;

        /// <userdoc>Grundhaltung, wenn die Figur sich bewegt (bis es Laufanimationen gibt).</userdoc>
        [Display("Grundhaltung", "Animation")] public AnimationClip RestAnimation;

        /// <summary>Render group of the own figure, see PlayerCamera.</summary>
        const RenderGroup OwnFigureGroup = RenderGroup.Group1;

        public override void Start()
        {
            BepuSimulation physics = PhysicsWorld();
            var queries = new WorldQueries(physics);
            var system = new SimulationSystem(queries);
            system.Activate();

            // Before any body joins: their processor looks the system up.
            Services.AddService(system);

            builder = new WorldBuilder(Entity.Scene, Content, queries);
            builder.LoadArena();
            if (builder.Zone != null)
                system.Match = new HillMatch(builder.Zone);
            simulation = system;
            visuals = Entity.Get<PlayerVisualSystem>();

            LaunchOptions options = LaunchOptions.Current;
            if (options.Networked)
            {
                StartNetworked(options);
                return;
            }

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

            // The own figure has a render group of its own: the eyes sit in
            // its cap, so the camera skips it unless it stands back far
            // enough. Shadow views keep their own mask, the shadow stays.
            AddVisual(player).Get<ModelComponent>().RenderGroup = OwnFigureGroup;
            foreach (Entity dummy in dummies)
            {
                AddVisual(dummy);
                AddLabel(dummy, null);
            }
            BindLocal(player, reader);
        }

        void StartNetworked(LaunchOptions options)
        {
            BindWorld(null);
            foreach (Entity dummy in NetWorld.SpawnDummies(builder))
            {
                AddVisual(dummy);
                AddLabel(dummy, null);
            }

            // The local player's input waits for its body; until then the session samples nothing.
            net = new NetClient(options, this, () => localReader?.ReadInput() ?? default, simulation.Match);
            Services.AddService(net);
            Log.Info($"Netzwerk: {(options.Host ? "Host" : "Gast")} als {options.Name}, Build {NetProtocol.BuildText}");
        }

        public override void Cancel()
        {
            net?.Dispose();
        }

        /// <summary>The camera, the HUD and the input on the local player's body.</summary>
        void BindLocal(Entity player, PlayerInputReader reader)
        {
            localReader = reader;
            Entity.Get<SimulationDriver>().LocalInput = reader;
            BindWorld(reader);

            foreach (Entity e in Entity.Scene.Entities)
            {
                var camera = e.Get<PlayerCamera>();
                if (camera == null)
                    continue;
                camera.Motor = player.Get<PlayerMotor>();
                camera.Reader = reader;
                camera.Melee = player.Get<MeleeAttack>();
                camera.World = simulation.World;
                camera.OwnFigureGroup = OwnFigureGroup;
            }
        }

        /// <summary>Hands the round, the zone and the own body (null for none yet) to their displays.</summary>
        void BindWorld(SimulationBody self)
        {
            HillBoard board = null;
            foreach (Entity e in Entity.Scene.Entities)
                if (e.Get<HillBoard>() is { } found)
                {
                    board = found;
                    board.Match = simulation.Match;
                    board.Simulation = simulation;
                    board.Self = self;
                    if (net != null)
                    {
                        NetClient client = net;
                        board.Notice = () => client.Notice;
                        board.ConfirmedRound = () => client.Session.TryConfirmedMatch(out MatchState m) ? m : null;
                    }
                }
            foreach (Entity e in Entity.Scene.Entities)
                if (e.Get<ZoneVisual>() is { } zone)
                {
                    zone.Zone = builder.Zone;
                    zone.Match = simulation.Match;
                    zone.Self = self;
                    zone.Board = board;
                }
            foreach (Entity e in Entity.Scene.Entities)
                if (e.Get<PerformanceOverlay>() is { } overlay && net != null)
                {
                    NetClient client = net;
                    overlay.Network = () => client.Readout;
                }
        }

        TransformComponent View()
        {
            foreach (Entity e in Entity.Scene.Entities)
                if (e.Get<PlayerCamera>() != null)
                    return e.Transform;
            return null;
        }

        SimulationSystem INetWorld.Simulation => simulation;

        /// <summary>
        /// A player the session announces: the own one with the keyboard and
        /// the camera, everyone else steered by the network and named above
        /// the head. At its slot, already in the tick; the session parks it.
        /// </summary>
        SimulationBody INetWorld.SpawnPlayer(int netId, int slot, string name)
        {
            bool own = net != null && netId == net.Session.LocalNetId;
            SimulationBody body = own ? new PlayerInputReader() : new NetBody();
            body.NetId = netId;
            Entity marker = NetWorld.SlotMarker(builder, slot);
            Entity e = builder.SpawnBody(WorldBuilder.PlayerBodyUrl, marker.Transform.Position,
                                         marker.Transform.Rotation, body, new DamagePercent());
            e.Name = name;

            Entity visual = AddVisual(e);
            Entity label = null;
            if (own)
            {
                visual.Get<ModelComponent>().RenderGroup = OwnFigureGroup;
                BindLocal(e, (PlayerInputReader)body);
            }
            else
                label = AddLabel(e, name);
            shown[body] = (visual, label);
            return body;
        }

        void INetWorld.Despawn(SimulationBody body)
        {
            simulation.Unregister(body);
            if (shown.Remove(body, out (Entity Visual, Entity Label) drawn))
            {
                visuals.Unregister(drawn.Visual);
                Entity.Scene.Entities.Remove(drawn.Visual);
                if (drawn.Label != null)
                    Entity.Scene.Entities.Remove(drawn.Label);
            }
            Entity.Scene.Entities.Remove(body.Entity);
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

        Entity AddLabel(Entity body, string name)
        {
            var label = new DamageLabel
            {
                Font = LabelFont,
                Target = body.Get<DamagePercent>(),
                Motor = body.Get<PlayerMotor>(),
                Camera = View(),
                Name = name,
            };
            var entity = new Entity(body.Name + "Label") { label };
            Entity.Scene.Entities.Add(entity);
            return entity;
        }

        Entity AddVisual(Entity body)
        {
            var visual = new Entity(body.Name + "Visual") { new ModelComponent(BodyModel) };
            if (IdleAnimation != null && RestAnimation != null)
            {
                var animation = new AnimationComponent();
                animation.Animations[PlayerVisualSystem.IdleClip] = IdleAnimation;
                animation.Animations[PlayerVisualSystem.RestClip] = RestAnimation;
                visual.Add(animation);
            }
            Entity.Scene.Entities.Add(visual);
            visuals.Register(visual, body.Get<PlayerMotor>());
            return visual;
        }

        WorldBuilder builder;
        SimulationSystem simulation;
        PlayerVisualSystem visuals;
        NetClient net;
        PlayerInputReader localReader;
        readonly Dictionary<SimulationBody, (Entity Visual, Entity Label)> shown =
            new Dictionary<SimulationBody, (Entity, Entity)>();
    }
}
