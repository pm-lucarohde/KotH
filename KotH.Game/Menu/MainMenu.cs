using System;
using System.Runtime;
using KotH.Client.Hud;
using KotH.Client.Net;
using KotH.Client.Player;
using KotH.Client.Rendering;
using KotH.Client.Steam;
using KotH.Shared.Match;
using KotH.Shared.Net;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Input;

namespace KotH.Client.Menu
{
    /// <summary>The main menu over the scene: MainMenuView's five parts as overlays of their own,
    /// drawn last by the HUD. GameRoot creates it with the map behind the load screen, which waits
    /// for <see cref="Ready"/> (first frames out of sight); back from a game it shows at once.
    /// GameRoot removes it when a game starts.</summary>
    [DataContract("KotH.MainMenu")]
    public sealed class MainMenu : SyncScript
    {
        /// <summary>The map behind the menu needs no more frames than this (Luca); the
        /// menu itself runs at the game's own limit (--fps, KOTH_FPS or none). Behind the
        /// load screen the map runs free, its bake not slowed.</summary>
        public const int MapFps = 30;

        // Ready once the map is loaded and then CalmNeeded of the last CalmFrames frames came quicker
        // than CalmFrame seconds (or 1.5 frame-cap intervals), or Longest real seconds after it at most;
        // the lit buttons go dark after the first WarmFrames calm ones.
        const int CalmFrames = 30, CalmNeeded = 27;
        const int WarmFrames = 6;
        const float CalmFrame = 0.025f;
        const float Longest = 3f;

        // The entrance, in seconds and units of a screen 1080 high.
        const float SlideSeconds = 0.75f, SlideFrom = 640f;
        const float DropDelay = 0.35f, DropSeconds = 0.9f, DropFrom = 80f;
        const float FadeSeconds = 0.4f;

        /// <summary>Host server's page: GameRoot opens a lobby on the map behind the menu, listed with these
        /// flags; a private one admits only who knows the password (or was invited).</summary>
        [DataMemberIgnore] public Action<LobbyFlags, string> StartHost { get; set; }

        /// <summary>A round from Find server or an invite, its lobby entered: GameRoot joins it, with the
        /// password typed for a private one (null otherwise).</summary>
        [DataMemberIgnore] public Action<SteamLobby, string> StartJoin { get; set; }

        /// <summary>GameRoot: joins a direct-IP host by its invite code.</summary>
        [DataMemberIgnore] public Action<string> StartInvite { get; set; }

        /// <summary>Why the last game ended before it began (a wrong password): Find server opens with it.</summary>
        [DataMemberIgnore] public string Note { get; set; }


        /// <summary>The name on the band.</summary>
        [DataMemberIgnore] public string PlayerName { get; set; }

        /// <summary>The own colours, for the Customize page to start from.</summary>
        [DataMemberIgnore] public FigureLook Look { get; set; } = FigureLook.Default;

        /// <summary>The player changed a colour on the Customize page.</summary>
        [DataMemberIgnore] public Action<FigureLook> LookChanged { get; set; }

        /// <summary>The Customize page opened (true) or closed: the figure makes room.</summary>
        [DataMemberIgnore] public Action<bool> CustomizeShown { get; set; }

        /// <summary>The load screen in front, until it lifts; then the boards slide in.</summary>
        [DataMemberIgnore] public LoadScreen Cover { get; set; }

        /// <summary>Whether its first frames are done and the frames run evenly: the load screen may lift.</summary>
        [DataMemberIgnore] public bool Ready { get; private set; }

        MainMenuView view;
        HudRenderer hud;
        SceneThrottle throttle;
        MenuOverlay column, page, customize, logo, corner;
        MenuOverlay[] overlays, pointed;
        readonly OverlayPointer pointer = new OverlayPointer();
        readonly bool[] calmWindow = new bool[CalmFrames];
        int calmSlot, calmFilled, calm;
        readonly System.Diagnostics.Stopwatch waiting = new System.Diagnostics.Stopwatch();
        // Find server searches again this often while open: a new lobby takes Steam a few seconds to list.
        const float SearchEvery = 5f;

        LobbyFlags hostFlags;
        string hostPassword;
        bool loaded, dark, shown, hostWanted, quitWanted, customizing, unsaved, entering, searching;
        float searched;
        int searches;
        SteamLobby entered;
        string enteredPassword;
        float since;

