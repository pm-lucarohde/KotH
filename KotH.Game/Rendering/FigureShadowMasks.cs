using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.Images;
using Stride.Rendering.Shadows;

namespace KotH.Client.Rendering
{
    /// <summary>Draws the figures' sun shadow from the cascades (FigureShadowMask.sdsl) into a mask per camera
    /// view, at the start of the opaque stage after the depth prepass; CascadeShadowReceiver reads it per
    /// pixel. Filtering the cascades in every material held registers and slowed the whole colour pass
    /// (0.7 ms of 2.7 at the spawn). Views without a prepass (voxels, bakes) get no mask and no figure shadow.</summary>
    public sealed class FigureShadowMasks : RootRenderFeature
    {
        static readonly Logger Log = GlobalLogger.GetLogger("FigureShadowMasks");
        static readonly ValueParameterKey<Matrix> ClipToRay = ParameterKeys.NewValue<Matrix>(default, "FigureShadowMask.ClipToRay");
        static readonly ValueParameterKey<Vector2> NearFar = ParameterKeys.NewValue<Vector2>(default, "FigureShadowMask.NearFar");
        static readonly ValueParameterKey<Vector3> Eye = ParameterKeys.NewValue<Vector3>(default, "FigureShadowMask.Eye");
        static readonly ValueParameterKey<Matrix> WorldToCascadeUV = ParameterKeys.NewValue<Matrix>(default, "FigureShadowMask.WorldToCascadeUV");
        static readonly ValueParameterKey<Vector4> CascadeRects = ParameterKeys.NewValue<Vector4>(default, "FigureShadowMask.CascadeRects");
        static readonly ValueParameterKey<Vector4> CascadeSplits = ParameterKeys.NewValue<Vector4>(default, "FigureShadowMask.CascadeSplits");
        static readonly ValueParameterKey<float> CascadeCount = ParameterKeys.NewValue<float>(default, "FigureShadowMask.CascadeCount");
        static readonly ValueParameterKey<float> BlendCascades = ParameterKeys.NewValue<float>(default, "FigureShadowMask.BlendCascades");
        static readonly ValueParameterKey<float> DepthBias = ParameterKeys.NewValue<float>(default, "FigureShadowMask.DepthBias");
        static readonly ValueParameterKey<float> OffsetScale = ParameterKeys.NewValue<float>(default, "FigureShadowMask.OffsetScale");
        static readonly ValueParameterKey<float> SunTangent = ParameterKeys.NewValue<float>(default, "FigureShadowMask.SunTangent");
        static readonly ValueParameterKey<float> MaxPenumbra = ParameterKeys.NewValue<float>(default, "FigureShadowMask.MaxPenumbra");
        static readonly ValueParameterKey<Vector2> ShadowMapSize = ParameterKeys.NewValue<Vector2>(default, "FigureShadowMask.ShadowMapSize");
        static readonly ValueParameterKey<Vector2> ShadowMapTexelSize = ParameterKeys.NewValue<Vector2>(default, "FigureShadowMask.ShadowMapTexelSize");
        static readonly ObjectParameterKey<Texture> ShadowMap = ParameterKeys.NewObject<Texture>(null, "FigureShadowMask.ShadowMap");
        static readonly ObjectParameterKey<Texture> CasterTiles = ParameterKeys.NewObject<Texture>(null, "FigureShadowMask.CasterTiles");
        static readonly ValueParameterKey<Vector2> TileCount = ParameterKeys.NewValue<Vector2>(default, "FigureShadowMask.TileCount");

        // Texels per side of a tile in FigureShadowTiles.
        const int TileTexels = 16;

        /// <summary>The most cascades the mask shader takes (1, 2 or 4, with a fixed depth range).</summary>
        public const int Cascades = 4;

        /// <summary>No render objects: it only draws once per opaque stage.</summary>
        sealed class Nothing : RenderObject { }

