using System;
using System.Collections.Generic;
using KotH.Client.Audio;
using KotH.Client.Hud;
using KotH.Client.Match;
using KotH.Client.Menu;
using KotH.Client.Net;
using KotH.Client.Player;
using KotH.Client.Rendering;
using KotH.Client.Settings;
using KotH.Client.Steam;
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
using Stride.Rendering.Lights;

namespace KotH.Client
{
    /// <summary>Builds the playable world: the map (--map, see Maps) with collision and visuals,
    /// the player and the bots (--bots, 3 by default). With --host or --join the players arrive
    /// through the network session, the host's bots among them (BotDirector); a joiner builds the
    /// host's map once welcomed. The scene itself only carries what every map shares.</summary>
    [DataContract("KotH.GameRoot")]
    public sealed class GameRoot : StartupScript, INetWorld
    {
        [Display("Body model", "Visuals")] public Model BodyModel;
        [Display("Damage label font", "Visuals")] public SpriteFont LabelFont;

        /// <userdoc>Breathing and slight swaying while a figure stands still.</userdoc>
        [Display("Idle", "Animation")] public AnimationClip IdleAnimation;

        /// <userdoc>Base pose in the air and when sliding down a steep slope.</userdoc>
        [Display("Rest", "Animation")] public AnimationClip RestAnimation;

        [Display("Walk forward", "Animation")] public AnimationClip WalkForward;
        [Display("Walk back", "Animation")] public AnimationClip WalkBack;
        [Display("Walk left", "Animation")] public AnimationClip WalkLeft;
        [Display("Walk right", "Animation")] public AnimationClip WalkRight;
        [Display("Run forward", "Animation")] public AnimationClip RunForward;
        [Display("Run back", "Animation")] public AnimationClip RunBack;
        [Display("Run left", "Animation")] public AnimationClip RunLeft;
        [Display("Run right", "Animation")] public AnimationClip RunRight;

        /// <userdoc>Crouch with breathing, crouched standing and in the air.</userdoc>
        [Display("Crouch", "Animation")] public AnimationClip CrouchAnimation;

        /// <userdoc>Crouched sneaking steps, one clip for all directions.</userdoc>
        [Display("Crouch walk", "Animation")] public AnimationClip CrouchWalk;

        /// <userdoc>Slide out of the sprint: one leg stretched flat forward.</userdoc>
        [Display("Slide", "Animation")] public AnimationClip SlideAnimation;

        /// <userdoc>Falling pose: arms out to the side and forward, slight balancing.</userdoc>
        [Display("Fall", "Animation")] public AnimationClip FallAnimation;

        /// <userdoc>Lowest point of the landing squash; the game blends it in briefly.</userdoc>
        [Display("Land", "Animation")] public AnimationClip LandAnimation;

        /// <userdoc>Pose rising after take-off (after Sanji): one leg stretched, the other knee up.</userdoc>
        [Display("Jump", "Animation")] public AnimationClip JumpAnimation;

        /// <userdoc>Kick into the air on the double jump (Sanji's Sky Walk); the game blends it in briefly.</userdoc>
        [Display("Double jump", "Animation")] public AnimationClip SkyWalkAnimation;

        /// <userdoc>Only the mushroom figure's arms; in first person they hang in front of the camera.</userdoc>
        [Display("First person arms", "Visuals")] public Model ArmsModel;

        /// <userdoc>First person arms standing and in the air: calm breathing.</userdoc>
        [Display("Arms idle", "First person arms")] public AnimationClip ArmsIdle;

        /// <userdoc>First person arms walking, in step.</userdoc>
        [Display("Arms walk", "First person arms")] public AnimationClip ArmsWalk;

        /// <userdoc>First person arms running, in step.</userdoc>
        [Display("Arms run", "First person arms")] public AnimationClip ArmsRun;