        public override void Start()
        {
            MenuOverlay.Start<MenuApp>();
            hud = HudRenderer.Of(this);
            throttle = Services.GetService<SceneThrottle>();

            view = new MainMenuView();
            view.Label(PlayerName ?? Environment.UserName, $"Build {NetProtocol.BuildText[..8]}");
            view.HostOpened += () => view.Hoster.Open(PlayerName ?? Environment.UserName);
            view.Hoster.HostRequested += (flags, password) =>
            {
                (hostFlags, hostPassword) = (flags, password);
                hostWanted = true;
            };
            view.QuitRequested += () => quitWanted = true;
            view.FindOpened += Search;
            view.Finder.RefreshRequested += Search;
            view.Finder.JoinRequested += (round, password) => Enter(round.Id, round.Host, password, 0);
            view.Finder.InviteRequested += code => StartInvite?.Invoke(code);
            view.Customizer.Look = Look;
            view.Customizer.LookChanged += look =>
            {
                unsaved = true;
                LookChanged?.Invoke(look);
            };
            // Over a map loaded before (back from a game) nothing hides the warming.
            if (Cover is { IsLoaded: true })
                Ready = dark = loaded = true;
            else
            {
                if (Cover != null)
                    Cover.Loaded += OnLoaded;
                view.Light(true);
            }

            var parts = view.TakeParts();
            column = new MenuOverlay(parts.Column, MenuOverlay.Corner.TopLeft, 0f, 0f);
            page = new MenuOverlay(parts.Page, MenuOverlay.Corner.TopLeft, 586f, 146f) { Visible = false };
            customize = new MenuOverlay(parts.Customize, MenuOverlay.Corner.TopLeft, 586f, 146f) { Visible = false };
            logo = new MenuOverlay(parts.Logo, MenuOverlay.Corner.TopRight, 0f, 0f);
            corner = new MenuOverlay(parts.Corner, MenuOverlay.Corner.BottomRight, 0f, 0f);
            overlays = new[] { column, page, customize, logo, corner };
            pointed = new[] { column, page, customize };
            foreach (MenuOverlay overlay in overlays)
                hud.Add(overlay);
        }

        public override void Update()
        {
            if (view == null)
                return;
            var backBuffer = GraphicsDevice.Presenter.BackBuffer;
            var screen = new Vector2(backBuffer.Width, backBuffer.Height);
            foreach (MenuOverlay overlay in overlays)
                overlay.Place(screen);

            if (Cover is not { Covers: true })
            {
                if (throttle != null)
                    throttle.Interval = 1.0 / MapFps;
                if (!shown)
                    Reveal();
                Enter((float)Game.UpdateTime.Elapsed.TotalSeconds, screen.Y / (float)MainMenuView.DesignHeight);
                if (!MenuTextBox.Capture(Input) && !view.SettingsView.Capture(Input) && KeyPresses.Pressed(Input, Keys.Escape)
                    && !view.Finder.Cancel())
                    view.GoBack();
                pointer.Update(Input, screen, pointed);
            }

            if (progressShown != SteamStats.Version)
                ShowProgress();
            MenuOverlay.Tick(Game.UpdateTime.FrameCount);
            page.Visible = view.PageOpen;
            customize.Visible = view.CustomizeOpen;
            logo.Visible = !view.PageOpen && !view.CustomizeOpen;
            if (customizing != view.CustomizeOpen)
            {
                customizing = view.CustomizeOpen;
                CustomizeShown?.Invoke(customizing);
                if (!customizing)
                    Save();
            }
            foreach (MenuOverlay overlay in overlays)
                overlay.Upload(Game.GraphicsContext.CommandList);
            Settle((float)Game.UpdateTime.Elapsed.TotalSeconds);

            searched += (float)Game.UpdateTime.Elapsed.TotalSeconds;
            if (view.FindOpen && !searching && !entering && searched >= SearchEvery && SteamClient.Ready)
                Search(again: true);
            if (shown && !entering && entered == null)
                Follow((float)Game.UpdateTime.Elapsed.TotalSeconds);
            if (hostWanted)
            {
                // Out of Avalonia's dispatcher: the host removes this menu.
                hostWanted = false;
                StartHost?.Invoke(hostFlags, hostPassword);
            }
            else if (entered != null)
            {
                SteamLobby lobby = entered;
                entered = null;
                StartJoin?.Invoke(lobby, enteredPassword);
            }
            if (quitWanted)
                ((GameBase)Game).Exit();
        }

