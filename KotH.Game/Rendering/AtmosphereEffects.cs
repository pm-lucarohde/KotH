using System;
using KotH.Client.Settings;
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
    /// <summary>The map's fog, clouds and smoke (<see cref="Atmosphere"/>) before Stride's post effects
    /// (<see cref="Inner"/>, FXAA): AtmosphereMarch at half resolution, AtmosphereComposite in
    /// place at full; without an Atmosphere it passes straight on.</summary>
    /// <userdoc>The map's fog and clouds before the other post effects.</userdoc>
    [DataContract("AtmosphereEffects")]
    [Display("Atmosphere and post effects")]
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
        static readonly ValueParameterKey<Vector4> CoastHeights = ParameterKeys.NewValue<Vector4>(default, "AtmosphereMarch.CoastHeights");
        static readonly ValueParameterKey<Vector4> CoastRing = ParameterKeys.NewValue<Vector4>(default, "AtmosphereMarch.CoastRing");
        static readonly ValueParameterKey<Vector4> CoastShape = ParameterKeys.NewValue<Vector4>(default, "AtmosphereMarch.CoastShape");
        static readonly ValueParameterKey<float> CoastOffset = ParameterKeys.NewValue<float>(default, "AtmosphereMarch.CoastOffset");
        static readonly ValueParameterKey<Vector3> CoastColor = ParameterKeys.NewValue<Vector3>(default, "AtmosphereMarch.CoastColor");
        static readonly ValueParameterKey<Vector4> Smoke = ParameterKeys.NewValue<Vector4>(default, "AtmosphereMarch.Smoke");
        static readonly ValueParameterKey<Vector4> SmokeShape = ParameterKeys.NewValue<Vector4>(default, "AtmosphereMarch.SmokeShape");
        static readonly ValueParameterKey<Vector4> SmokeRing = ParameterKeys.NewValue<Vector4>(default, "AtmosphereMarch.SmokeRing");
        static readonly ValueParameterKey<Vector4> SmokeMotion = ParameterKeys.NewValue<Vector4>(default, "AtmosphereMarch.SmokeMotion");
        static readonly ValueParameterKey<Vector3> SmokeLitColor = ParameterKeys.NewValue<Vector3>(default, "AtmosphereMarch.SmokeLitColor");
        static readonly ValueParameterKey<Vector3> SmokeShadeColor = ParameterKeys.NewValue<Vector3>(default, "AtmosphereMarch.SmokeShadeColor");
        static readonly ValueParameterKey<Vector3> SmokeGlowColor = ParameterKeys.NewValue<Vector3>(default, "AtmosphereMarch.SmokeGlowColor");
        static readonly ValueParameterKey<Vector2> NearFar = ParameterKeys.NewValue<Vector2>(default, "AtmosphereComposite.NearFar");
        static readonly ValueParameterKey<float> MarchDivisor = ParameterKeys.NewValue<float>(2f, "AtmosphereMarch.Divisor");
        static readonly ValueParameterKey<float> CompositeDivisor = ParameterKeys.NewValue<float>(2f, "AtmosphereComposite.Divisor");
        static readonly ValueParameterKey<Vector2> MaskSunUV = ParameterKeys.NewValue<Vector2>(default, "SunShaftMask.SunUV");
        static readonly ValueParameterKey<Vector2> MaskShape = ParameterKeys.NewValue<Vector2>(default, "SunShaftMask.MaskShape");
        static readonly ValueParameterKey<Vector2> BlurSunUV = ParameterKeys.NewValue<Vector2>(default, "SunShaftBlur.SunUV");
        static readonly ValueParameterKey<Vector2> BlurShape = ParameterKeys.NewValue<Vector2>(default, "SunShaftBlur.BlurShape");
        static readonly ValueParameterKey<Vector3> Tint = ParameterKeys.NewValue<Vector3>(default, "SunShaftAdd.Tint");
        static readonly ValueParameterKey<Matrix> HazeClipToRay = ParameterKeys.NewValue<Matrix>(default, "HeatShimmer.ClipToRay");
        static readonly ValueParameterKey<Vector2> HazeNearFar = ParameterKeys.NewValue<Vector2>(default, "HeatShimmer.NearFar");
        static readonly ValueParameterKey<Vector3> HazeEye = ParameterKeys.NewValue<Vector3>(default, "HeatShimmer.Eye");
        static readonly ValueParameterKey<Vector4> Haze = ParameterKeys.NewValue<Vector4>(default, "HeatShimmer.Haze");
        static readonly ValueParameterKey<Vector4> HazeShape = ParameterKeys.NewValue<Vector4>(default, "HeatShimmer.Shape");

        [DataMember(-100), Display(Browsable = false)]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Stride's post effects, drawn after the atmosphere.</summary>
        /// <userdoc>The other post effects (FXAA), after fog and clouds.</userdoc>
        [DataMember(10)]
        public IPostProcessingEffects Inner { get; set; }

        public bool RequiresVelocityBuffer => Inner?.RequiresVelocityBuffer ?? false;
        public bool RequiresNormalBuffer => Inner?.RequiresNormalBuffer ?? false;
        public bool RequiresSpecularRoughnessBuffer => Inner?.RequiresSpecularRoughnessBuffer ?? false;

        // Screen rays: below this cosine between the view and the sun they are gone (and not
        // drawn); the share of the way to the sun they reach, the radius round it that
        // shines (screen heights) and the fall-off per step (32 steps).
        const float RayFacing = 0.3f, RayReach = 0.9f, RayRadius = 0.6f, RayDecay = 0.955f;

        ImageEffectShader march;
        ImageEffectShader composite;
        ImageEffectShader shaftMask;
        ImageEffectShader shaftBlur;
        ImageEffectShader shaftAdd;
        ImageEffectShader shimmer;
        ImageEffectShader compress;
        ImageEffectShader resolve;

        protected override void InitializeCore()
        {
            base.InitializeCore();
            compress = ToLoadAndUnload(new ImageEffectShader("RangeCompressorShader"));
            resolve = ToLoadAndUnload(new ImageEffectShader("FxaaResolveEffect"));
            march = ToLoadAndUnload(new ImageEffectShader("AtmosphereMarch"));
            composite = ToLoadAndUnload(new ImageEffectShader("AtmosphereComposite"));
            // scene * a + rgb
            var blend = new BlendStateDescription(Blend.One, Blend.SourceAlpha);
            blend.RenderTargets[0].AlphaSourceBlend = Blend.Zero;
            blend.RenderTargets[0].AlphaDestinationBlend = Blend.One;
            composite.BlendState = blend;
            shaftMask = ToLoadAndUnload(new ImageEffectShader("SunShaftMask"));
            shaftBlur = ToLoadAndUnload(new ImageEffectShader("SunShaftBlur"));
            shaftAdd = ToLoadAndUnload(new ImageEffectShader("SunShaftAdd"));
            shimmer = ToLoadAndUnload(new ImageEffectShader("HeatShimmer"));
            // scene + rgb
            var add = new BlendStateDescription(Blend.One, Blend.One);
            add.RenderTargets[0].AlphaSourceBlend = Blend.Zero;
            add.RenderTargets[0].AlphaDestinationBlend = Blend.One;
            shaftAdd.BlendState = add;
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
            if (colour >= 0 && FxaaOnly(inputs[colour], outputTarget))
                DrawFxaa(drawContext, inputs[colour], outputTarget);
            else
                Inner?.Draw(drawContext, outputValidator, inputs, inputDepthStencil, outputTarget);
        }

        // Whether Stride's post effects would only run its FXAA path: range compress, FXAA, decompress and
        // the colour group writing luma to alpha (four full-screen passes, DrawFxaa does the same in two).
        bool FxaaOnly(Texture colour, Texture output)
        {
            if (Inner is not PostProcessingEffects post || !post.Enabled || output == null || colour == output
                || colour.Format != PixelFormat.R16G16B16A16_Float || colour.Width != output.Width || colour.Height != output.Height
                || post.Antialiasing is not FXAAEffect { Enabled: true, InputLuminanceInAlpha: true, Dither: FXAAEffect.DitherType.Low, Quality: 3 }
                || post.Bloom is not { Enabled: false, StableConvolution: true } || post.Outline.Enabled || post.AmbientOcclusion.Enabled
                || post.LocalReflections.Enabled || post.DepthOfField.Enabled || post.Fog.Enabled || post.LightStreak.Enabled
                || post.LensFlare.Enabled || !post.ColorTransforms.Enabled)
                return false;
            foreach (ColorTransform t in post.ColorTransforms.PreTransforms)
                if (t.Enabled)
                    return false;
            foreach (ColorTransform t in post.ColorTransforms.Transforms)
                if (t.Enabled)
                    return false;
            foreach (ColorTransform t in post.ColorTransforms.PostTransforms)
                if (t.Enabled && t is not LuminanceToChannelTransform { ColorChannel: Stride.Rendering.Materials.ColorChannel.A })
                    return false;
            return true;
        }

        void DrawFxaa(RenderDrawContext context, Texture colour, Texture output)
        {
            var compressed = Context.Allocator.GetTemporaryTexture2D(colour.Width, colour.Height, colour.Format,
                TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            using (context.PushRenderTargetsAndRestore())
            {
                compress.SetInput(0, colour);
                compress.SetOutput(compressed);
                compress.Draw(context);
                resolve.SetInput(0, compressed);
                resolve.SetOutput(output);
                resolve.Draw(context);
            }
            Context.Allocator.ReleaseReference(compressed);
        }

        /// <summary>Clip x, y and 1 to a pixel's view ray with a view depth of 1, from the camera's axes and the
        /// projection's scale. Inverting the whole view-projection (far 20 km) in float tilted all rays by up
        /// to a pixel, anew every frame, and the thin layer seen from above shook with it (Luca: "Zittern").</summary>
        internal static Matrix ClipToRayOf(RenderView view)
        {
            Matrix v = view.View, proj = view.Projection;
            var right = new Vector3(v.M11, v.M21, v.M31) / proj.M11;
            var up = new Vector3(v.M12, v.M22, v.M32) / proj.M22;
            var ahead = right * proj.M31 + up * proj.M32 - new Vector3(v.M13, v.M23, v.M33);
            return new Matrix(right.X, right.Y, right.Z, 0f, up.X, up.Y, up.Z, 0f,
                              ahead.X, ahead.Y, ahead.Z, 0f, 0f, 0f, 0f, 1f);
        }

        void DrawAtmosphere(RenderDrawContext drawContext, Atmosphere atmosphere, Texture colour, Texture depth)
        {
            RenderView view = drawContext.RenderContext.RenderView;
            // Half the screen's size at High; the Volumetric setting's divisor.
            int divisor = QualityLevels.MarchDivisor;
            int width = Math.Max(1, colour.Width / divisor), height = Math.Max(1, colour.Height / divisor);
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

            Matrix v = view.View, clipToRay = ClipToRayOf(view);
            Matrix viewToWorld = Matrix.Invert(view.View);
            float texel = atmosphere.CloudTile / atmosphere.CloudMap.Width;
            // Metres one half-resolution pixel spans per metre away, over a texel's width: the cloud map's lod.
            float pixel = 2f / (view.Projection.M22 * height);
            var p = march.Parameters;
            p.Set(ClipToRay, clipToRay);
            p.Set(MarchNearFar, new Vector2(view.NearClipPlane, view.FarClipPlane));
            p.Set(MarchDivisor, (float)divisor);
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
            p.Set(CoastHeights, new Vector4(atmosphere.CoastLevel, atmosphere.CoastLevel + atmosphere.CoastTop, 0f, pixel));
            p.Set(CoastRing, new Vector4(atmosphere.CoastInner.X, atmosphere.CoastInner.Y, atmosphere.CoastOuter.X, atmosphere.CoastOuter.Y));
            p.Set(CoastShape, new Vector4(atmosphere.CoastDensity, atmosphere.CoastHaze, 1f / atmosphere.CoastTile, atmosphere.CloudMap.Width / atmosphere.CoastTile));
            p.Set(CoastOffset, atmosphere.CoastOffset);
            p.Set(CoastColor, (Vector3)atmosphere.CoastColor);
            p.Set(Smoke, new Vector4(atmosphere.SmokeSource, atmosphere.SmokeDensity));
            p.Set(SmokeShape, new Vector4(atmosphere.SmokeClear.X, atmosphere.SmokeClear.Y, atmosphere.SmokeBend,
                pixel * atmosphere.CloudMap.Width / Math.Max(atmosphere.SmokeTile, 1f)));
            p.Set(SmokeRing, atmosphere.SmokeRing);
            p.Set(SmokeMotion, new Vector4(1f / Math.Max(atmosphere.SmokeTile, 1f), atmosphere.SmokeOffset.X, atmosphere.SmokeOffset.Y, 0f));
            p.Set(SmokeLitColor, (Vector3)atmosphere.SmokeLit);
            p.Set(SmokeShadeColor, (Vector3)atmosphere.SmokeShade);
            p.Set(SmokeGlowColor, (Vector3)atmosphere.SmokeGlow);

            using (drawContext.PushRenderTargetsAndRestore())
            {
                march.SetInput(0, depth);
                march.SetInput(1, atmosphere.CloudMap);
                march.SetOutput(half);
                march.Draw(drawContext);

                composite.Parameters.Set(NearFar, new Vector2(view.NearClipPlane, view.FarClipPlane));
                composite.Parameters.Set(CompositeDivisor, (float)divisor);
                composite.SetInput(0, half);
                composite.SetInput(1, depth);
                composite.SetOutput(colour);
                composite.Draw(drawContext);

                // Rays towards the sun, only while it is roughly ahead.
                var forward = -new Vector3(v.M13, v.M23, v.M33);
                float fade = MathUtil.Clamp((Vector3.Dot(Vector3.Normalize(forward), toSun) - RayFacing) / (1f - RayFacing), 0f, 1f);
                if (atmosphere.SunRays > 0f && fade > 0f && toSun.Y > 0f)
                {
                    var clip = Vector4.Transform(new Vector4(toSun, 0f), view.ViewProjection);
                    var sun = new Vector2(0.5f + 0.5f * clip.X / clip.W, 0.5f - 0.5f * clip.Y / clip.W);
                    int qw = Math.Max(1, colour.Width / 4), qh = Math.Max(1, colour.Height / 4);
                    var mask = Context.Allocator.GetTemporaryTexture2D(qw, qh, PixelFormat.R16_Float, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
                    var quarter = Context.Allocator.GetTemporaryTexture2D(qw, qh, PixelFormat.R16_Float, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
                    shaftMask.Parameters.Set(MaskSunUV, sun);
                    shaftMask.Parameters.Set(MaskShape, new Vector2(RayRadius, colour.Width / (float)colour.Height));
                    shaftMask.SetInput(0, depth);
                    shaftMask.SetInput(1, half);
                    shaftMask.SetOutput(mask);
                    shaftMask.Draw(drawContext);
                    shaftBlur.Parameters.Set(BlurSunUV, sun);
                    shaftBlur.Parameters.Set(BlurShape, new Vector2(RayReach, RayDecay));
                    shaftBlur.SetInput(0, mask);
                    shaftBlur.SetOutput(quarter);
                    shaftBlur.Draw(drawContext);
                    Context.Allocator.ReleaseReference(mask);
                    shaftAdd.Parameters.Set(Tint, sunColor * (atmosphere.SunRays * fade * fade));
                    shaftAdd.SetInput(0, quarter);
                    shaftAdd.SetOutput(colour);
                    shaftAdd.Draw(drawContext);
                    Context.Allocator.ReleaseReference(quarter);
                }
                if (atmosphere.HazeRadius > 0f)
                    DrawShimmer(drawContext, atmosphere, view, colour, depth, clipToRay, viewToWorld.TranslationVector);
            }
            Context.Allocator.ReleaseReference(half);
        }

        /// <summary>The hot air (HeatShimmer) over a copy of the scene, only while its cylinder is in view:
        /// elsewhere it costs nothing.</summary>
        void DrawShimmer(RenderDrawContext drawContext, Atmosphere atmosphere, RenderView view, Texture colour, Texture depth,
                         Matrix clipToRay, Vector3 eye)
        {
            Vector3 bottom = atmosphere.HazeBottom, reach = new Vector3(atmosphere.HazeRadius, atmosphere.HazeHeight, atmosphere.HazeRadius);
            var box = new BoundingBoxExt(new BoundingBox(bottom - new Vector3(reach.X, 0f, reach.Z), bottom + reach));
            Matrix viewProjection = view.ViewProjection;
            if (!new BoundingFrustum(ref viewProjection).Contains(ref box))
                return;
            var copy = Context.Allocator.GetTemporaryTexture2D(colour.Width, colour.Height, colour.Format, TextureFlags.ShaderResource);
            drawContext.CommandList.Copy(colour, copy);
            var p = shimmer.Parameters;
            p.Set(HazeClipToRay, clipToRay);
            p.Set(HazeNearFar, new Vector2(view.NearClipPlane, view.FarClipPlane));
            p.Set(HazeEye, eye);
            p.Set(Haze, new Vector4(bottom, atmosphere.HazeRadius));
            p.Set(HazeShape, new Vector4(atmosphere.HazeHeight, atmosphere.HazeStrength,
                (float)(drawContext.RenderContext.Time.Total.TotalSeconds % 3600.0), 0f));
            shimmer.SetInput(0, copy);
            shimmer.SetInput(1, depth);
            shimmer.SetOutput(colour);
            shimmer.Draw(drawContext);
            Context.Allocator.ReleaseReference(copy);
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
