using System;
using KotH.Client.Menu;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Corner = KotH.Client.Menu.MenuOverlay.Corner;

namespace KotH.Client.Hud
{
    /// <summary>Black with a turning clock and a word at the bottom right (LoadView, Luca's choice)
    /// from the first frame until the map's light is baked and a moment after, so the first
    /// seconds' stutter is never seen. Nothing starts before Loaded (round, host, guest join).
    /// The hands turn as finished pictures, costing Avalonia nothing per frame.</summary>
    public sealed class LoadScreen : IHudCover
    {
        // Seconds it stays after the bake, while the last first-use work settles,
        // and at most from the map on, whatever happens.
        const double Settle = 0.5;
        const double Longest = 20.0;

        // Seconds for one turn of the minute hand and of the hour hand.
        const double MinuteTurn = 2.0, HourTurn = 24.0;

        // Test aid for shots: KOTH_SHOT_LOAD=1 keeps it up.
        static readonly bool Held = Environment.GetEnvironmentVariable("KOTH_SHOT_LOAD") == "1";

        readonly HudRenderer hud;
        readonly LoadView view;
        readonly MenuOverlay minute, hour;
        readonly MenuOverlay[] overlays;
        bool begun, loaded;
        double shown, turned, bakedAt = -1.0;

        public LoadScreen(HudRenderer hud)
        {
            this.hud = hud;
            MenuOverlay.Start<MenuApp>();
            view = new LoadView();
            view.TakeParts();
            // In units of a screen 1080 high: the clock 40 in from the corner, the word left of it.
            minute = new MenuOverlay(view.MinutePart, Corner.BottomRight, 40f, 40f);
            hour = new MenuOverlay(view.HourPart, Corner.BottomRight, 40f, 40f);
            overlays = new[]
            {
                new MenuOverlay(view.FacePart, Corner.BottomRight, 40f, 40f), hour, minute,
                new MenuOverlay(view.PinPart, Corner.BottomRight, 40f, 40f),
                new MenuOverlay(view.WordsPart, Corner.BottomRight, 178f, 40f),
            };
        }

        /// <summary>What it says while no map loads, such as the connection; null for the default.</summary>
        public Func<string> Text { get; set; }

        /// <summary>Something else to wait for once loaded, such as the host letting a guest in.</summary>
        public Func<bool> Hold { get; set; }

        /// <summary>Raised once, when the map is loaded and its lighting baked.</summary>
        public event Action Loaded;

        public bool Covers { get; private set; } = true;

        /// <summary>Whether Loaded was raised already.</summary>
        public bool IsLoaded => loaded;

        /// <summary>A map is being built now: from here on it waits for its bake.</summary>
        public void Begin() => begun = true;

        /// <summary>The map's voxels are all in.</summary>
        public void Baked()
        {
            if (begun && bakedAt < 0.0)
                bakedAt = shown;
        }

        /// <summary>Once per frame, with the frame's seconds and number; renders and uploads the clock while it covers.</summary>
        public void Update(double seconds, long frame, CommandList commandList)
        {
            if (begun && !loaded)
            {
                shown += seconds;
                if (shown >= Longest || bakedAt >= 0.0 && shown - bakedAt >= Settle)
                {
                    loaded = true;
                    // Loading and warming the HUD left garbage behind: collected here, not in play.
                    GC.Collect();
                    Console.WriteLine(bakedAt < 0.0 ? $"[load] done after {shown:F1} s, light not baked"
                                                    : $"[load] done after {shown:F1} s, light baked after {bakedAt:F1} s");
                    Loaded?.Invoke();
                }
            }
            Covers = !loaded || Held || (Hold?.Invoke() ?? false);
            if (!Covers)
                return;

            turned += seconds;
            view.Say((begun && !loaded ? null : Text?.Invoke()) ?? "Loading …");
            Texture backBuffer = commandList.GraphicsDevice.Presenter.BackBuffer;
            var screen = new Vector2(backBuffer.Width, backBuffer.Height);
            foreach (MenuOverlay overlay in overlays)
                overlay.Place(screen);
            minute.Angle = (float)(turned % MinuteTurn / MinuteTurn * 2.0 * Math.PI);
            hour.Angle = (float)(turned % HourTurn / HourTurn * 2.0 * Math.PI);
            MenuOverlay.Tick(frame);
            foreach (MenuOverlay overlay in overlays)
                overlay.Upload(commandList);
        }

        public void Draw(SpriteBatch batch, Vector2 screen)
        {
            batch.Draw(hud.White, new RectangleF(0f, 0f, screen.X, screen.Y), Color.Black);
            foreach (MenuOverlay overlay in overlays)
                overlay.Draw(batch, screen);
        }
    }
}
