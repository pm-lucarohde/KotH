using System;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.Images;

namespace KotH.Client.Rendering.Dlss
{
    /// <summary>While DLSS is Active: the scene at DLSS's render size with a jittered projection, the
    /// atmosphere still at that size, then DLSS (DlssUpscale, in FXAA's place) to the output size and
    /// DlssSharpen onto the target; the HUD draws after, at full size. Off, it only passes through.</summary>
    public sealed class DlssRenderer : SceneRendererBase
    {
        /// <summary>The scene renderer it wraps (the compositor's game renderer).</summary>
        public ISceneRenderer Child { get; set; }

        static readonly ValueParameterKey<float> SharpnessKey = ParameterKeys.NewValue(0.5f, "DlssSharpen.Sharpness");

        SceneCameraRenderer view;
        AtmosphereEffects atmosphere;
        IPostProcessingEffects fxaa;
        DlssUpscale upscale;
        DlssFeature feature;
        ImageEffectShader sharpen;
        Texture lowColor, lowDepth, output;
        bool active, reset = true;
        int outputWidth, outputHeight, renderWidth, renderHeight, frame;
        DlssMode sizedFor;

        // This frame's camera (unjittered projection) and the last one's, for the motion vectors.
        Matrix viewMatrix, projection, previousView, previousProjection;
        bool hasPrevious;

        /// <summary>The jitter of this frame in render pixels, x right and y down.</summary>
        internal Vector2 Jitter { get; private set; }

        /// <summary>Whether frames go through DLSS now (decided in Collect).</summary>
        internal bool Active => active;

        /// <summary>The unjittered camera now and one frame ago (now again after a cut).</summary>
        internal void Clips(out Matrix now, out Matrix before)
        {
            now = viewMatrix * projection;
            before = hasPrevious ? previousView * previousProjection : now;
        }

        /// <summary>The camera pass's opaque stage, the only one that writes velocity.</summary>
        internal RenderStage OpaqueStage { get; private set; }

        internal DlssFeature Feature => feature;
        internal Texture Output => output;
        internal bool Reset => reset;
        internal bool Evaluated { get; set; }

        /// <summary>For the F3 overlay: "Quality 1707x960" while active, else null.</summary>
        public string Status => active ? $"{Dlss.Mode} {renderWidth}x{renderHeight}" : null;

        /// <summary>Starts DLSS's history anew (a cut: map change, teleport, other camera).</summary>
        public void Restart()
        {
            reset = true;
            hasPrevious = false;
        }

        protected override void InitializeCore()
        {
            base.InitializeCore();
            sharpen = new ImageEffectShader("DlssSharpen");
            sharpen.Initialize(Context);
            upscale = new DlssUpscale(this);
            DlssVelocityFeature.Renderer = this;
        }

        // The game's camera pass and its post effects; found once, the compositor keeps them.
        bool Find()
        {
            view ??= SceneThrottle.FindView(Child);
            ForwardRenderer forward = SceneThrottle.FindForward(view?.Child);
            atmosphere ??= forward?.PostEffects as AtmosphereEffects;
            OpaqueStage ??= forward?.OpaqueRenderStage;
            if (atmosphere != null && fxaa == null && atmosphere.Inner is not DlssUpscale)
                fxaa = atmosphere.Inner;
            return view != null && atmosphere != null;
        }

        protected override void CollectCore(RenderContext context)
        {
            bool wanted = Dlss.Active && Find();
            if (wanted != active)
            {
                active = wanted;
                if (atmosphere != null)
                    atmosphere.Inner = active ? upscale : fxaa;
                Restart();
            }
            if (!active)
            {
                Child?.Collect(context);
                return;
            }

            Viewport viewport = context.ViewportState.Viewport0;
            int width = Math.Max(1, (int)viewport.Width), height = Math.Max(1, (int)viewport.Height);
            if (width != outputWidth || height != outputHeight || sizedFor != Dlss.Mode)
            {
                Dlss.RenderSize(Dlss.QualityOf(Dlss.Mode), width, height, out renderWidth, out renderHeight);
                (outputWidth, outputHeight, sizedFor) = (width, height, Dlss.Mode);
                Restart();
            }

            using (context.SaveViewportAndRestore())
            {
                context.ViewportState.Viewport0 = new Viewport(0, 0, renderWidth, renderHeight);
                Child?.Collect(context);
            }

            // Halton(2,3), as many phases as DLSS wants for this scale (8 per output pixel per render pixel).
            RenderView renderView = view.RenderView;
            float scale = outputWidth / (float)renderWidth;
            int phases = Math.Max(8, (int)MathF.Ceiling(8f * scale * scale));
            int i = frame++ % phases + 1;
            var jitter = new Vector2(Halton(i, 2) - 0.5f, Halton(i, 3) - 0.5f);
            Jitter = jitter;

            viewMatrix = renderView.View;
            projection = renderView.Projection;
            if (hasPrevious && Cut(previousView, viewMatrix))
                Restart();
            Matrix jittered = projection;
            jittered.M31 += 2f * jitter.X / renderWidth * projection.M34;
            jittered.M32 -= 2f * jitter.Y / renderHeight * projection.M34;
            renderView.Projection = jittered;
            renderView.ViewProjection = renderView.View * jittered;
        }

