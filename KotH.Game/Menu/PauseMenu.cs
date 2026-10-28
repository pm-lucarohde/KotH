using System;
using KotH.Client.Hud;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Input;

namespace KotH.Client.Menu
{
    /// <summary>
    /// What Escape opens in a game (Luca): Resume, Settings,
    /// To desktop and Main menu on a board in the middle, over a dark veil
    /// and in front of the load screen too. The game runs on behind it, the
    /// own figure lets go of every key (SimulationDriver.Paused). Closed it
    /// costs nothing: its parts are only ticked and uploaded while open.
    /// </summary>
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
            var parts = view.TakeParts();
            // In the middle of a screen 1080 high.
            board = new MenuOverlay(parts.Board, MenuOverlay.Corner.TopCenter, 0f, (1080f - 418f) / 2f) { Visible = false };
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
            if (KeyPresses.Pressed(Input, Keys.Escape))
            {
                if (!Open)
                    Show(true);
                else if (view.SettingsOpen)
                    view.SettingsOpen = false;
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
            board.Visible = !view.SettingsOpen;
            page.Visible = view.SettingsOpen;
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
            view.SettingsOpen = false;
            since = 0f;
            if (!open)
            {
                pointer.Reset();
                board.Visible = page.Visible = false;
            }
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
