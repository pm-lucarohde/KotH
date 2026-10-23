using System;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace KotH.Client.Hud
{
    /// <summary>
    /// Black with a word while the world gets ready: from the first frame
    /// until the map's lighting is baked (BakeVoxels reports it) and a moment
    /// after, so the stutter of the first seconds is never seen (Lucas Wahl).
    /// Nothing starts before Loaded: alone the round waits (SimulationDriver),
    /// a host opens its game then, a guest joins then (GameRoot).
    /// </summary>
    public sealed class LoadScreen : IHudCover
    {
        // Seconds it stays after the bake, while the last first-use work settles,
        // and at most from the map on, whatever happens.
        const double Settle = 0.5;
        const double Longest = 20.0;

        readonly HudRenderer hud;
        bool begun, loaded;
        double shown, bakedAt = -1.0;

        public LoadScreen(HudRenderer hud) => this.hud = hud;

        public SpriteFont Font { get; set; }

        /// <summary>What it says while no map loads, such as the connection; null for the default.</summary>
        public Func<string> Text { get; set; }

        /// <summary>Something else to wait for once loaded, such as the host letting a guest in.</summary>
        public Func<bool> Hold { get; set; }

        /// <summary>Raised once, when the map is loaded and its lighting baked.</summary>
        public event Action Loaded;

        public bool Covers { get; private set; } = true;

        /// <summary>A map is being built now: from here on it waits for its bake.</summary>
        public void Begin() => begun = true;

        /// <summary>The map's voxels are all in.</summary>
        public void Baked()
        {
            if (begun && bakedAt < 0.0)
                bakedAt = shown;
        }

        /// <summary>Once per frame, with the frame's seconds.</summary>
        public void Update(double seconds)
        {
            if (begun && !loaded)
            {
                shown += seconds;
                if (shown >= Longest || bakedAt >= 0.0 && shown - bakedAt >= Settle)
                {
                    loaded = true;
                    Console.WriteLine(bakedAt < 0.0 ? $"[laden] fertig nach {shown:F1} s, ohne fertiges Licht"
                                                    : $"[laden] fertig nach {shown:F1} s, Licht nach {bakedAt:F1} s gebacken");
                    Loaded?.Invoke();
                }
            }
            Covers = !loaded || (Hold?.Invoke() ?? false);
        }

        public void Draw(SpriteBatch batch, Vector2 screen)
        {
            batch.Draw(hud.White, new RectangleF(0f, 0f, screen.X, screen.Y), Color.Black);
            if (Font == null)
                return;
            string text = (begun && !loaded ? null : Text?.Invoke()) ?? "Lädt ...";
            Vector2 extent = Font.MeasureString(text, Font.Size);
            batch.DrawString(Font, text, Font.Size, new Vector2(MathF.Round((screen.X - extent.X) * 0.5f),
                                                                MathF.Round(screen.Y * 0.32f)), Color.White);
        }
    }
}
