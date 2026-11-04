using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.Images;

namespace KotH.Client.Rendering.Dlss
{
    /// <summary>In FXAA's place behind the atmosphere while DLSS runs: motion vectors (DlssMotion, from
    /// the objects' velocity and, for the sky, depth), then DLSS from the render-size HDR colour into
    /// DlssRenderer's output picture. The render-size target stays as it is; DlssRenderer draws the output.</summary>
    [DataContract("DlssUpscale")]
    public sealed class DlssUpscale : ImageEffect, IPostProcessingEffects
    {
        static readonly ValueParameterKey<Matrix> ClipToView = ParameterKeys.NewValue<Matrix>(default, "DlssMotion.ClipToView");
        static readonly ValueParameterKey<Matrix> ViewToClip = ParameterKeys.NewValue<Matrix>(default, "DlssMotion.ViewToClip");
        static readonly ValueParameterKey<Matrix> ViewToPrevClip = ParameterKeys.NewValue<Matrix>(default, "DlssMotion.ViewToPrevClip");

        // NGX's own pass, timed for KOTH_GPULOG.
        static readonly Stride.Core.Diagnostics.ProfilingKey EvaluateKey = new Stride.Core.Diagnostics.ProfilingKey("DlssEvaluate");

        readonly DlssRenderer owner;
        ImageEffectShader motion;

        public DlssUpscale() { }

        internal DlssUpscale(DlssRenderer owner) => this.owner = owner;

        [DataMember(-100), Display(Browsable = false)]
        public Guid Id { get; set; } = Guid.NewGuid();

        public bool RequiresVelocityBuffer => true;
        public bool RequiresNormalBuffer => false;
        public bool RequiresSpecularRoughnessBuffer => false;

        protected override void InitializeCore()
        {
            base.InitializeCore();
            motion = ToLoadAndUnload(new ImageEffectShader("DlssMotion"));
        }

        public void Collect(RenderContext context)
        {
        }

        public void Draw(RenderDrawContext drawContext, RenderOutputValidator outputValidator, Span<Texture> inputs, Texture inputDepthStencil, Texture outputTarget)
        {
            if (!Initialized)
                Initialize(drawContext.RenderContext);
            int index = outputValidator.Find<ColorTargetSemantic>();
            int velocity = outputValidator.Find<VelocityTargetSemantic>();
            DlssFeature feature = owner?.Feature;
            if (index < 0 || velocity < 0 || inputDepthStencil == null || feature == null || owner.Output == null)
                return;
            Texture color = inputs[index];
            Texture output = owner.Output;
            if (!feature.Fits(color.Width, color.Height, output.Width, output.Height, Dlss.Mode)
                && !feature.Create(drawContext.CommandList, color.Width, color.Height, output.Width, output.Height, Dlss.Mode))
                return;

            RenderView view = drawContext.RenderContext.RenderView;
            var (clipToView, viewToClip, viewToPrevClip) = owner.MotionMatrices(view.Projection);
            Texture vectors = Context.Allocator.GetTemporaryTexture2D(color.Width, color.Height, PixelFormat.R16G16_Float,
                TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            using (drawContext.PushRenderTargetsAndRestore())
            {
                motion.Parameters.Set(ClipToView, clipToView);
                motion.Parameters.Set(ViewToClip, viewToClip);
                motion.Parameters.Set(ViewToPrevClip, viewToPrevClip);
                motion.SetInput(0, inputDepthStencil);
                motion.SetInput(1, inputs[velocity]);
                motion.SetOutput(vectors);
                motion.Draw(drawContext);
            }

            Vector2 jitter = owner.Jitter * Dlss.JitterSign;
            float ms = (float)drawContext.RenderContext.Time.Elapsed.TotalMilliseconds;
            using (drawContext.QueryManager.BeginProfile(Color.Green, EvaluateKey))
                owner.Evaluated = feature.Evaluate(drawContext.CommandList, color, inputDepthStencil, vectors, output,
                                                   jitter.X, jitter.Y, owner.Reset, ms);
            Context.Allocator.ReleaseReference(vectors);
        }

        protected override void DrawCore(RenderDrawContext context)
        {
        }
    }
}
