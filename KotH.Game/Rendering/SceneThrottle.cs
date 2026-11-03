using System.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace KotH.Client.Rendering
{
    /// <summary>Draws the scene at most once per <see cref="Interval"/> into its own picture that
    /// every frame draws over the back buffer; the HUD still draws every frame. The main menu runs
    /// so (Luca: map at 30 fps, buttons free); HudRenderer.Of wraps the compositor's scene. An overlay
    /// group (the menu figure) is drawn every frame on top, by its own camera pass.</summary>
    public sealed class SceneThrottle : SceneRendererBase
    {
        /// <summary>The scene renderer it wraps.</summary>
        public ISceneRenderer Child { get; set; }

        /// <summary>Seconds between two scene frames; 0 draws the scene every frame, straight into the back buffer.</summary>
        public double Interval { get; set; }

        Texture color, depth;
        long next;
        bool due;

        // The overlay: one more pass of the scene's camera over one render group only,
        // 4x MSAA into its own picture, blended over the scene's every frame.
        SceneCameraRenderer overlay;
        Texture overlayColor, overlayDepth;

        /// <summary>Draws group every frame by its own pass, and no longer with the scene.</summary>
        public void ShowOverlay(RenderGroup group)
        {
            SceneCameraRenderer main = FindView(Child);
            ForwardRenderer forward = FindForward(main?.Child);
            if (main == null || forward == null)
                return;
            var mask = (RenderGroupMask)(1u << (int)group);
            main.RenderMask &= ~mask;
            var pass = new ForwardRenderer
            {
                Clear = new ClearRenderer { Color = new Color4(0f, 0f, 0f, 0f) },
                OpaqueRenderStage = forward.OpaqueRenderStage,
                TransparentRenderStage = forward.TransparentRenderStage,
                LightProbes = false,
                MSAALevel = MultisampleCount.X4,
            };
            pass.ShadowMapRenderStages.AddRange(forward.ShadowMapRenderStages);
            overlay = new SceneCameraRenderer { Camera = main.Camera, RenderMask = mask, Child = pass };
        }

        /// <summary>Ends the overlay pass; its group stays out of the scene's view.</summary>
        public void HideOverlay()
        {
            overlay?.Dispose();
            overlay = null;
            overlayColor?.Dispose();
            overlayDepth?.Dispose();
            overlayColor = overlayDepth = null;
        }

        static SceneCameraRenderer FindView(ISceneRenderer renderer) => renderer switch
        {
            SceneCameraRenderer camera => camera,
            SceneRendererCollection collection => Find(collection, FindView),
            _ => null,
        };

        static ForwardRenderer FindForward(ISceneRenderer renderer) => renderer switch
        {
            ForwardRenderer forward => forward,
            SceneRendererCollection collection => Find(collection, FindForward),
            _ => null,
        };

        static T Find<T>(SceneRendererCollection collection, System.Func<ISceneRenderer, T> find) where T : class
        {
            foreach (ISceneRenderer child in collection.Children)
                if (find(child) is { } found)
                    return found;
            return null;
        }

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
            overlay?.Collect(context);
        }

        protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
        {
            if (Interval <= 0.0)
            {
                Release();
                if (due)
                    Child?.Draw(drawContext);
                DrawOverlay(drawContext);
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
            DrawOverlay(drawContext);
        }

        // Cleared transparent and resolved, so the picture is premultiplied: edges blend soft.
        void DrawOverlay(RenderDrawContext drawContext)
        {
            if (overlay == null)
                return;
            GraphicsPresenter presenter = drawContext.GraphicsDevice.Presenter;
            Texture back = presenter.BackBuffer, backDepth = presenter.DepthStencilBuffer;
            if (overlayColor != null && (overlayColor.Width != back.Width || overlayColor.Height != back.Height))
            {
                overlayColor.Dispose();
                overlayDepth?.Dispose();
                overlayColor = overlayDepth = null;
            }
            if (overlayColor == null)
            {
                overlayColor = Texture.New2D(drawContext.GraphicsDevice, back.Width, back.Height, back.ViewFormat,
                                             TextureFlags.RenderTarget | TextureFlags.ShaderResource);
                overlayDepth = Texture.New2D(drawContext.GraphicsDevice, back.Width, back.Height,
                                             PixelFormat.D24_UNorm_S8_UInt, TextureFlags.DepthStencil);
            }
            using (drawContext.PushRenderTargetsAndRestore())
            {
                drawContext.CommandList.SetRenderTargetAndViewport(overlayDepth, overlayColor);
                overlay.Draw(drawContext);
            }
            drawContext.CommandList.SetRenderTargetAndViewport(backDepth, back);
            drawContext.GraphicsContext.DrawTexture(overlayColor, BlendStates.AlphaBlend);
        }

        protected override void Destroy()
        {
            Release();
            HideOverlay();
            base.Destroy();
        }
    }
}