        /// <summary>A camera view's mask and what it is drawn from this frame.</summary>
        sealed class ViewMask
        {
            public Texture Texture;
            public Size2 Wanted;   // the depth's size the last draw found (zero: the view's size)
            public LightDirectionalShadowMapRenderer.ShaderData Shadow;
            public float SunTangent, MaxPenumbra;
            public int Count;
            public bool Blend;
            public int Frame;      // the frame it was last bound
        }

        public override Type SupportedRenderObjectType => typeof(Nothing);

        RenderStage opaque, prepass;
        ImageEffectShader shader, tiler;
        Texture tiles;
        readonly Dictionary<RenderView, ViewMask> views = new Dictionary<RenderView, ViewMask>();
        readonly Matrix[] toUV = new Matrix[Cascades];
        readonly Vector4[] rects = new Vector4[Cascades];
        int frame;

        /// <summary>Hooks it into the scene's compositor once and hands it to the cascades' renderer.</summary>
        public static void Install(SceneSystem scene)
        {
            GraphicsCompositor compositor = scene.GraphicsCompositor;
            if (compositor == null || compositor.RenderFeatures.OfType<FigureShadowMasks>().Any())
                return;
            var lighting = compositor.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault()?.RenderFeatures
                .OfType<Stride.Rendering.Lights.ForwardLightingRenderFeature>().FirstOrDefault();
            var cascades = (lighting?.ShadowMapRenderer as ShadowMapRenderer)?.Renderers.OfType<CascadeShadowMapRenderer>().FirstOrDefault();
            RenderStage opaque = compositor.RenderStages.FirstOrDefault(s => s.Name == "Opaque");
            RenderStage prepass = compositor.RenderStages.FirstOrDefault(s => s.Name == "GBuffer");
            if (cascades == null || opaque == null || prepass == null)
            {
                Log.Warning("No cascade renderer, opaque or prepass stage: figures keep no sun shadow.");
                return;
            }
            var masks = new FigureShadowMasks { opaque = opaque, prepass = prepass };
            compositor.RenderFeatures.Add(masks);
            cascades.Masks = masks;
        }

        protected override void InitializeCore()
        {
            base.InitializeCore();
            shader = new ImageEffectShader("FigureShadowMask");
            shader.Initialize(Context);
            tiler = new ImageEffectShader("FigureShadowTiles");
            tiler.Initialize(Context);
        }

        public override void Unload()
        {
            shader?.Dispose();
            tiler?.Dispose();
            tiles?.Dispose();
            (shader, tiler, tiles) = (null, null, null);
            foreach (ViewMask mask in views.Values)
                mask.Texture?.Dispose();
            views.Clear();
            base.Unload();
        }

        /// <summary>The mask the view's materials read this frame, drawn from the cascades' data; null for a
        /// view without the depth prepass. Called while the lighting prepares (perhaps several times a view).</summary>
        public Texture Bind(RenderView view, LightDirectionalShadowMapRenderer.ShaderData shadow, int count, bool blend,
                            float sunTangent, float maxPenumbra)
        {
            if (!Has(view, opaque) || !Has(view, prepass))
                return null;
            lock (views)
            {
                if (!views.TryGetValue(view, out ViewMask mask))
                    views[view] = mask = new ViewMask();
                Size2 size = mask.Wanted.Width > 0 ? mask.Wanted : new Size2((int)view.ViewSize.X, (int)view.ViewSize.Y);
                if (size.Width <= 0 || size.Height <= 0)
                    return null;
                if (mask.Texture == null || mask.Texture.Width != size.Width || mask.Texture.Height != size.Height)
                {
                    mask.Texture?.Dispose();
                    mask.Texture = Texture.New2D(Context.GraphicsDevice, size.Width, size.Height, PixelFormat.R32_Float,
                                                 TextureFlags.RenderTarget | TextureFlags.ShaderResource);
                }
                (mask.Shadow, mask.Count, mask.Blend, mask.SunTangent, mask.MaxPenumbra, mask.Frame) =
                    (shadow, count, blend, sunTangent, maxPenumbra, frame);
                return mask.Texture;
            }
        }

