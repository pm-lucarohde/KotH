using System;
using System.Collections.Generic;
using KotH.Client.Hud;
using KotH.Client.Net;
using KotH.Client.Player;
using KotH.Client.Steam;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Net;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Input;

namespace KotH.Client.Menu
{
    /// <summary>What Escape opens in a game (Luca): Resume, Settings, To desktop and Main menu on a board over a
    /// veil, in front of the load screen too; Invite friends in a Steam round, Map for the host in the lobby (opens
    /// by itself on landing). The game runs on, the own figure lets go of every key (SimulationDriver.Paused). A
    /// Steam invite accepted before the round runs goes to the main menu, which joins it.</summary>
    [DataContract("KotH.PauseMenu")]
    public sealed class PauseMenu : SyncScript, IHudElement
    {
        // The pop on opening, in seconds; the veil's darkness, blended in
        // linear light (0.6 leaves about two thirds of the brightness seen).
        const float PopSeconds = 0.16f;
        const float Veil = 0.6f;

        /// <summary>Main menu: GameRoot takes the game down.</summary>
        [DataMemberIgnore] public Action ToMainMenu { get; set; }

        /// <summary>The driver it pauses the own input with; it stays shut while the main menu is open.</summary>
        [DataMemberIgnore] public SimulationDriver Driver { get; set; }

        /// <summary>Whether it is open.</summary>
        [DataMemberIgnore] public bool Open { get; private set; }

        PauseMenuView view;
        HudRenderer hud;
        MenuOverlay board, page;
        MenuOverlay[] overlays;
        readonly OverlayPointer pointer = new OverlayPointer();
        bool resumeWanted, menuWanted, quitWanted;
        float since;

        // The lobby session the map choice opened by itself for: once per landing.
        PeerSession landed;

        // Test aid for shots: KOTH_SHOT_PAUSE=open, settings or menu, once per process.
        static string pretend = Environment.GetEnvironmentVariable("KOTH_SHOT_PAUSE") is { Length: > 0 } p ? p : null;

        public override void Start()
        {
            MenuOverlay.Start<MenuApp>();
            hud = HudRenderer.Of(this);
            view = new PauseMenuView();
            view.ResumeRequested += () => resumeWanted = true;
            view.MenuRequested += () => menuWanted = true;
            view.QuitRequested += () => quitWanted = true;
            view.InviteOpened += ListFriends;
            view.MapsOpened += ListMaps;
            var parts = view.TakeParts();
            // In the middle of a screen 1080 high.
            board = new MenuOverlay(parts.Board, MenuOverlay.Corner.TopCenter, 0f, (1080f - 586f) / 2f) { Visible = false };
            page = new MenuOverlay(parts.Page, MenuOverlay.Corner.TopCenter, 0f, (1080f - 610f) / 2f) { Visible = false };
            overlays = new[] { board, page };
            hud.AddFront(this);
            hud.AddFront(board);
            hud.AddFront(page);
        }

        public override void Update()
        {
            if (view == null || Driver == null)
                return;
            if (Driver.InMenu)
            {
                if (Open)
                    Show(false);
                return;
            }
            if (SteamClient.HasInvite && Pregame())
            {
                if (Open)
                    Show(false);
                ToMainMenu?.Invoke();
                return;
            }
            // The lobby ends (restart to the round's map): the map choice goes with it.
            if (Open && view.MapsShown && LobbyHost == null)
                Show(false);
            if (LobbyHost is { } host && host.Session != landed && Services.GetService<LoadScreen>() is not { Covers: true })
            {
                landed = host.Session;
                if (!Open)
                    Show(true);
                view.OpenMaps();
            }
            if (KeyPresses.Pressed(Input, Keys.Escape))
            {
                if (!Open)
                    Show(true);
                else if (view.PageOpen)
                    view.PageOpen = false;
                else
                    Show(false);
            }
            Pretend();
            if (!Open)
                return;

            var backBuffer = GraphicsDevice.Presenter.BackBuffer;
            var screen = new Vector2(backBuffer.Width, backBuffer.Height);
            foreach (MenuOverlay overlay in overlays)
                overlay.Place(screen);
            pointer.Update(Input, screen, overlays);
            MenuOverlay.Tick(Game.UpdateTime.FrameCount);
            board.Visible = !view.PageOpen;
            page.Visible = view.PageOpen;
            Pop((float)Game.UpdateTime.Elapsed.TotalSeconds);
            foreach (MenuOverlay overlay in overlays)
                overlay.Upload(Game.GraphicsContext.CommandList);

            // Out of Avalonia's dispatcher: the main menu removes the game.
            if (resumeWanted)
                Show(false);
            if (menuWanted)
            {
                Show(false);
                ToMainMenu?.Invoke();
            }
            if (quitWanted)
                ((GameBase)Game).Exit();
        }

