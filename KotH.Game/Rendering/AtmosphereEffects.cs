using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.Images;
using Stride.Rendering.Lights;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// The map's fog and clouds (<see cref="Atmosphere"/>) before Stride's post
    /// effects (<see cref="Inner"/>, FXAA): AtmosphereMarch along every view ray
    /// at half resolution, then AtmosphereComposite blends it over the colour at
    /// full resolution, in place. Without an Atmosphere in the scene it passes
    /// straight on.
    /// </summary>
    /// <userdoc>Nebel und Wolken der Map vor den übrigen Post-Effekten.</userdoc>
    [DataContract("AtmosphereEffects")]
    [Display("Atmosphäre und Post-Effekte")]
    public sealed class AtmosphereEffects : ImageEffect, IPostProcessingEffects
    {
        static readonly ValueParameterKey<Matrix> ClipToRay = ParameterKeys.NewValue<Matrix>(default, "AtmosphereMarch.ClipToRay");
        static readonly ValueParameterKey<Vector2> MarchNearFar = ParameterKeys.NewValue<Vector2>(default, "AtmosphereMarch.NearFar");
        static readonly ValueParameterKey<Vector3> Eye = ParameterKeys.NewValue<Vector3>(default, "AtmosphereMarch.Eye");
        static readonly ValueParameterKey<Vector3> ToSun = ParameterKeys.NewValue<Vector3>(default, "AtmosphereMarch.ToSun");
        static readonly ValueParameterKey<Vector3> SunColor = ParameterKeys.NewValue<Vector3>(default, "AtmosphereMarch.SunColor");
        static readonly ValueParameterKey<Vector4> FogHeights = ParameterKeys.NewValue<Vector4>(default, "AtmosphereMarch.FogHeights");
        static readonly ValueParameterKey<Vector4> FogShape = ParameterKeys.NewValue<Vector4>(default, "AtmosphereMarch.FogShape");
        static readonly ValueParameterKey<float> FogRadius = ParameterKeys.NewValue<float>(default, "AtmosphereMarch.FogRadius");
        static readonly ValueParameterKey<Vector3> FogColor = ParameterKeys.NewValue<Vector3>(default, "AtmosphereMarch.FogColor");
        static readonly ValueParameterKey<float> FogSun = ParameterKeys.NewValue<float>(default, "AtmosphereMarch.FogSun");
        static readonly ValueParameterKey<Vector4> Cloud = ParameterKeys.NewValue<Vector4>(default, "AtmosphereMarch.Cloud");
        static readonly ValueParameterKey<Vector4> CloudReach = ParameterKeys.NewValue<Vector4>(default, "AtmosphereMarch.CloudReach");
        static readonly ValueParameterKey<Vector2> CloudScale = ParameterKeys.NewValue<Vector2>(default, "AtmosphereMarch.CloudScale");
        static readonly ValueParameterKey<Vector4> CloudOffset = ParameterKeys.NewValue<Vector4>(default, "AtmosphereMarch.CloudOffset");
        static readonly ValueParameterKey<Vector2> Wind = ParameterKeys.NewValue<Vector2>(default, "AtmosphereMarch.Wind");
        static readonly ValueParameterKey<Vector3> CloudTopColor = ParameterKeys.NewValue<Vector3>(default, "AtmosphereMarch.CloudTopColor");
        static readonly ValueParameterKey<Vector3> CloudBottomColor = ParameterKeys.NewValue<Vector3>(default, "AtmosphereMarch.CloudBottomColor");
        static readonly ValueParameterKey<Vector2> NearFar = ParameterKeys.NewValue<Vector2>(default, "AtmosphereComposite.NearFar");

        [DataMember(-100), Display(Browsable = false)]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Stride's post effects, drawn after the atmosphere.</summary>
        /// <userdoc>Die übrigen Post-Effekte (FXAA), nach Nebel und Wolken.</userdoc>
        [DataMember(10)]
        public IPostProcessingEffects Inner { get; set; }

        public bool RequiresVelocityBuffer => Inner?.RequiresVelocityBuffer ?? false;
        public bool RequiresNormalBuffer => Inner?.RequiresNormalBuffer ?? false;
        public bool RequiresSpecularRoughnessBuffer => Inner?.RequiresSpecularRoughnessBuffer ?? false;

        ImageEffectShader march;
        ImageEffectShader composite;

        protected override void InitializeCore()
        {
            base.InitializeCore();
            march = ToLoadAndUnload(new ImageEffectShader("AtmosphereMarch"));
            composite = ToLoadAndUnload(new ImageEffectShader("AtmosphereComposite"));
            // scene * a + rgb
            var blend = new BlendStateDescription(Blend.One, Blend.SourceAlpha);
            blend.RenderTargets[0].AlphaSourceBlend = Blend.Zero;
            blend.RenderTargets[0].AlphaDestinationBlend = Blend.One;
            composite.BlendState = blend;
        }

        public void Collect(RenderContext context) => Inner?.Collect(context);

        public void Draw(RenderDrawContext drawContext, RenderOutputValidator outputValidator, Span<Texture> inputs, Texture inputDepthStencil, Texture outputTarget)
        {
            if (!Initialized)
                Initialize(drawContext.RenderContext);
            int colour = outputValidator.Find<ColorTargetSemantic>();
            var atmosphere = drawContext.RenderContext.Tags.Get(Atmosphere.Key);
            if (colour >= 0 && inputDepthStencil != null && atmosphere is { CloudMap: not null })
                DrawAtmosphere(drawContext, atmosphere, inputs[colour], inputDepthStencil);
            Inner?.Draw(drawContext, outputValidator, inputs, inputDepthStencil, outputTarget);
        }

        void DrawAtmosphere(RenderDrawContext drawContext, Atmosphere atmosphere, Texture colour, Texture depth)
        {
            RenderView view = drawContext.RenderContext.RenderView;
            int width = Math.Max(1, colour.Width / 2), height = Math.Max(1, colour.Height / 2);
            var half = Context.Allocator.GetTemporaryTexture2D(width, height, PixelFormat.R16G16B16A16_Float,
                TextureFlags.RenderTarget | TextureFlags.ShaderResource);

            // The sun: the strongest directional light; straight up if there is none.
            var toSun = Vector3.UnitY;
            var sunColor = Vector3.One;
            foreach (RenderLight light in SceneInstance.GetCurrent(drawContext.RenderContext)?.GetProcessor<LightProcessor>()?.Lights ?? [])
                if (light.Type is LightDirectional)
                {
                    toSun = -Vector3.Normalize(light.Direction);
                    sunColor = (Vector3)light.Color;
                }

            // Each pixel's ray from the camera's axes and the projection's scale. Inverting the
            // whole view-projection (far 20 km) in float tilted all rays by up to a pixel, anew
            // every frame, and the thin layer seen from above shook with it (Luca: "Zittern").
            Matrix v = view.View, proj = view.Projection;
            var right = new Vector3(v.M11, v.M21, v.M31) / proj.M11;
            var up = new Vector3(v.M12, v.M22, v.M32) / proj.M22;
            var ahead = right * proj.M31 + up * proj.M32 - new Vector3(v.M13, v.M23, v.M33);
            var clipToRay = new Matrix(right.X, right.Y, right.Z, 0f, up.X, up.Y, up.Z, 0f,
                                       ahead.X, ahead.Y, ahead.Z, 0f, 0f, 0f, 0f, 1f);
            Matrix viewToWorld = Matrix.Invert(view.View);
            float texel = atmosphere.CloudTile / atmosphere.CloudMap.Width;
            // Metres one half-resolution pixel spans per metre away, over a texel's width: the cloud map's lod.
            float pixel = 2f / (view.Projection.M22 * height);
            var p = march.Parameters;
            p.Set(ClipToRay, clipToRay);
            p.Set(MarchNearFar, new Vector2(view.NearClipPlane, view.FarClipPlane));
            p.Set(Eye, viewToWorld.TranslationVector);
            p.Set(ToSun, toSun);
            p.Set(SunColor, sunColor);
            p.Set(FogHeights, new Vector4(atmosphere.FogStart, atmosphere.FogPeak, atmosphere.FogTop, atmosphere.FogDistance));
            p.Set(FogShape, new Vector4(atmosphere.FogDensity, atmosphere.FogBase, atmosphere.FogCentre.X, atmosphere.FogCentre.Y));
            p.Set(FogRadius, atmosphere.FogRadius);
            p.Set(FogColor, (Vector3)atmosphere.FogColor);
            p.Set(FogSun, atmosphere.FogSun);
            p.Set(Cloud, new Vector4(atmosphere.CloudBase, atmosphere.CloudThickness, atmosphere.CloudCoverage, atmosphere.CloudDensity));
            p.Set(CloudReach, new Vector4(atmosphere.CloudDistance, pixel / texel, MathF.Log2(atmosphere.BandTile / atmosphere.CloudTile), 0f));
            p.Set(CloudScale, new Vector2(1f / atmosphere.CloudTile, 1f / atmosphere.BandTile));
            p.Set(CloudOffset, atmosphere.CloudOffset);
            p.Set(Wind, atmosphere.WindAxis);
            p.Set(CloudTopColor, (Vector3)atmosphere.CloudTop);
            p.Set(CloudBottomColor, (Vector3)atmosphere.CloudBottom);

            using (drawContext.PushRenderTargetsAndRestore())
            {
                march.SetInput(0, depth);
                march.SetInput(1, atmosphere.CloudMap);
                march.SetOutput(half);
                march.Draw(drawContext);

                composite.Parameters.Set(NearFar, new Vector2(view.NearClipPlane, view.FarClipPlane));
                composite.SetInput(0, half);
                composite.SetInput(1, depth);
                composite.SetOutput(colour);
                composite.Draw(drawContext);
            }
            Context.Allocator.ReleaseReference(half);
        }

        protected override void DrawCore(RenderDrawContext context)
        {
        }

        protected override void Destroy()
        {
            (Inner as IDisposable)?.Dispose();
            base.Destroy();
        }
    }
}