        // A cut: the camera jumped (respawn, teleport, third person, menu, map) or turned
        // by more than 45° in one frame; the last frames' pixels would only smear.
        const float CutMetres = 2f, CutCosine = 0.7071f;

        static bool Cut(Matrix before, Matrix now)
        {
            Matrix a = Matrix.Invert(before), b = Matrix.Invert(now);
            var forwardA = new Vector3(-a.M31, -a.M32, -a.M33);
            var forwardB = new Vector3(-b.M31, -b.M32, -b.M33);
            return Vector3.Distance(a.TranslationVector, b.TranslationVector) > CutMetres
                   || Vector3.Dot(Vector3.Normalize(forwardA), Vector3.Normalize(forwardB)) < CutCosine;
        }

        static float Halton(int index, int radix)
        {
            float result = 0f, fraction = 1f / radix;
            for (; index > 0; index /= radix, fraction /= radix)
                result += fraction * (index % radix);
            return result;
        }

        /// <summary>DlssMotion's matrices: from this frame's jittered clip space back to view space,
        /// and from view space to the unjittered clip space now and one frame ago.</summary>
        internal (Matrix ClipToView, Matrix ViewToClip, Matrix ViewToPrevClip) MotionMatrices(Matrix jitteredProjection)
        {
            Matrix toPrevious = hasPrevious ? Matrix.Invert(viewMatrix) * previousView * previousProjection : projection;
            return (Matrix.Invert(jitteredProjection), projection, toPrevious);
        }

        protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
        {
            if (!active)
            {
                Child?.Draw(drawContext);
                return;
            }
            CommandList list = drawContext.CommandList;
            Texture target = list.RenderTarget, targetDepth = list.DepthStencilBuffer;
            if (target == null)
                return;
            Prepare(drawContext.GraphicsDevice, target, targetDepth);

            Evaluated = false;
            using (drawContext.PushRenderTargetsAndRestore())
            {
                list.SetRenderTargetAndViewport(lowDepth, lowColor);
                Child?.Draw(drawContext);
            }
            list.SetRenderTargetAndViewport(targetDepth, target);
            if (Evaluated)
            {
                sharpen.Parameters.Set(SharpnessKey, Dlss.Sharpness);
                sharpen.SetInput(0, output);
                sharpen.SetOutput(target);
                sharpen.Draw(drawContext);
                list.SetRenderTargetAndViewport(targetDepth, target);
                reset = false;
            }
            else
                drawContext.GraphicsContext.DrawTexture(lowColor, BlendStates.Opaque);

            (previousView, previousProjection, hasPrevious) = (viewMatrix, projection, true);
        }

        // The render-size targets (formats as the target's, so the compositor's output still fits),
        // the output-size picture DLSS writes, and the DLSS instance; all made again on a new size.
        void Prepare(GraphicsDevice device, Texture target, Texture targetDepth)
        {
            if (lowColor != null && (lowColor.Width != renderWidth || lowColor.Height != renderHeight || lowColor.ViewFormat != target.ViewFormat))
                Release();
            if (output != null && (output.Width != outputWidth || output.Height != outputHeight))
            {
                output.Dispose();
                output = null;
            }
            lowColor ??= Texture.New2D(device, renderWidth, renderHeight, target.ViewFormat, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            lowDepth ??= Texture.New2D(device, renderWidth, renderHeight, targetDepth?.ViewFormat ?? PixelFormat.D24_UNorm_S8_UInt,
                                       TextureFlags.DepthStencil | TextureFlags.ShaderResource);
            output ??= Texture.New2D(device, outputWidth, outputHeight, PixelFormat.R16G16B16A16_Float,
                                     TextureFlags.RenderTarget | TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
            feature ??= new DlssFeature(device);
        }

        void Release()
        {
            lowColor?.Dispose();
            lowDepth?.Dispose();
            lowColor = lowDepth = null;
        }

        protected override void Destroy()
        {
            if (atmosphere != null && fxaa != null)
                atmosphere.Inner = fxaa;
            Release();
            output?.Dispose();
            feature?.Dispose();
            sharpen?.Dispose();
            upscale?.Dispose();
            base.Destroy();
        }
    }
}
