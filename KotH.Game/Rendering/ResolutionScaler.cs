using System;
using KotH.Client.Settings;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace KotH.Client.Rendering
{
    /// <summary>Borderless fullscreen at a lower Resolution: draws the whole game (scene, DLSS and HUD)
    /// at the chosen size into one target, then stretches it onto the back buffer. At native size, in
    /// exclusive fullscreen or windowed, it passes straight through with zero overhead.</summary>
    public sealed class ResolutionScaler : SceneRendererBase
    {
        /// <summary>The scene and HUD collection it wraps.</summary>
        public ISceneRenderer Child { get; set; }

        Texture color, depth;
        bool active;
        int targetWidth, targetHeight;

        /// <summary>Whether borderless scaling is active right now.</summary>
        public bool Active => active;

        /// <summary>For the F3 overlay when scaled without DLSS: "Scaled 1920x1080" or null.</summary>
        public string Status => active ? $"Scaled {targetWidth}x{targetHeight}" : null;

        /// <summary>The effective game resolution: in borderless fullscreen the chosen Resolution
        /// (clamped to the back buffer), otherwise the back buffer's own size.</summary>
        public static Int2 TargetSize(int width, int height)
        {
            PlayerSettings s = ClientSettings.Applied;
            if (s.Window != WindowMode.Borderless || s.Width <= 0 || s.Height <= 0 || s.Width >= width && s.Height >= height)
                return new Int2(width, height);
            return new Int2(Math.Min(s.Width, width), Math.Min(s.Height, height));
        }

        /// <summary>The effective game resolution as a Vector2, for HUD and menu overlays.</summary>
        public static Vector2 ScreenSize(Texture backBuffer)
        {
            Int2 size = TargetSize(backBuffer.Width, backBuffer.Height);
            return new Vector2(size.X, size.Y);
        }

        protected override void CollectCore(RenderContext context)
        {
            Viewport viewport = context.ViewportState.Viewport0;
            int width = Math.Max(1, (int)viewport.Width), height = Math.Max(1, (int)viewport.Height);
            Int2 size = TargetSize(width, height);
            active = size.X != width || size.Y != height;
            (targetWidth, targetHeight) = (size.X, size.Y);
            if (!active)
            {
                Child?.Collect(context);
                return;
            }
            using (context.SaveViewportAndRestore())
            {
                context.ViewportState.Viewport0 = new Viewport(0, 0, targetWidth, targetHeight);
                Child?.Collect(context);
            }
        }

        protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
        {
            if (!active)
            {
                Release();
                Child?.Draw(drawContext);
                return;
            }
            CommandList list = drawContext.CommandList;
            Texture back = list.RenderTarget, backDepth = list.DepthStencilBuffer;
            if (back == null)
                return;
            Prepare(drawContext.GraphicsDevice, back, backDepth);
            using (drawContext.PushRenderTargetsAndRestore())
            {
                list.SetRenderTargetAndViewport(depth, color);
                Child?.Draw(drawContext);
            }
            list.SetRenderTargetAndViewport(backDepth, back);
            drawContext.GraphicsContext.DrawTexture(color, BlendStates.Opaque);
        }

        void Prepare(GraphicsDevice device, Texture back, Texture backDepth)
        {
            if (color != null && (color.Width != targetWidth || color.Height != targetHeight || color.ViewFormat != back.ViewFormat))
                Release();
            color ??= Texture.New2D(device, targetWidth, targetHeight, back.ViewFormat, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            if (backDepth != null)
                depth ??= Texture.New2D(device, targetWidth, targetHeight, backDepth.ViewFormat, backDepth.Flags);
        }

        void Release()
        {
            color?.Dispose();
            depth?.Dispose();
            color = depth = null;
        }

        protected override void Destroy()
        {
            Release();
            base.Destroy();
        }
    }
}
