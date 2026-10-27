using System.Collections.Generic;
using System.Globalization;
using KotH.Client.Rendering;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace KotH.Client.Hud
{
    /// <summary>Something the HUD draws in screen pixels, after the scene.</summary>
    public interface IHudElement
    {
        /// <summary>Draws into batch; screen is the back buffer size in pixels.</summary>
        void Draw(SpriteBatch batch, Vector2 screen);
    }

    /// <summary>Drawn alone over the whole screen while it covers it, such as while loading.</summary>
    public interface IHudCover : IHudElement
    {
        bool Covers { get; }
    }

    /// <summary>
    /// Draws the screen HUD with one sprite batch after the scene, in exactly
    /// the order elements were added and without depth. Stride's UI orders
    /// overlapping parts by a depth bias whose sign flips under Vulkan, so
    /// an outline or a text band ends up on top of what it should back.
    /// Sizes are pixels, like Unity's overlay canvas without a scaler.
    /// </summary>
    public sealed class HudRenderer : SceneRendererBase
    {
        /// <summary>
        /// The German number format the Unity build showed through its
        /// system culture, made explicit so the client does not depend on
        /// the machine's locale or on invariant globalisation.
        /// </summary>
        public static readonly NumberFormatInfo Numbers = GermanNumbers();

        readonly List<IHudElement> elements = new List<IHudElement>();
        SpriteBatch batch;
        Texture white;

        /// <summary>One opaque white pixel, for filled rectangles.</summary>
        public Texture White => white;

        /// <summary>The scene's HUD, appended to the compositor on first use, the scene wrapped in a SceneThrottle.</summary>
        public static HudRenderer Of(ScriptComponent script)
        {
            var hud = script.Services.GetService<HudRenderer>();
            if (hud != null)
                return hud;

            hud = new HudRenderer();
            script.Services.AddService(hud);

            GraphicsCompositor compositor = script.SceneSystem.GraphicsCompositor;
            var throttle = new SceneThrottle { Child = compositor.Game };
            script.Services.AddService(throttle);
            compositor.Game = new SceneRendererCollection { throttle, hud };

            hud.white = Texture.New2D(script.GraphicsDevice, 1, 1, PixelFormat.R8G8B8A8_UNorm,
                                      new byte[] { 255, 255, 255, 255 });
            return hud;
        }

        /// <summary>While it covers the screen, nothing else of the HUD is drawn.</summary>
        public IHudCover Cover { get; set; }

        public void Add(IHudElement element) => elements.Add(element);
        public void Remove(IHudElement element) => elements.Remove(element);

        protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
        {
            bool covered = Cover is { Covers: true };
            if (elements.Count == 0 && !covered)
                return;

            batch ??= new SpriteBatch(drawContext.GraphicsDevice);

            Texture target = drawContext.GraphicsDevice.Presenter.BackBuffer;
            drawContext.CommandList.SetRenderTargetAndViewport(null, target);
            var screen = new Vector2(target.Width, target.Height);

            batch.Begin(drawContext.GraphicsContext, SpriteSortMode.Deferred, BlendStates.AlphaBlend,
                        drawContext.GraphicsDevice.SamplerStates.LinearClamp, DepthStencilStates.None);
            if (covered)
                Cover.Draw(batch, screen);
            else
                for (int i = 0; i < elements.Count; i++)
                    elements[i].Draw(batch, screen);
            batch.End();
        }

        protected override void Destroy()
        {
            batch?.Dispose();
            white?.Dispose();
            base.Destroy();
        }

        static NumberFormatInfo GermanNumbers()
        {
            var format = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
            format.NumberDecimalSeparator = ",";
            format.NumberGroupSeparator = ".";
            return format;
        }
    }
}
