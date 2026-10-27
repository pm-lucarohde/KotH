using System.Diagnostics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// Draws the scene at most once per <see cref="Interval"/>, into a picture of
    /// its own that every frame draws over the back buffer (the swap chain
    /// keeps no frame); the HUD after it still draws every frame. The main menu runs
    /// so (Luca): the map behind it at 30 fps, the buttons free. HudRenderer.Of
    /// puts it round the compositor's scene and registers it as a service.
    /// </summary>
    public sealed class SceneThrottle : SceneRendererBase
    {
        /// <summary>The scene renderer it wraps.</summary>
        public ISceneRenderer Child { get; set; }

        /// <summary>Seconds between two scene frames; 0 draws the scene every frame, straight into the back buffer.</summary>
        public double Interval { get; set; }

        Texture color, depth;
        long next;
        bool due;

        /// <summary>Frees the picture; the next limited frame draws the scene anew.</summary>
        public void Release()
        {
            color?.Dispose();
            depth?.Dispose();
            color = depth = null;
        }

        protected override void CollectCore(RenderContext context)
        {
            due = true;
            if (Interval > 0.0)
            {
                Texture back = context.GraphicsDevice.Presenter.BackBuffer;
                if (color != null && (color.Width != back.Width || color.Height != back.Height))
                    Release();
                // Due on time or later; behind by more than a frame, it counts on from now.
                long now = Stopwatch.GetTimestamp(), step = (long)(Interval * Stopwatch.Frequency);
                due = color == null || now >= next;
                if (due)
                    next = now - next > step ? now + step : next + step;
            }
            if (due)
                Child?.Collect(context);
        }

        protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
        {
            if (Interval <= 0.0)
            {
                Release();
                if (due)
                    Child?.Draw(drawContext);
                return;
            }

            GraphicsPresenter presenter = drawContext.GraphicsDevice.Presenter;
            Texture back = presenter.BackBuffer, backDepth = presenter.DepthStencilBuffer;
            if (color == null)
            {
                // Same formats as the back buffer, so the compositor's render output still fits;
                // the depth readable like the presenter's, the atmosphere samples it.
                color = Texture.New2D(drawContext.GraphicsDevice, back.Width, back.Height, back.ViewFormat,
                                      TextureFlags.RenderTarget | TextureFlags.ShaderResource);
                if (backDepth != null)
                    depth = Texture.New2D(drawContext.GraphicsDevice, back.Width, back.Height, backDepth.ViewFormat,
                                          backDepth.Flags);
            }

            if (due && Child != null)
                using (drawContext.PushRenderTargetsAndRestore())
                {
                    drawContext.CommandList.SetRenderTargetAndViewport(depth, color);
                    Child.Draw(drawContext);
                }
            drawContext.CommandList.SetRenderTargetAndViewport(backDepth, back);
            drawContext.GraphicsContext.DrawTexture(color, BlendStates.Opaque);
        }

        protected override void Destroy()
        {
            Release();
            base.Destroy();
        }
    }
}