        void Search() => Search(again: false);

        void Search(bool again)
        {
            if (!SteamClient.Ready)
            {
                view.Finder.Unavailable(SteamClient.Failure);
                return;
            }
            if (!again)
                view.Finder.Searching();
            searching = true;
            searched = 0f;
            // A filter changed while Steam searched: only the newest search's answer counts.
            int asked = ++searches;
            SteamLobby.Search(view.Finder.Filter, rounds =>
            {
                if (asked != searches)
                    return;
                searching = false;
                view?.Finder.Found(rounds, again);
            });
        }

        // An accepted invite (a round's or a party's), else the round the party's leader went to; the
        // party's leader is in no round here. The Party page shows who came or went, once a second.
        void Follow(float seconds)
        {
            SteamParty.Playing(0);
            if (SteamClient.TakeInvite(out ulong invited, out ulong friend))
            {
                view.OpenFind();
                Enter(invited, null, null, friend);
            }
            else if (SteamParty.FollowTo(out ulong round, out ulong leader))
            {
                view.OpenFind();
                Enter(round, $"{SteamClient.NameOf(leader)}'s round", null, leader);
            }
            partyShown += seconds;
            if (view.SocialOpen && partyShown >= 1f)
            {
                partyShown = 0f;
                view.SocialView.Refresh();
            }
        }

        float partyShown;

        // Enters a round's lobby; it is joined only on the host's build (GameRoot changes the map if need be).
        // A party's lobby becomes the party instead. cameWith: the friend this player came with, 0 for none.
        void Enter(ulong id, string host, string password, ulong cameWith)
        {
            if (entering || !SteamClient.Ready)
                return;
            entering = true;
            enteredPassword = password;
            view.Finder.Joining(host ?? "the round");
            SteamLobby.Enter(id, (lobby, why) =>
            {
                entering = false;
                if (lobby != null && view == null)
                    lobby.Dispose();
                if (lobby == null || view == null)
                {
                    view?.Finder.Failed(why);
                    return;
                }
                if (lobby.Build != NetProtocol.BuildText)
                {
                    lobby.Dispose();
                    view.Finder.Failed(lobby.IsParty ? "Different build than your friend" : "Different build than the host");
                }
                else if (lobby.IsParty)
                {
                    SteamParty.Joined(lobby);
                    view.OpenSocial(party: true);
                }
                else
                {
                    lobby.CameWith = cameWith;
                    entered = lobby;
                }
            });
        }

        /// <summary>
        /// Counts even frames behind the load screen: a few with every button
        /// lit, then more with them dark (their transitions run out there).
        /// </summary>
        void Settle(float seconds)
        {
            if (Ready || Cover != null && !loaded)
                return;
            if (!waiting.IsRunning)
                waiting.Start();
            double waited = waiting.Elapsed.TotalSeconds;
            CountCalm(seconds <= Math.Max(CalmFrame, 1.5 * ((GameBase)Game).WindowMinimumUpdateRate.MinimumElapsedTime.TotalSeconds));
            if (!dark && (calm >= WarmFrames || waited >= Longest))
            {
                view.Light(false);
                dark = true;
                Array.Clear(calmWindow);
                calmFilled = calm = 0;
            }
            else if (dark && (calmFilled == CalmFrames && calm >= CalmNeeded || waited >= Longest))
            {
                // Loading and warming left garbage behind: collected here, not as
                // a stutter a second after the menu shows.
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                Ready = true;
                bool even = calm >= CalmNeeded;
                Console.WriteLine($"[menu] ready {waited:F1} s after loading{(even ? "" : ", without calm frames")}");
                StartLog.Mark($"menu ready {waited:F1} s after loading, {calm}/{calmFilled} calm{(even ? "" : " (TIMEOUT)")}; effects {EffectRecorder.Counts}");
                PipelineCache.Save(now: false);
            }
        }

        // The last CalmFrames frames, whether each was calm; calm counts the calm ones among them.
        void CountCalm(bool now)
        {
            if (calmFilled == CalmFrames && calmWindow[calmSlot])
                calm--;
            calmWindow[calmSlot] = now;
            calm += now ? 1 : 0;
            calmSlot = (calmSlot + 1) % CalmFrames;
            calmFilled = Math.Min(calmFilled + 1, CalmFrames);
        }