        static bool Has(RenderView view, RenderStage stage)
        {
            foreach (RenderViewStage s in view.RenderStages)
                if (s.Index == stage.Index)
                    return true;
            return false;
        }

        public override void Flush(RenderDrawContext context)
        {
            base.Flush(context);
            frame++;
        }

        public override void Draw(RenderDrawContext context, RenderView renderView, RenderViewStage renderViewStage)
        {
            if (renderViewStage.Index != opaque.Index)
                return;
            ViewMask mask;
            lock (views)
                if (!views.TryGetValue(renderView, out mask) || mask.Frame != frame)
                    return;
            if (mask.Shadow?.Texture is not { } atlas)
                return;
            Texture depth = context.CommandList.DepthStencilBuffer;
            if (depth == null)
                return;
            if (depth.Width != mask.Texture.Width || depth.Height != mask.Texture.Height)
            {
                // Sized from the view before the first draw: right from the next frame on, lit until then.
                mask.Wanted = new Size2(depth.Width, depth.Height);
                context.CommandList.Clear(mask.Texture, Color4.White);
                return;
            }

            var shadow = mask.Shadow;
            var p = shader.Parameters;
            p.Set(ClipToRay, AtmosphereEffects.ClipToRayOf(renderView));
            p.Set(NearFar, new Vector2(renderView.NearClipPlane, renderView.FarClipPlane));
            p.Set(Eye, Matrix.Invert(renderView.View).TranslationVector);
            Array.Copy(shadow.WorldToShadowCascadeUV, toUV, mask.Count);
            Array.Copy(shadow.TextureCoords, rects, mask.Count);
            Vector4 splits = default;
            for (int i = 0; i < mask.Count; i++)
                splits[i] = shadow.CascadeSplits[i];
            p.Set(WorldToCascadeUV, Cascades, ref toUV[0]);
            p.Set(CascadeRects, Cascades, ref rects[0]);
            p.Set(CascadeSplits, splits);
            p.Set(CascadeCount, (float)mask.Count);
            p.Set(BlendCascades, mask.Blend ? 1f : 0f);
            p.Set(DepthBias, shadow.DepthBias);
            p.Set(OffsetScale, shadow.OffsetScale);
            p.Set(SunTangent, mask.SunTangent);
            p.Set(MaxPenumbra, mask.MaxPenumbra);
            p.Set(ShadowMapSize, new Vector2(atlas.Width, atlas.Height));
            p.Set(ShadowMapTexelSize, new Vector2(1f / atlas.Width, 1f / atlas.Height));
            p.Set(ShadowMap, atlas);
            using (context.PushRenderTargetsAndRestore())
            {
                DrawTiles(context, atlas);
                p.Set(CasterTiles, tiles);
                p.Set(TileCount, new Vector2(tiles.Width, tiles.Height));
                shader.SetInput(0, depth);
                shader.SetOutput(mask.Texture);
                shader.Draw(context);
            }
        }

        /// <summary>The atlas' caster tiles, anew for each view (another renderer may cast into it between).</summary>
        void DrawTiles(RenderDrawContext context, Texture atlas)
        {
            int width = (atlas.Width + TileTexels - 1) / TileTexels, height = (atlas.Height + TileTexels - 1) / TileTexels;
            if (tiles == null || tiles.Width != width || tiles.Height != height)
            {
                tiles?.Dispose();
                tiles = Texture.New2D(Context.GraphicsDevice, width, height, PixelFormat.R8_UNorm,
                                      TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            }
            tiler.SetInput(0, atlas);
            tiler.SetOutput(tiles);
            tiler.Draw(context);
        }
    }
}