        /// <userdoc>What each figure holds in its right hand (the bamboo cane), also on the first person arms. Modelled in the figure's rest pose; a bone "Leaf" sways like the figure's leaves.</userdoc>
        [Display("Held object", "Visuals")] public Model HeldModel;

        /// <userdoc>Bone of the figure the held object follows: Grip.R is the centre of the right fist; in first person the arm clip turns it so the cane points up and right.</userdoc>
        [Display("Held object bone", "Visuals")] public string HeldBone = "Grip.R";

        /// <userdoc>The mushroom blaster, held instead of the cane on key 2; modelled in the rest pose like the cane (.work/rig/blaster.py), bones Breech and Muzzle give its barrel.</userdoc>
        [Display("Blaster", "Visuals")] public Model BlasterModel;

        /// <userdoc>The figure in the main menu: its centre in the menu camera's frame (x right, y up, -z ahead), in metres.</userdoc>
        [Display("Figure place", "Main menu")] public Vector3 MenuFigureAt = new Vector3(0.46f, -0.82f, -2.0f);

        /// <userdoc>How far the menu figure turns away from facing the camera, degrees (positive: towards the buttons).</userdoc>
        [Display("Figure turn", "Main menu")] public float MenuFigureTurn = 15f;

        /// <userdoc>Where the menu figure stands while the Customize page is open, right of it, in the camera's frame.</userdoc>
        [Display("Figure place customizing", "Main menu")] public Vector3 MenuFigureCustomizeAt = new Vector3(0.95f, -0.82f, -2.0f);

        /// <userdoc>The menu figure's own light, a point light only it is close enough to: place in the figure's frame (x right, -z its front), in metres.</userdoc>
        [Display("Light place", "Main menu")] public Vector3 MenuLightAt = new Vector3(-0.8f, 1.2f, -1.6f);

        /// <userdoc>Strength of the menu figure's light.</userdoc>
        [Display("Light strength", "Main menu")] public float MenuLightIntensity = 6f;

        /// <summary>Render group of the own figure, see PlayerCamera.</summary>
        const RenderGroup OwnFigureGroup = RenderGroup.Group1;

        /// <summary>Render group of the main menu's figure, its own pass in SceneThrottle.</summary>
        const RenderGroup MenuFigureGroup = RenderGroup.Group7;

        /// <summary>Render group of the first-person arms, see PlayerCamera.</summary>
        const RenderGroup ArmsGroup = RenderGroup.Group2;

        // The eye of the arms model over its origin (.work/rig/fp_arms.py): the
        // arms hang this far under the camera.
        const float ArmsEye = 0.78f;

