using System;
using System.Collections.Generic;
using KotH.Client.Audio;
using KotH.Client.Hud;
using KotH.Client.Match;
using KotH.Client.Net;
using KotH.Client.Player;
using KotH.Shared.Bots;
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
    /// Builds the playable world when the scene starts: the map (--map, see
    /// Maps) with its collision and its visuals, the player and the bots
    /// (--bots, three by default) that play for the hill. Started with --host
    /// or --join, the players arrive through the network session, the host's
    /// bots among them; see BotDirector. A joiner builds the host's map once
    /// it is welcomed. The scene itself only carries what every map shares.
    /// </summary>
    [DataContract("KotH.GameRoot")]
    public sealed class GameRoot : StartupScript, INetWorld
    {
        [Display("Körpermodell", "Darstellung")] public Model BodyModel;
        [Display("Schrift der Schadensanzeige", "Darstellung")] public SpriteFont LabelFont;

        /// <userdoc>Atmen und leichtes Wiegen, solange eine Figur still steht.</userdoc>
        [Display("Ruhe", "Animation")] public AnimationClip IdleAnimation;

        /// <userdoc>Grundhaltung in der Luft und beim Rutschen am Steilhang.</userdoc>
        [Display("Grundhaltung", "Animation")] public AnimationClip RestAnimation;

        [Display("Gehen vorwärts", "Animation")] public AnimationClip WalkForward;
        [Display("Gehen rückwärts", "Animation")] public AnimationClip WalkBack;
        [Display("Gehen links", "Animation")] public AnimationClip WalkLeft;
        [Display("Gehen rechts", "Animation")] public AnimationClip WalkRight;
        [Display("Rennen vorwärts", "Animation")] public AnimationClip RunForward;
        [Display("Rennen rückwärts", "Animation")] public AnimationClip RunBack;
        [Display("Rennen links", "Animation")] public AnimationClip RunLeft;
        [Display("Rennen rechts", "Animation")] public AnimationClip RunRight;

        /// <userdoc>Hocke mit Atmen, geduckt im Stand und in der Luft.</userdoc>
        [Display("Geduckt", "Animation")] public AnimationClip CrouchAnimation;

        /// <userdoc>Schleichschritte geduckt, ein Clip für alle Richtungen.</userdoc>
        [Display("Geduckt gehen", "Animation")] public AnimationClip CrouchWalk;

        /// <userdoc>Gleiten aus dem Sprint: ein Bein flach nach vorn gestreckt.</userdoc>
        [Display("Gleiten", "Animation")] public AnimationClip SlideAnimation;

        /// <userdoc>Haltung beim Fallen: Arme schräg zur Seite und nach vorn, leichtes Balancieren.</userdoc>
        [Display("Fallen", "Animation")] public AnimationClip FallAnimation;

        /// <userdoc>Tiefster Punkt beim Einfedern nach der Landung; das Spiel blendet ihn kurz ein.</userdoc>
        [Display("Landen", "Animation")] public AnimationClip LandAnimation;

        /// <userdoc>Haltung beim Aufsteigen nach dem Absprung (nach Sanji): ein Bein gestreckt, das andere Knie hoch.</userdoc>
        [Display("Springen", "Animation")] public AnimationClip JumpAnimation;

        /// <userdoc>Tritt in die Luft beim Doppelsprung (Sanjis Sky Walk); das Spiel blendet ihn kurz ein.</userdoc>
        [Display("Doppelsprung", "Animation")] public AnimationClip SkyWalkAnimation;

        /// <userdoc>Nur die Arme der Pilzfigur; in First Person hängen sie vor der Kamera.</userdoc>
        [Display("Arme in First Person", "Darstellung")] public Model ArmsModel;

        /// <userdoc>Arme in First Person im Stehen und in der Luft: ruhiges Atmen.</userdoc>
        [Display("Arme Ruhe", "First-Person-Arme")] public AnimationClip ArmsIdle;

        /// <userdoc>Arme in First Person beim Gehen, im Takt der Schritte.</userdoc>
        [Display("Arme Gehen", "First-Person-Arme")] public AnimationClip ArmsWalk;

        /// <userdoc>Arme in First Person beim Rennen, im Takt der Schritte.</userdoc>
        [Display("Arme Rennen", "First-Person-Arme")] public AnimationClip ArmsRun;

        /// <userdoc>Was jede Figur in der rechten Hand hält (der Bambusstab), auch an den Armen in First Person. In der Ruhelage der Figur modelliert; ein Knochen „Leaf“ weht wie die Blätter der Figur.</userdoc>
        [Display("Gehaltenes Objekt", "Darstellung")] public Model HeldModel;

        /// <userdoc>Knochen der Figur, dem das gehaltene Objekt folgt: Grip.R ist die Mitte der rechten Faust; in First Person dreht ihn der Arm-Clip, damit der Stab nach rechts oben zeigt.</userdoc>
        [Display("Knochen des Objekts", "Darstellung")] public string HeldBone = "Grip.R";

        /// <summary>Render group of the own figure, see PlayerCamera.</summary>
        const RenderGroup OwnFigureGroup = RenderGroup.Group1;

        /// <summary>Render group of the first-person arms, see PlayerCamera.</summary>
        const RenderGroup ArmsGroup = RenderGroup.Group2;

        // The eye of the arms model over its origin (.work/rig/fp_arms.py): the
        // arms hang this far under the camera.
        const float ArmsEye = 0.78f;

        public override void Start()
        {
            BepuSimulation physics = PhysicsWorld();
            var queries = new WorldQueries(physics);
            var system = new SimulationSystem(queries);
            system.Activate();

            // Before any body joins: their processor looks the system up.
            Services.AddService(system);

            builder = new WorldBuilder(Entity.Scene, Content, queries);
            simulation = system;
            visuals = Entity.Get<PlayerVisualSystem>();

            LaunchOptions options = LaunchOptions.Current;
            CoverWhileLoading();
            if (!options.Joining)
            {
                string map = options.Map;
                if (!builder.HasMap(map))
                {
                    Log.Error($"Unbekannte Map {map}, lade {Maps.Default}");
                    map = Maps.Default;
                }
                LoadMap(map);
            }
            if (options.Host)
            {
                // The game opens once the host has loaded.
                cover.Loaded += () => StartNetworked(options);
                return;
            }
            if (options.Networked)
            {
                // A guest asks for the map, loads it and only then joins.
                StartNetworked(options);
                cover.Loaded += () => net.Session.Join(net.Now);
                cover.Hold = () => !net.Session.Joined;
                return;
            }

            var reader = new PlayerInputReader { NetId = NetWorld.FirstPlayerId };
            Entity player = Spawn(builder, WorldBuilder.PlayerBodyUrl, "PlayerSpawn", "Player", reader, new DamagePercent());

            // The bots on the player slots, numbered on from the player like
            // joiners, so they think exactly as they would on a host.
            var states = new LocalFinalStates(simulation);
            BotDirector director = options.Bots > 0 ? BotDirector.Create(builder, simulation, states, options.BotSeed) : null;
            WorkAhead(director);
            var bots = new List<Entity>();
            for (int i = 0; i < options.Bots; i++)
            {
                var bot = new BotBody(director, states, NetWorld.FirstPlayerId + 1 + i);
                Entity marker = NetWorld.SlotMarker(builder, NetWorld.BotSlots[i]);
                Entity e = builder.SpawnBody(WorldBuilder.PlayerBodyUrl, marker.Transform.Position, marker.Transform.Rotation,
                                             bot, new DamagePercent());
                e.Name = $"Bot {i + 1}";
                bots.Add(e);
            }

            // The own figure has a render group of its own: the eyes sit in
            // its cap, so the camera skips it unless it stands back far
            // enough. Shadow views keep their own mask, the shadow stays.
            AddVisual(player, OwnFigureGroup);
            foreach (Entity bot in bots)
            {
                AddVisual(bot);
                AddLabel(bot, bot.Name);
            }
            BindLocal(player, reader);
        }

        void StartNetworked(LaunchOptions options)
        {
            BindWorld(null);

            // The local player's input waits for its body; until then the session samples nothing.
            net = new NetClient(options, this, () => localReader?.ReadInput() ?? default, simulation.Match);
            Services.AddService(net);
            if (options.Host && options.Bots > 0)
            {
                BotDirector director = BotDirector.Create(builder, simulation, net.Session, options.BotSeed);
                net.AddBots(director, options.Bots);
                WorkAhead(director);
                Log.Info($"Netzwerk: {options.Bots} Bots, Seed {options.BotSeed}");
            }
            Log.Info($"Netzwerk: {(options.Host ? "Host" : "Gast")} als {options.Name}, Build {NetProtocol.BuildText}");
        }

        /// <summary>The map's collision, its round and what is drawn of it; once per game.</summary>
        void LoadMap(string map)
        {
            if (builder.Map == map)
                return;
            cover?.Begin();
            builder.LoadArena(map);
            if (builder.Zone != null)
                simulation.Match = new HillMatch(builder.Zone) { Seed = (uint)Random.Shared.Next() };  // a guest takes the host's
            string url = Maps.VisualUrl(map);
            if (Content.Exists(url))
                foreach (Entity e in Content.Load<Prefab>(url).Instantiate())
                    Entity.Scene.Entities.Add(e);
            else
                Log.Warning($"Map {map} hat keine Optik ({url})");
            Log.Info($"Map {map} geladen");
        }

        string INetWorld.Map => builder.Map;

        // Before the state: the own body arrives with it and binds the displays.
        void INetWorld.LoadMap(string map) => LoadMap(map);

        public override void Cancel()
        {
            net?.Dispose();
            botDirector?.Dispose();
        }

        /// <summary>
        /// Black until the map's lighting is baked: the first seconds stutter
        /// (shaders, the bake). Before a map it says what the connection says.
        /// </summary>
        void CoverWhileLoading()
        {
            HudRenderer hud = HudRenderer.Of(this);
            cover = new LoadScreen(hud) { Text = () => net?.Notice };
            foreach (Entity e in Entity.Scene.Entities)
                if (e.Get<HillBoard>() is { } board)
                    cover.Font = board.Font;
            hud.Cover = cover;
            Services.AddService(cover);
        }

        /// <summary>
        /// The bots' inputs worked out ahead of their ticks, off the frame: on
        /// a thread of their own, with KOTH_BOT_THREAD=0 a little each frame
        /// instead. SimulationDriver pumps the director it finds here.
        /// </summary>
        void WorkAhead(BotDirector director)
        {
            if (director == null)
                return;
            botDirector = director;
            Services.AddService(director);
            if (Environment.GetEnvironmentVariable("KOTH_BOT_THREAD") != "0")
                director.StartWorker();
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
                camera.ArmsGroup = ArmsGroup;
                AddArms(e, player);
            }
        }

        /// <summary>
        /// The own arms as a child of the camera, so they turn and shake with
        /// the view; PlayerVisualSystem plays them in step with the figure.
        /// </summary>
        void AddArms(Entity camera, Entity player)
        {
            if (ArmsModel == null)
                return;
            if (arms != null)
            {
                visuals.UnregisterArms();
                arms.Transform.Parent = null;
            }
            ArmsInFront.Install(SceneSystem, ArmsGroup);
            var model = new ModelComponent(ArmsModel) { RenderGroup = ArmsGroup, IsShadowCaster = false };
            var animation = new AnimationComponent();
            AnimationClip[] clips = { ArmsIdle, ArmsWalk, ArmsRun };
            for (int i = 0; i < clips.Length; i++)
                if (clips[i] != null)
                    animation.Animations[PlayerVisualSystem.ArmClips[i]] = clips[i];
            arms = new Entity("Arms") { model, animation };
            arms.Transform.Position = new Vector3(0f, -ArmsEye, 0f);
            camera.AddChild(arms);
            AttachHeld(arms, ArmsGroup, false);
            visuals.RegisterArms(arms, player.Get<PlayerMotor>());
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
                if (e.Get<MusicPlayer>() is { } music)
                {
                    music.Match = simulation.Match;
                    // Bound before the client exists too: read it late, so connecting counts as the lobby.
                    if (LaunchOptions.Current.Networked)
                        music.ConfirmedRound = () => net != null && net.Session.TryConfirmedMatch(out MatchState m) ? m : null;
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

            Entity visual = own ? AddVisual(e, OwnFigureGroup) : AddVisual(e);
            Entity label = null;
            if (own)
            {
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

        Entity AddVisual(Entity body, RenderGroup group = RenderGroup.Group0)
        {
            var visual = new Entity(body.Name + "Visual") { new ModelComponent(BodyModel) { RenderGroup = group } };
            // In the order of PlayerVisualSystem.Clips; a missing clip just never plays.
            AnimationClip[] clips = { IdleAnimation, RestAnimation, WalkForward, WalkBack, WalkLeft, WalkRight,
                                      RunForward, RunBack, RunLeft, RunRight,
                                      CrouchAnimation, CrouchWalk, SlideAnimation, FallAnimation, LandAnimation,
                                      JumpAnimation, SkyWalkAnimation };
            if (RestAnimation != null)
            {
                var animation = new AnimationComponent();
                for (int i = 0; i < clips.Length; i++)
                    if (clips[i] != null)
                        animation.Animations[PlayerVisualSystem.Clips[i]] = clips[i];
                visual.Add(animation);
            }
            AttachHeld(visual, group, true);
            Entity.Scene.Entities.Add(visual);
            visuals.Register(visual, body.Get<PlayerMotor>());
            return visual;
        }

        /// <summary>
        /// Hangs HeldModel on HeldBone of figure's model, as a child so it
        /// goes with it. The object is modelled in the figure's rest pose
        /// (.work/rig/bamboo.py): its local transform undoes the bone's rest,
        /// so at rest it sits as modelled and then follows the bone.
        /// </summary>
        void AttachHeld(Entity figure, RenderGroup group, bool shadow)
        {
            var target = figure.Get<ModelComponent>();
            Skeleton skeleton = target?.Model?.Skeleton;
            int bone = skeleton == null ? -1 : Array.FindIndex(skeleton.Nodes, n => n.Name == HeldBone);
            if (HeldModel == null || bone < 0)
                return;
            // The bone's rest in model space, child first (row vectors).
            Matrix rest = Matrix.Identity;
            for (int i = bone; i >= 0; i = skeleton.Nodes[i].ParentIndex)
            {
                TransformTRS node = skeleton.Nodes[i].Transform;
                rest *= Matrix.Scaling(node.Scale) * Matrix.RotationQuaternion(node.Rotation) * Matrix.Translation(node.Position);
            }
            rest.Invert();
            var held = new Entity("Held")
            {
                new ModelComponent(HeldModel) { RenderGroup = group, IsShadowCaster = shadow },
                new ModelNodeLinkComponent { Target = target, NodeName = HeldBone },
            };
            rest.Decompose(out Vector3 scale, out Quaternion rotation, out Vector3 position);
            held.Transform.Position = position;
            held.Transform.Rotation = rotation;
            held.Transform.Scale = scale;
            figure.AddChild(held);
        }

        WorldBuilder builder;
        SimulationSystem simulation;
        PlayerVisualSystem visuals;
        NetClient net;
        BotDirector botDirector;
        LoadScreen cover;
        PlayerInputReader localReader;
        Entity arms;
        readonly Dictionary<SimulationBody, (Entity Visual, Entity Label)> shown =
            new Dictionary<SimulationBody, (Entity, Entity)>();
    }
}