        void Show(bool open)
        {
            Open = open;
            Driver.Paused = open;
            resumeWanted = menuWanted = false;
            view.PageOpen = false;
            if (open)
                view.Offer(invite: Lobby != null, maps: LobbyHost != null);
            since = 0f;
            if (!open)
            {
                pointer.Reset();
                board.Visible = page.Visible = false;
            }
        }

        // Waiting for players or counting down, or still connecting: an invite may end it.
        bool Pregame()
        {
            if (Services.GetService<NetClient>() is { Session.Joined: false })
                return true;
            return Services.GetService<SimulationSystem>()?.Match?.State.Phase is MatchPhase.Waiting or MatchPhase.Countdown;
        }

        // The client while it hosts a lobby with its own peer in it: the host chooses the round's map.
        NetClient LobbyHost => Services.GetService<NetClient>() is { Host: not null, Restarting: false, Stage: MatchStage.Lobby } c
                               && c.Session.Joined ? c : null;

        void ListMaps()
        {
            if (LobbyHost is not { } client)
                return;
            var rows = new List<RowList.Row>();
            string chosen = client.NextMap ?? Maps.Default;
            foreach (string map in Maps.All)
            {
                string pick = map;
                rows.Add(new RowList.Row(map, map == chosen ? "chosen" : "", "Choose", () =>
                {
                    client.Host.NextMap = pick;
                    ListMaps();
                }));
            }
            view.ShowList(rows, "", $"The round starts on {chosen} when the countdown ends.");
        }

        // The lobby of the round, if it runs over Steam.
        SteamLobby Lobby => Services.GetService<NetClient>()?.Lobby is { Id: not 0 } lobby && SteamClient.Ready ? lobby : null;

        void ListFriends()
        {
            var rows = new List<RowList.Row>();
            foreach ((ulong id, string name) in SteamClient.OnlineFriends())
            {
                string friend = name;
                rows.Add(new RowList.Row(name, "", "Invite", () =>
                    view.Say(Lobby?.Invite(id) == true ? $"Invited {friend}" : $"Could not invite {friend}")));
            }
            view.ShowList(rows, "No friends online", rows.Count == 0 ? "" : "They get it in their Steam chat.");
        }

        /// <summary>The board grows in from a little smaller and fades in, as finished pictures.</summary>
        void Pop(float seconds)
        {
            since += seconds;
            float t = Math.Min(1f, since / PopSeconds);
            foreach (MenuOverlay overlay in overlays)
            {
                overlay.Zoom = 0.94f + 0.06f * MainMenu.BackOut(t);
                overlay.Alpha = t;
            }
        }

        void Pretend()
        {
            if (pretend == null || Services.GetService<LoadScreen>() is { Covers: true })
                return;
            string what = pretend;
            pretend = null;
            if (!Open)
                Show(true);
            if (what == "settings")
                view.Pretend(1);
            else if (what == "menu")
                view.Pretend(2);
        }

        /// <summary>The veil under the board.</summary>
        public void Draw(SpriteBatch batch, Vector2 screen)
        {
            if (Open)
                batch.Draw(hud.White, new RectangleF(0f, 0f, screen.X, screen.Y), Color.Black * (Veil * Math.Min(1f, since / PopSeconds)));
        }
    }
}