        public override void Start()
        {
            StartLog.Mark("game root starts");
            Stride.Rendering.Voxels.VoxelStorageSparse.Report = StartLog.Mark;
            ClientSettings.Changed += ApplyTextures;
            FigureShadowMasks.Install(SceneSystem);
            OpaqueDepthRead.Install(SceneSystem);
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
            var pause = new PauseMenu { Driver = Entity.Get<SimulationDriver>(), ToMainMenu = ReturnToMenu };
            Entity.Scene.Entities.Add(new Entity("PauseMenu") { pause });
            Entity.Scene.Entities.Add(new Entity("Chat") { new ChatBox { Driver = pause.Driver, Pause = pause } });
            Entity.Scene.Entities.Add(new Entity("Voice") { new VoiceChat { Driver = pause.Driver } });
            if (!options.Joining)
            {
                // The main menu and a host's lobby are on Mount Destiny (Luca); --map is the round's map.
                string map = options.ShowsMenu || options.Host ? Maps.Default : options.Map;
                if (!builder.HasMap(map))
                {
                    Log.Error($"Unknown map {map}, loading {Maps.Default}");
                    map = Maps.Default;
                }
                LoadMap(map);
            }
            if (options.ShowsMenu)
            {
                ShowMenu();
                return;
            }
            if (options.Host)
            {
                // The game opens once the host has loaded, unless it went back to the menu first;
                // only the first map's Loaded, a round's map change fires it again.
                Action open = null;
                open = () =>
                {
                    cover.Loaded -= open;
                    if (games == 0)
                        StartNetworked(options);
                };
                cover.Loaded += open;
                return;
            }
            if (options.Networked)
            {
                // A guest asks for the map, loads it and only then joins.
                StartNetworked(options);
                NetClient guest = net;
                Action join = null;
                join = () =>
                {
                    cover.Loaded -= join;
                    if (net == guest)
                        guest.Session.Join(guest.Now);
                };
                cover.Loaded += join;
                cover.Hold = () => !guest.Joined;
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
            shown[reader] = (AddVisual(player, LaunchOptions.Current.Look, OwnFigureGroup), null);
            foreach (Entity bot in bots)
                shown[bot.Get<BotBody>()] = (AddVisual(bot, FigureLook.Default), AddLabel(bot, bot.Name));
            BindLocal(player, reader);
        }

        /// <summary>The main menu over the flight round the map, with its music; no body
        /// yet and nothing ticks. Both start behind the load screen, which stays until
        /// the menu's first frames and the flight's first views are done (no stutter in
        /// sight). Host server opens a lobby on Mount Destiny.</summary>
        void ShowMenu()
        {
            var script = new MainMenu
            {
                StartHost = HostFromMenu, StartJoin = JoinFromMenu, StartInvite = JoinByInvite, Note = menuNote,
                PlayerName = LaunchOptions.Current.Name, Cover = cover, Look = LaunchOptions.Current.Look,
                LookChanged = LookChanged,
                CustomizeShown = open => visuals.MoveShowcase(menuFigure, open ? MenuFigureCustomizeAt : MenuFigureAt),
            };
            menuNote = null;
            menu = new Entity("Menu") { script };
            Entity.Scene.Entities.Add(menu);
            cover.Hold = () => !script.Ready;
            SetMenuOpen(true);
        }

        /// <summary>Host server: as if started with --host, the lobby on Mount Destiny (behind the menu
        /// already), through a listed Steam lobby with the page's flags when Steam is there (direct IP
        /// otherwise); a private round takes only who knows the password.</summary>
        void HostFromMenu(LobbyFlags flags, string password)
        {
            if (!LeaveMenu())
                return;
            LaunchOptions options = LaunchOptions.Current;
            options.Host = true;
            options.Steam = SteamClient.Ready;
            options.Password = password;
            StartNetworked(options, options.Steam ? SteamLobby.Open(builder.Map, flags) : null);
        }

        /// <summary>A round from the lobby list or an invite, its lobby entered. On another map the
        /// loaded one goes; the host names its map, which loads, and the guest joins once it is baked.</summary>
        void JoinFromMenu(SteamLobby lobby, string password)
        {
            if (!LeaveMenu())
            {
                lobby.Dispose();
                return;
            }
            LaunchOptions options = LaunchOptions.Current;
            options.Host = false;
            options.Steam = true;
            options.Password = password;
            if (lobby.Map != builder.Map)
                UnloadMap();
            StartNetworked(options, lobby);
            NetClient guest = net;
            guest.JoinWhenReady(() => cover.IsLoaded);
            cover.Hold = () => !guest.Joined;
        }

        /// <summary>A direct-IP host by its invite code: encrypted, and only with the host key the code names.
        /// The host names its map, which loads, and the guest joins once it is baked.</summary>
        void JoinByInvite(string code)
        {
            if (!Invites.TryParse(code, out string address, out int port, out string key) || !LeaveMenu())
                return;
            LaunchOptions options = LaunchOptions.Current;
            options.Host = options.Steam = false;
            options.JoinAddress = address;
            options.Port = port;
            options.JoinKey = key;
            options.Password = null;
            StartNetworked(options);
            NetClient guest = net;
            guest.JoinWhenReady(() => cover.IsLoaded);
            cover.Hold = () => !guest.Joined;
        }

        /// <summary>The host restarted the game on a map and stage (lobby to round and back): the
        /// world is taken down as for the main menu, the map changes if it differs, a fresh round
        /// waits; NetClient joins again once the map is baked. Covered until joined.</summary>
        void RestartOn(string map, MatchStage stage)
        {
            DropBots();
            foreach (SimulationBody body in new List<SimulationBody>(shown.Keys))
                ((INetWorld)this).Despawn(body);
            foreach (SimulationBody body in new List<SimulationBody>(simulation.Bodies))
                ((INetWorld)this).Despawn(body);
            Unbind();
            simulation.ResetTickCounter();
            if (builder.Map != map && builder.HasMap(map))
            {
                UnloadMap();
                LoadMap(map);
            }
            if (builder.Zone != null)
            {
                // The host's own round; a guest takes the host's state when it joins.
                simulation.Match = new HillMatch(builder.Zone) { Seed = (uint)Random.Shared.Next(), Stage = stage };
                if (stage == MatchStage.Lobby)
                    simulation.Match.MinPlayers = LaunchOptions.Current.MinPlayers;
            }
            BindWorld(null);
            NetClient client = net;
            cover.Hold = () => net == client && (client.Restarting || !client.Joined);
            Log.Info($"Network: restart on {map} ({stage})");
        }

        bool LeaveMenu()
        {
            if (menu == null)
                return false;
            Entity.Scene.Entities.Remove(menu);
            menu = null;
            cover.Hold = null;
            SetMenuOpen(false);
            return true;
        }

        /// <summary>Main menu in the pause menu: the game is taken down - connection,
        /// bots, bodies and what shows them - and the main menu flies over Mount Destiny
        /// again, which stays loaded or loads anew after a round on another map.</summary>
        void ReturnToMenu()
        {
            if (menu != null)
                return;
            // A private round's door stayed shut: Find server says why.
            menuNote = net?.Session.Rejected == RejectReason.Password ? "Wrong password" : net?.KeyRefused;
            SimulationJob.Drop();
            games++;
            if (net != null)
            {
                Services.RemoveService<NetClient>();
                net.Dispose();
                net = null;
            }
            DropBots();
            foreach (SimulationBody body in new List<SimulationBody>(shown.Keys))
                ((INetWorld)this).Despawn(body);
            foreach (SimulationBody body in new List<SimulationBody>(simulation.Bodies))
                ((INetWorld)this).Despawn(body);
            Unbind();

            LaunchOptions options = LaunchOptions.Current;
            options.Host = false;
            options.JoinAddress = null;
            options.Steam = false;
            cover.Hold = null;
            simulation.ResetTickCounter();
            if (builder.Map != Maps.Default)
            {
                // The menu flies round Mount Destiny: another map goes, a guest without one loads it.
                UnloadMap();
                LoadMap(Maps.Default);
            }
            else if (builder.Zone != null)
                simulation.Match = new HillMatch(builder.Zone) { Seed = (uint)Random.Shared.Next() };
            // The game's garbage goes now, not as a stutter in the menu.
            GC.Collect();
            ShowMenu();
            Log.Info("Back in the main menu");
        }

        /// <summary>Camera, arms and displays let go of the game, as before the first one.</summary>
        void Unbind()
        {
            Entity.Get<SimulationDriver>().LocalInput = null;
            localReader = null;
            if (arms != null)
            {
                visuals.UnregisterArms();
                arms.Transform.Parent = null;
                arms = null;
            }
            foreach (Entity e in Entity.Scene.Entities)
            {
                if (e.Get<PlayerCamera>() is { } camera)
                {
                    camera.Motor = null;
                    camera.Reader = null;
                    camera.Melee = null;
                }
                if (e.Get<HillBoard>() is { } board)
                {
                    board.Match = null;
                    board.Simulation = null;
                    board.Self = null;
                    board.Notice = null;
                    board.ConfirmedRound = null;
                }
                if (e.Get<ZoneVisual>() is { } zone)
                {
                    zone.Zone = null;
                    zone.Match = null;
                    zone.Self = null;
                    zone.Board = null;
                }
                if (e.Get<MusicPlayer>() is { } music)
                {
                    music.Match = null;
                    music.ConfirmedRound = null;
                }
                if (e.Get<PerformanceOverlay>() is { } overlay)
                    overlay.Network = null;
            }
        }

        void SetMenuOpen(bool open)
        {
            Entity.Get<SimulationDriver>().InMenu = open;
            foreach (Entity e in Entity.Scene.Entities)
            {
                if (e.Get<MusicPlayer>() is { } music)
                    music.InMenu = open;
                if (e.Get<MenuCamera>() is { } flight)
                {
                    flight.Active = open;
                    ShowMenuFigure(e, open);
                }
                if (e.Get<Crosshair>() is { } crosshair)
                    crosshair.Hidden = open;
            }
        }

        void StartNetworked(LaunchOptions options, SteamLobby lobby = null)
        {
            BindWorld(null);

            // The local player's input waits for its body; until then the session samples nothing.
            net = new NetClient(options, this, () => localReader?.ReadInput() ?? default, simulation.Match, lobby)
            {
                ChangeMap = RestartOn,
                MapReady = () => cover.IsLoaded,
                MakeBots = (states, since) =>
                {
                    DropBots();
                    long at = StartLog.Now;
                    BotDirector made = BotDirector.Create(builder, simulation, states, options.BotSeed);
                    made.Since = since;
                    StartLog.Took("bot director", at);
                    WorkAhead(made);
                    return made;
                },
                DropBots = DropBots,
            };
            Services.AddService(net);
            if (options.Host && options.Bots > 0)
            {
                BotDirector director = BotDirector.Create(builder, simulation, net.Session, options.BotSeed);
                net.AddBots(director, options.Bots);
                WorkAhead(director);
                Log.Info($"Network: {options.Bots} bots, seed {options.BotSeed}");
            }
            Log.Info($"Network: {(options.Host ? "host" : "guest")} as {options.Name}{(lobby != null ? " over Steam" : "")}, build {NetProtocol.BuildText}");
        }

        /// <summary>The map's collision, its round and what is drawn of it; one at a time,
        /// another needs UnloadMap first.</summary>
        void LoadMap(string map)
        {
            if (builder.Map == map)
                return;
            long loadAt = StartLog.Now;
            cover?.Begin();
            builder.LoadArena(map);
            if (builder.Zone != null)
                simulation.Match = new HillMatch(builder.Zone) { Seed = (uint)Random.Shared.Next() };  // a guest takes the host's
            string url = Maps.VisualUrl(map);
            if (Content.Exists(url))
            {
                List<Entity> loaded = Content.Load<Prefab>(url).Instantiate();
                long cullAt = StartLog.Now;
                StartLog.Took($"map meshes sorted into {MapCulling.Split(loaded, Services, GraphicsDevice)} clusters", cullAt);
                foreach (Entity e in loaded)
                {
                    Entity.Scene.Entities.Add(e);
                    mapVisuals.Add(e);
                }
                LeafRenderFeature.Install(SceneSystem);
                MapCulling.InstallFeature(SceneSystem);
                QualityLevels.ApplyTextures(mapVisuals);
            }
            else
                Log.Warning($"Map {map} has no visuals ({url})");
            if (visuals != null)
                visuals.Surface = GroundSurface.Load(map);
            Log.Info($"Map {map} loaded");
            StartLog.Took($"map {map} loaded", loadAt);
        }

        /// <summary>Takes the loaded map down for another, with no body left: what is drawn of it
        /// (its scripts and the voxel light free their GPU memory), its colliders and its round.
        /// The load screen covers until the next map is loaded and baked.</summary>
        void UnloadMap()
        {
            string map = builder.Map;
            if (map == null)
                return;
            foreach (Entity e in mapVisuals)
                Entity.Scene.Entities.Remove(e);
            mapVisuals.Clear();
            MapCulling.Clear();
            if (Content.IsLoaded(Maps.VisualUrl(map)))
                Content.Unload(Maps.VisualUrl(map));
            builder.UnloadArena();
            simulation.Match = null;
            if (visuals != null)
                visuals.Surface = null;
            // The new map's casters go into the static sun shadow, drawn anew before its bake;
            // the old lightmap and its sun map are forgotten.
            var tags = RenderContext.GetShared(Services).Tags;
            if (tags.Get(StaticSunShadowRenderer.Key) is { } sun)
                sun.Ready = false;
            tags.Set(Stride.Rendering.Voxels.ForwardRendererVoxels.ForgetLightmapKey, true);
            cover.Restart();
            GC.Collect();
            Log.Info($"Map {map} unloaded");
        }

        // The Texture setting on the map's materials, again after every Apply.
        void ApplyTextures(PlayerSettings settings) => QualityLevels.ApplyTextures(mapVisuals);

        string INetWorld.Map => builder.Map;

        // Before the state: the own body arrives with it and binds the displays. A guest from
        // the menu on another map than the host's (only before it has joined) changes maps.
        void INetWorld.LoadMap(string map)
        {
            if (builder.Map != null && builder.Map != map)
                UnloadMap();
            LoadMap(map);
        }

        public override void Cancel()
        {
            ClientSettings.Changed -= ApplyTextures;
            SimulationJob.Drop();
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
            hud.Cover = cover;
            Services.AddService(cover);
        }

        // The bot director goes (a restart, the main menu, a host change that ends this side's hosting).
        void DropBots()
        {
            if (botDirector == null)
                return;
            Services.RemoveService<BotDirector>();
            botDirector.Dispose();
            botDirector = null;
        }

        /// <summary>The bots' inputs worked out ahead of their ticks, off the frame: on a
        /// thread of their own, with KOTH_BOT_THREAD=0 a little each frame instead.
        /// SimulationDriver pumps the director it finds here.</summary>
        void WorkAhead(BotDirector director)
        {
            DropBots();
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
            AttachHeld(arms, ArmsGroup, false, HeldModel, PlayerVisualSystem.CaneEntity);
            AttachHeld(arms, ArmsGroup, false, BlasterModel, PlayerVisualSystem.BlasterEntity);
            visuals.RegisterArms(arms, player.Get<PlayerMotor>(), LaunchOptions.Current.Look);
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

        /// <summary>A player the session announces: the own one with the keyboard and the
        /// camera, everyone else steered by the network and named above the head. At its
        /// slot, already in the tick; the session parks it.</summary>
        SimulationBody INetWorld.SpawnPlayer(int netId, int slot, string name, FigureLook look)
        {
            bool own = net != null && netId == net.Session.LocalNetId;
            SimulationBody body = own ? new PlayerInputReader() : new NetBody();
            body.NetId = netId;
            Entity marker = NetWorld.SlotMarker(builder, slot);
            Entity e = builder.SpawnBody(WorldBuilder.PlayerBodyUrl, marker.Transform.Position,
                                         marker.Transform.Rotation, body, new DamagePercent());
            e.Name = name;

            Entity visual = own ? AddVisual(e, look, OwnFigureGroup) : AddVisual(e, look);
            Entity label = null;
            if (own)
            {
                BindLocal(e, (PlayerInputReader)body);
            }
            else
            {
                label = AddLabel(e, name);
                label.Get<DamageLabel>().NetId = netId;
            }
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

        Entity AddVisual(Entity body, FigureLook look, RenderGroup group = RenderGroup.Group0)
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
            AttachHeld(visual, group, true, HeldModel, PlayerVisualSystem.CaneEntity);
            AttachHeld(visual, group, true, BlasterModel, PlayerVisualSystem.BlasterEntity);
            Entity.Scene.Entities.Add(visual);
            visuals.Register(visual, body.Get<PlayerMotor>(), look);
            return visual;
        }

        /// <summary>The figure beside the main menu, upper body and head in the free space right of the
        /// buttons: hung from the flying camera, lit by its own short point light, no shadow, drawn
        /// at the full frame rate.</summary>
        void ShowMenuFigure(Entity camera, bool open)
        {
            if (!open)
            {
                if (menuFigure != null)
                {
                    Services.GetService<SceneThrottle>()?.HideOverlay();
                    visuals.UnregisterShowcase(menuFigure);
                    camera.RemoveChild(menuFigure);
                    menuFigure = null;
                }
                return;
            }
            if (menuFigure != null || BodyModel == null)
                return;

            // Its own group, drawn every frame over the map's 30 fps picture (SceneThrottle).
            var figure = new Entity("MenuFigure")
            {
                new ModelComponent(BodyModel) { RenderGroup = MenuFigureGroup, IsShadowCaster = false },
            };
            if (IdleAnimation != null)
                figure.Add(new AnimationComponent { Animations = { [PlayerVisualSystem.Clips[0]] = IdleAnimation } });
            AttachHeld(figure, MenuFigureGroup, false, HeldModel, PlayerVisualSystem.CaneEntity);
            FigureColors.Apply(figure.Get<ModelComponent>(), LaunchOptions.Current.Look);
            HudRenderer.Of(this);
            Services.GetService<SceneThrottle>()?.ShowOverlay(MenuFigureGroup);
            figure.Transform.Position = MenuFigureAt;
            figure.Transform.Rotation = Quaternion.RotationY(MathUtil.Pi - MathUtil.DegreesToRadians(MenuFigureTurn));
            var light = new Entity("MenuFigureLight")
            {
                new LightComponent { Type = new LightPoint { Radius = 3f }, Intensity = MenuLightIntensity },
            };
            light.Transform.Position = MenuLightAt;
            figure.AddChild(light);
            camera.AddChild(figure);
            visuals.RegisterShowcase(figure);
            menuFigure = figure;
        }

        /// <summary>The customize page changed the own colours: they show on the menu figure at once.</summary>
        void LookChanged(FigureLook look)
        {
            LaunchOptions.Current.Look = look;
            FigureColors.Apply(menuFigure?.Get<ModelComponent>(), look);
        }

        /// <summary>Hangs model on HeldBone of figure's model, as a child named name so it goes
        /// with it. The object is modelled in the figure's rest pose
        /// (.work/rig/bamboo.py): its local transform undoes the bone's rest, so at rest
        /// it sits as modelled and then follows the bone.</summary>
        void AttachHeld(Entity figure, RenderGroup group, bool shadow, Model model, string name)
        {
            var target = figure.Get<ModelComponent>();
            Skeleton skeleton = target?.Model?.Skeleton;
            int bone = skeleton == null ? -1 : Array.FindIndex(skeleton.Nodes, n => n.Name == HeldBone);
            if (model == null || bone < 0)
                return;
            // The bone's rest in model space, child first (row vectors).
            Matrix rest = Matrix.Identity;
            for (int i = bone; i >= 0; i = skeleton.Nodes[i].ParentIndex)
            {
                TransformTRS node = skeleton.Nodes[i].Transform;
                rest *= Matrix.Scaling(node.Scale) * Matrix.RotationQuaternion(node.Rotation) * Matrix.Translation(node.Position);
            }
            rest.Invert();
            var held = new Entity(name)
            {
                new ModelComponent(model) { RenderGroup = group, IsShadowCaster = shadow },
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
        readonly List<Entity> mapVisuals = new List<Entity>();
        Entity menu;
        Entity menuFigure;
        int games;
        string menuNote;
        PlayerInputReader localReader;
        Entity arms;
        readonly Dictionary<SimulationBody, (Entity Visual, Entity Label)> shown =
            new Dictionary<SimulationBody, (Entity, Entity)>();
    }
}
