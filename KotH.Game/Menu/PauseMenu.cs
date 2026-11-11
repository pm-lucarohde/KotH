using System;
using System.Collections.Generic;
using KotH.Client.Audio;
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
    /// <summary>What Escape opens in a game (Luca): Resume, Settings, To desktop and Main menu on a board over a veil,
    /// in front of the load screen too; Invite friends in a Steam round, Map for the host in the lobby (opens by itself).
    /// The own figure lets go of every key (SimulationDriver.Paused). An accepted Steam invite goes to the main menu,
    /// which joins it. Leaving a running online round asks first, naming the XP it costs where XP counts.</summary>
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

        /// <summary>The XP keeper: whether leaving costs XP, and the penalty.</summary>
        [DataMemberIgnore] public ProgressKeeper Progress { get; set; }

        /// <summary>The custom maps the host may choose besides the built-in ones.</summary>
        [DataMemberIgnore] public MapLibrary Library { get; set; }

        /// <summary>Whether it is open.</summary>
        [DataMemberIgnore] public bool Open { get; private set; }

        PauseMenuView view;
        HudRenderer hud;
        MenuOverlay board, page, confirm;
        MenuOverlay[] overlays;
        readonly OverlayPointer pointer = new OverlayPointer();
        bool resumeWanted, menuWanted, quitWanted, leaveWanted, confirming, toDesktop;
        float since;

        // The lobby session the map choice opened by itself for: once per landing.
        PeerSession landed;

        // Test aid for shots: KOTH_SHOT_PAUSE=open, settings, menu or leave (Main menu once a round runs), once per process.
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
            view.PlayersOpened += ListPlayers;
            view.Confirmed += leave =>
            {
                if (leave)
                    leaveWanted = true;
                else
                    confirming = false;
            };
            var parts = view.TakeParts();
            // In the middle of a screen 1080 high.
            board = new MenuOverlay(parts.Board, MenuOverlay.Corner.TopCenter, 0f, (1080f - 730f) / 2f) { Visible = false };
            page = new MenuOverlay(parts.Page, MenuOverlay.Corner.TopCenter, 0f, (1080f - 770f) / 2f) { Visible = false };
            confirm = new MenuOverlay(parts.Confirm, MenuOverlay.Corner.TopCenter, 0f, (1080f - 330f) / 2f) { Visible = false };
            overlays = new[] { board, page, confirm };
            hud.AddFront(this);
            hud.AddFront(board);
            hud.AddFront(page);
            hud.AddFront(confirm);
        }

        public override void Update()
        {
            if (view == null || Driver == null)
                return;
            // A running online round holds the window open; closing it asks first.
            WindowCloseGuard.Blocking = !Driver.InMenu && RoundRunning();
            if (WindowCloseGuard.TakeRequest())
            {
                if (!WindowCloseGuard.Blocking)
                    ((GameBase)Game).Exit();
                else
                {
                    if (!Open)
                        Show(true);
                    Ask(desktop: true);
                }
            }
            if (Driver.InMenu)
            {
                if (Open)
                    Show(false);
                return;
            }
            // An accepted invite, a refused password or a host key not the invite's: back to the main menu.
            bool refused = Services.GetService<NetClient>() is { } net && (net.Session.Rejected == RejectReason.Password || net.KeyRefused != null);
            if (SteamClient.HasInvite && Pregame() || refused)
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
            if (Open && view.PageOpen && view.SettingsView.Capture(Input))
            {
            }
            else if (!Hud.ChatBox.Busy && KeyPresses.Pressed(Input, Keys.Escape))
            {
                if (confirming)
                    confirming = false;
                else if (!Open)
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
            if (confirming)
                view.Ask(Penalty);
            if (view.PlayersShown && Game.UpdateTime.Total.TotalSeconds - listedAt > 0.5)
                ListPlayers();
            board.Visible = !view.PageOpen && !confirming;
            page.Visible = view.PageOpen && !confirming;
            confirm.Visible = confirming;
            Pop((float)Game.UpdateTime.Elapsed.TotalSeconds);
            foreach (MenuOverlay overlay in overlays)
                overlay.Upload(Game.GraphicsContext.CommandList);

            // Out of Avalonia's dispatcher: the main menu removes the game.
            if (resumeWanted)
                Show(false);
            if ((menuWanted || quitWanted) && RoundRunning())
            {
                Ask(desktop: quitWanted);
                menuWanted = quitWanted = false;
            }
            if (leaveWanted)
            {
                if (Penalty)
                    Progress.Forfeit();
                (menuWanted, quitWanted) = (!toDesktop, toDesktop);
            }
            if (menuWanted)
            {
                Show(false);
                ToMainMenu?.Invoke();
            }
            if (quitWanted)
                ((GameBase)Game).Exit();
            quitWanted = leaveWanted = false;
        }

        // "Leave the round?" before the main menu or the desktop.
        void Ask(bool desktop)
        {
            confirming = true;
            toDesktop = desktop;
            view.Ask(Penalty);
        }

        // Leaving now costs XP: a running online round that counts for the own XP.
        bool Penalty => RoundRunning() && Progress is { AtStake: true };

        void Show(bool open)
        {
            Open = open;
            Driver.Paused = open;
            resumeWanted = menuWanted = leaveWanted = confirming = false;
            view.PageOpen = false;
            if (open)
                Offer();
            since = 0f;
            if (!open)
            {
                pointer.Reset();
                board.Visible = page.Visible = confirm.Visible = false;
            }
        }

        void Offer() => view.Offer(invite: Lobby != null || InviteCode != null, players: Talk != null,
                                   maps: LobbyHost != null);

        // The Steam round's chat and voice, for the Players page.
        Comms Talk => Lobby != null ? Services.GetService<NetClient>()?.Comms : null;

        // Teams: whether the player with this NetId is in the own body's team.
        bool IsTeammate(int netId)
        {
            NetClient net = Services.GetService<NetClient>();
            return netId != 0 && net?.Session?.BodyOf(net.Session.LocalNetId) is { } self
                   && net.Session.BodyOf(netId) is { } other && other != self && HillMatch.SameTeam(self, other);
        }

        // Everyone else in the lobby, muted or not; the list keeps up with who speaks.
        void ListPlayers()
        {
            listedAt = Game.UpdateTime.Total.TotalSeconds;
            var rows = new List<RowList.Row>();
            if (Talk is { } talk)
                foreach (ulong member in talk.Others())
                {
                    bool muted = talk.IsMuted(member);
                    int netId = talk.NetIdOf(member);
                    string detail = muted ? "muted" : VoiceChat.IsSpeaking(netId) ? "speaking" : "";
                    if (IsTeammate(netId))
                        detail = detail.Length > 0 ? "team, " + detail : "team";
                    rows.Add(new RowList.Row(talk.NameOf(member), detail, muted ? "Unmute" : "Mute", () =>
                    {
                        talk.SetMuted(member, !muted);
                        ListPlayers();
                    }));
                }
            view.ShowList(rows, "Nobody else here", "Muted players are not heard and their chat lines not shown.");
        }

        double listedAt;

        // An online round on its map (stage Round, its win pause included): no leaving until the lobby.
        bool RoundRunning() => Services.GetService<NetClient>() is { Stage: MatchStage.Round };

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
            string chosen = client.NextMap ?? Maps.Default, chosenTitle = chosen;
            var maps = new List<(string Id, string Title, string Kind)>();
            foreach (string map in Maps.All)
                maps.Add((map, map, ""));
            // Custom maps: guests without one download it from the workshop (a local one only they lack).
            if (Library != null)
                foreach (KotH.MapFormat.MapPackage p in Library.All())
                    maps.Add((p.Id, p.Manifest.Title, p.WorkshopItem != 0 || p.Manifest.WorkshopId != 0 ? "workshop" : "local"));
            foreach ((string id, string title, string kind) in maps)
            {
                string pick = id;
                if (id == chosen)
                    chosenTitle = title;
                // Choosing closes the menu, as Resume does (out of Avalonia's dispatcher).
                rows.Add(new RowList.Row(title, id == chosen ? "chosen" : kind, "Choose", () =>
                {
                    client.Host.NextMap = pick;
                    ListMaps();
                    resumeWanted = true;
                }));
            }
            view.ShowList(rows, "", $"The round starts on {chosenTitle} when the countdown ends.");
        }

        // The lobby of the round, if it runs over Steam.
        SteamLobby Lobby => Services.GetService<NetClient>()?.Lobby is { Id: not 0 } lobby && SteamClient.Ready ? lobby : null;

        // A direct-IP host's invite code, for Invite friends without Steam.
        string InviteCode => Lobby == null ? Services.GetService<NetClient>()?.Invite : null;

        void ListFriends()
        {
            var rows = new List<RowList.Row>();
            if (InviteCode is { } code)
            {
                rows.Add(new RowList.Row(code, "", "Copy", () =>
                {
                    MenuTextBox.Copy(code);
                    view.Say("Copied: paste it into Join by invite (Find server). Over the internet use your public address.");
                }));
                view.ShowList(rows, "", "Friends join with this code; it carries this host's key.");
                return;
            }
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
            if (pretend == null || Services.GetService<LoadScreen>() is { Covers: true } || pretend == "leave" && !RoundRunning())
                return;
            string what = pretend;
            pretend = null;
            if (!Open)
                Show(true);
            if (what == "settings")
            {
                view.Pretend(1);
                view.SettingsView.Choose(Environment.GetEnvironmentVariable("KOTH_SHOT_SETTINGS") ?? "sound");
            }
            else if (what is "menu" or "leave")
                view.Pretend(2);
            else if (what == "players")
                view.Pretend(4);
            else if (what == "maps")
                view.Pretend(5);
        }

        /// <summary>The veil under the board.</summary>
        public void Draw(SpriteBatch batch, Vector2 screen)
        {
            if (Open)
                batch.Draw(hud.White, new RectangleF(0f, 0f, screen.X, screen.Y), Color.Black * (Veil * Math.Min(1f, since / PopSeconds)));
        }
    }
}
