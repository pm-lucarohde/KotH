using System;
using System.Runtime;
using KotH.Client.Hud;
using KotH.Client.Net;
using KotH.Client.Rendering;
using KotH.Shared.Net;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Input;

namespace KotH.Client.Menu
{
    /// <summary>
    /// The main menu over the scene: MainMenuView's four parts as overlays of
    /// their own (MenuOverlay), drawn last by the HUD. GameRoot creates it with
    /// the map, behind the load screen, which waits for <see cref="Ready"/>:
    /// the first Avalonia frames and the flight's first views are done there,
    /// not in sight; back from a game over the loaded map it shows at once.
    /// GameRoot removes it when a game starts.
    /// </summary>
    [DataContract("KotH.MainMenu")]
    public sealed class MainMenu : SyncScript
    {
        /// <summary>
        /// The map behind the menu needs no more frames than this (Luca); the
        /// menu itself runs at the game's own limit (--fps, KOTH_FPS or none).
        /// Behind the load screen the map runs free, its bake not slowed.
        /// </summary>
        public const int MapFps = 30;

        // Ready once the map is loaded and then this many frames in a row came
        // quicker than CalmFrame seconds, or Longest seconds after it at most;
        // the lit buttons go dark after the first WarmFrames of them.
        const int CalmFrames = 30;
        const int WarmFrames = 6;
        const float CalmFrame = 0.025f;
        const float Longest = 8f;

        // The entrance, in seconds and units of a screen 1080 high.
        const float SlideSeconds = 0.75f, SlideFrom = 640f;
        const float DropDelay = 0.35f, DropSeconds = 0.9f, DropFrom = 80f;
        const float FadeSeconds = 0.4f;

        /// <summary>Server erstellen: GameRoot hosts on the loaded map.</summary>
        [DataMemberIgnore] public Action StartHost { get; set; }

        /// <summary>The name on the band.</summary>
        [DataMemberIgnore] public string PlayerName { get; set; }

        /// <summary>The load screen in front, until it lifts; then the boards slide in.</summary>
        [DataMemberIgnore] public LoadScreen Cover { get; set; }

        /// <summary>Whether its first frames are done and the frames run evenly: the load screen may lift.</summary>
        [DataMemberIgnore] public bool Ready { get; private set; }

        MainMenuView view;
        HudRenderer hud;
        SceneThrottle throttle;
        MenuOverlay column, page, logo, corner;
        MenuOverlay[] overlays, pointed;
        readonly OverlayPointer pointer = new OverlayPointer();
        int calm;
        float waited;
        bool loaded, dark, shown, hostWanted, quitWanted;
        float since;

        public override void Start()
        {
            MenuOverlay.Start<MenuApp>();
            hud = HudRenderer.Of(this);
            throttle = Services.GetService<SceneThrottle>();

            view = new MainMenuView();
            view.Label(PlayerName ?? Environment.UserName, $"Build {NetProtocol.BuildText[..8]}");
            view.HostRequested += () => hostWanted = true;
            view.QuitRequested += () => quitWanted = true;
            // Over a map loaded before (back from a game) nothing hides the warming.
            if (Cover is { IsLoaded: true })
                Ready = dark = loaded = true;
            else
            {
                if (Cover != null)
                    Cover.Loaded += () => loaded = true;
                view.Light(true);
            }

            var parts = view.TakeParts();
            column = new MenuOverlay(parts.Column, MenuOverlay.Corner.TopLeft, 0f, 0f);
            page = new MenuOverlay(parts.Page, MenuOverlay.Corner.TopLeft, 586f, 146f) { Visible = false };
            logo = new MenuOverlay(parts.Logo, MenuOverlay.Corner.TopRight, 0f, 0f);
            corner = new MenuOverlay(parts.Corner, MenuOverlay.Corner.BottomRight, 0f, 0f);
            overlays = new[] { column, page, logo, corner };
            pointed = new[] { column, page };
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
                if (KeyPresses.Pressed(Input, Keys.Escape))
                    view.GoBack();
                pointer.Update(Input, screen, pointed);
            }

            MenuOverlay.Tick(Game.UpdateTime.FrameCount);
            page.Visible = view.PageOpen;
            logo.Visible = !view.PageOpen;
            foreach (MenuOverlay overlay in overlays)
                overlay.Upload(Game.GraphicsContext.CommandList);
            Settle((float)Game.UpdateTime.Elapsed.TotalSeconds);

            if (hostWanted)
            {
                // Out of Avalonia's dispatcher: the host removes this menu.
                hostWanted = false;
                StartHost?.Invoke();
            }
            if (quitWanted)
                ((GameBase)Game).Exit();
        }

        /// <summary>
        /// Counts even frames behind the load screen: a few with every button
        /// lit, then more with them dark (their transitions run out there).
        /// </summary>
        void Settle(float seconds)
        {
            if (Ready || Cover != null && !loaded)
                return;
            waited += seconds;
            calm = seconds < CalmFrame ? calm + 1 : 0;
            if (!dark && (calm >= WarmFrames || waited >= Longest))
            {
                view.Light(false);
                dark = true;
                calm = 0;
            }
            else if (dark && (calm >= CalmFrames || waited >= Longest))
            {
                // Loading and warming left garbage behind: collected here, not as
                // a stutter a second after the menu shows.
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                Ready = true;
                Console.WriteLine($"[menü] bereit {waited:F1} s nach dem Laden{(calm >= CalmFrames ? "" : ", ohne ruhige Bilder")}");
            }
        }

        /// <summary>The load screen lifted: the boards slide in.</summary>
        void Reveal()
        {
            shown = true;
            // Test aids for shots: KOTH_SHOT_MENU_HOT=n lights button n, KOTH_SHOT_MENU_PAGE=n clicks it (0 hosts).
            if (int.TryParse(Environment.GetEnvironmentVariable("KOTH_SHOT_MENU_HOT"), out int hot))
                view.Pretend(hot, false);
            if (int.TryParse(Environment.GetEnvironmentVariable("KOTH_SHOT_MENU_PAGE"), out int open))
                view.Pretend(open, true);
        }

        /// <summary>
        /// The entrance, by moving the finished pictures only: the buttons slide
        /// in from the left, the title drops in and springs, the corner fades.
        /// Animated in Avalonia it redrew both parts every frame (15-20 ms).
        /// </summary>
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

        public override void Cancel()
        {
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