        /// <summary>The load screen lifted: the boards slide in.</summary>
        void Reveal()
        {
            shown = true;
            // Test aids for shots: KOTH_SHOT_MENU_HOT=n lights button n, KOTH_SHOT_MENU_PAGE=n clicks it (0 hosts),
            // KOTH_SHOT_SETTINGS=sound|graphics|input picks the Settings tab, KOTH_SHOT_HOST_PRIVATE=pw a private round,
            // KOTH_SHOT_SOCIAL=board the Leaderboard tab (=sample with made-up rows), KOTH_SHOT_FIND_DROP=0-4 opens a filter.
            if (int.TryParse(Environment.GetEnvironmentVariable("KOTH_SHOT_MENU_HOT"), out int hot))
                view.Pretend(hot, false);
            if (int.TryParse(Environment.GetEnvironmentVariable("KOTH_SHOT_MENU_PAGE"), out int open))
                view.Pretend(open, true);
            if (Environment.GetEnvironmentVariable("KOTH_SHOT_HOST_PRIVATE") is { } pass)
                view.Hoster.Pretend(pass);
            if (Environment.GetEnvironmentVariable("KOTH_SHOT_SETTINGS") is { } tab)
                view.SettingsView.Choose(tab);
            if (Environment.GetEnvironmentVariable("KOTH_SHOT_SOCIAL") is "board" or "sample")
                view.SocialView.Choose(true);
            if (Environment.GetEnvironmentVariable("KOTH_SHOT_SOCIAL") == "sample")
                view.SocialView.Leaderboard.Sample();
            if (int.TryParse(Environment.GetEnvironmentVariable("KOTH_SHOT_FIND_DROP"), out int drop))
                view.Finder.Pretend(drop);
            if (Note != null)
            {
                view.OpenFind();
                Search();
                view.Finder.Failed(Note);
                Note = null;
            }
        }

        /// <summary>The entrance, by moving the finished pictures only: the buttons slide
        /// in from the left, the title drops in and springs, the corner fades. Animated
        /// in Avalonia it redrew both parts every frame (15-20 ms).</summary>
        void Enter(float seconds, float scale)
        {
            if (since > DropDelay + DropSeconds)
                return;
            since += seconds;
            float slide = BackOut(Math.Min(1f, since / SlideSeconds));
            column.Shift = new Vector2(-SlideFrom * scale * (1f - slide), 0f);
            column.Alpha = Math.Min(1f, since / FadeSeconds);
            float t = Math.Clamp((since - DropDelay) / DropSeconds, 0f, 1f), drop = ElasticOut(t);
            logo.Shift = new Vector2(0f, -DropFrom * scale * (1f - drop));
            logo.Zoom = 0.85f + 0.15f * drop;
            logo.Alpha = corner.Alpha = Math.Clamp((since - DropDelay) / FadeSeconds, 0f, 1f);
        }

        // The easings Avalonia's BackEaseOut and ElasticEaseOut use.
        internal static float BackOut(float t)
        {
            float u = t - 1f;
            return 1f + u * u * (2.70158f * u + 1.70158f);
        }

        static float ElasticOut(float t)
            => t >= 1f ? 1f : MathF.Sin(-13f * MathF.PI / 2f * (t + 1f)) * MathF.Pow(2f, -10f * t) + 1f;

        // The level bar and the Customize lock as Steam (or the file) has them; the figure loses or
        // regains its colours with the level.
        void ShowProgress()
        {
            progressShown = SteamStats.Version;
            (int level, int into, int span) = Levels.Into(PlayerProgress.Xp);
            view.Progress(level, into, span, !PlayerProgress.Live, !PlayerProgress.CanCustomize);
            LookChanged?.Invoke(view.Customizer.Look);
        }

        int progressShown = -1;

        // The look goes to look.toml when the page closes or the menu goes, if it changed.
        void Save()
        {
            if (!unsaved || view == null)
                return;
            unsaved = false;
            LookFile.Save(view.Customizer.Look);
        }

        void OnLoaded() => loaded = true;

        public override void Cancel()
        {
            if (Cover != null)
                Cover.Loaded -= OnLoaded;
            Save();
            entered?.Dispose();
            entered = null;
            if (throttle != null)
            {
                throttle.Interval = 0.0;
                throttle.Release();
            }

            if (overlays == null)
                return;
            foreach (MenuOverlay overlay in overlays)
            {
                hud.Remove(overlay);
                overlay.Dispose();
            }
            overlays = null;
            view = null;
        }
    }
}
