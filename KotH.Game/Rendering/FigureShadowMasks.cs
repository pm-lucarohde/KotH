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
    /// <summary>Draws the moving casters' sun shadow from their tiles (FigureShadowMask.sdsl) into a mask per
    /// camera view, at the start of the opaque stage after the depth prepass; CascadeShadowReceiver reads it per
    /// pixel. First the pyramid over the sheet (FigureShadowPyramid.sdsl), which finds blockers and wide
    /// penumbrae in a few reads. Views without a prepass (voxels, bakes) get no mask and no such shadow.</summary>
    public sealed class FigureShadowMasks : RootRenderFeature
    {
        static readonly Logger Log = GlobalLogger.GetLogger("FigureShadowMasks");
        static readonly ValueParameterKey<Matrix> ClipToRay = ParameterKeys.NewValue<Matrix>(default, "FigureShadowMask.ClipToRay");
        static readonly ValueParameterKey<Vector2> NearFar = ParameterKeys.NewValue<Vector2>(default, "FigureShadowMask.NearFar");
        static readonly ValueParameterKey<Vector3> Eye = ParameterKeys.NewValue<Vector3>(default, "FigureShadowMask.Eye");
        static readonly ValueParameterKey<float> TileCount = ParameterKeys.NewValue<float>(default, "FigureShadowMask.TileCount");
        static readonly ValueParameterKey<Matrix> WorldToTile = ParameterKeys.NewValue<Matrix>(default, "FigureShadowMask.WorldToTile");
        static readonly ValueParameterKey<Vector4> TileRects = ParameterKeys.NewValue<Vector4>(default, "FigureShadowMask.TileRects");
        static readonly ValueParameterKey<Vector4> TileScreens = ParameterKeys.NewValue<Vector4>(default, "FigureShadowMask.TileScreens");
        static readonly ValueParameterKey<Vector4> TileScales = ParameterKeys.NewValue<Vector4>(default, "FigureShadowMask.TileScales");
        static readonly ValueParameterKey<float> DepthBias = ParameterKeys.NewValue<float>(default, "FigureShadowMask.DepthBias");
        static readonly ValueParameterKey<float> OffsetScale = ParameterKeys.NewValue<float>(default, "FigureShadowMask.OffsetScale");
        static readonly ValueParameterKey<float> SunTangent = ParameterKeys.NewValue<float>(default, "FigureShadowMask.SunTangent");
        static readonly ValueParameterKey<float> Throw = ParameterKeys.NewValue<float>(default, "FigureShadowMask.Throw");
        static readonly ValueParameterKey<Vector2> ShadowMapSize = ParameterKeys.NewValue<Vector2>(default, "FigureShadowMask.ShadowMapSize");
        static readonly ValueParameterKey<Vector2> ShadowMapTexelSize = ParameterKeys.NewValue<Vector2>(default, "FigureShadowMask.ShadowMapTexelSize");
        static readonly ValueParameterKey<Vector2> SheetAt = ParameterKeys.NewValue<Vector2>(default, "FigureShadowMask.SheetAt");
        static readonly ValueParameterKey<Vector4> PyramidLevels = ParameterKeys.NewValue<Vector4>(default, "FigureShadowMask.PyramidLevels");
        static readonly ObjectParameterKey<Texture> ShadowMap = ParameterKeys.NewObject<Texture>(null, "FigureShadowMask.ShadowMap");
        static readonly ObjectParameterKey<Texture> PyramidEven = ParameterKeys.NewObject<Texture>(null, "FigureShadowMask.PyramidEven");
        static readonly ObjectParameterKey<Texture> PyramidOdd = ParameterKeys.NewObject<Texture>(null, "FigureShadowMask.PyramidOdd");
        static readonly ValueParameterKey<Vector2> SourceAt = ParameterKeys.NewValue<Vector2>(default, "FigureShadowPyramid.SourceAt");
        static readonly ValueParameterKey<Vector2> TargetAt = ParameterKeys.NewValue<Vector2>(default, "FigureShadowPyramid.TargetAt");
        static readonly ValueParameterKey<float> First = ParameterKeys.NewValue<float>(default, "FigureShadowPyramid.First");

        /// <summary>The pyramid's levels: level n holds blocks of 2^(n+1) texels, the last a whole tile of 1024.</summary>
        public const int Levels = 10;

        /// <summary>No render objects: it only draws once per opaque stage.</summary>
        sealed class Nothing : RenderObject { }

        /// <summary>A camera view's mask and what it is drawn from this frame.</summary>
        sealed class ViewMask
        {
            public Texture Texture;
            public Size2 Wanted;   // the depth's size the last draw found (zero: the view's size)
            public SunShadowTiles Tiles;
            public float SunTangent;
            public int Frame;      // the frame it was last bound
        }

        public override Type SupportedRenderObjectType => typeof(Nothing);

        RenderStage opaque, prepass;
        ImageEffectShader shader, builder;
        // The pyramid in two textures, even and odd levels: a level is drawn from the one before it.
        Texture even, odd;
        readonly Dictionary<RenderView, ViewMask> views = new Dictionary<RenderView, ViewMask>();
        readonly Vector4[] levels = new Vector4[Levels];
        int frame;

        /// <summary>Hooks it into the scene's compositor once and hands it to the sun's shadow renderer.</summary>
        public static void Install(SceneSystem scene)
        {
            GraphicsCompositor compositor = scene.GraphicsCompositor;
            if (compositor == null || compositor.RenderFeatures.OfType<FigureShadowMasks>().Any())
                return;
            var lighting = compositor.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault()?.RenderFeatures
                .OfType<Stride.Rendering.Lights.ForwardLightingRenderFeature>().FirstOrDefault();
            var sun = (lighting?.ShadowMapRenderer as ShadowMapRenderer)?.Renderers.OfType<SunShadowMapRenderer>().FirstOrDefault();
            RenderStage opaque = compositor.RenderStages.FirstOrDefault(s => s.Name == "Opaque");
            RenderStage prepass = compositor.RenderStages.FirstOrDefault(s => s.Name == "GBuffer");
            if (sun == null || opaque == null || prepass == null)
            {
                Log.Warning("No sun shadow renderer, opaque or prepass stage: figures keep no sun shadow.");
                return;
            }
            var masks = new FigureShadowMasks { opaque = opaque, prepass = prepass };
            compositor.RenderFeatures.Add(masks);
            sun.Masks = masks;
        }

        protected override void InitializeCore()
        {
            base.InitializeCore();
            shader = new ImageEffectShader("FigureShadowMask");
            shader.Initialize(Context);
            builder = new ImageEffectShader("FigureShadowPyramid");
            builder.Initialize(Context);
        }

        public override void Unload()
        {
            shader?.Dispose();
            builder?.Dispose();
            even?.Dispose();
            odd?.Dispose();
            (shader, builder, even, odd) = (null, null, null, null);
            foreach (ViewMask mask in views.Values)
                mask.Texture?.Dispose();
            views.Clear();
            base.Unload();
        }

        /// <summary>Whether the view gets a mask: it draws the opaque stage after a depth prepass.</summary>
        public bool Wants(RenderView view) => Has(view, opaque) && Has(view, prepass);

        /// <summary>The mask the view's materials read this frame, drawn from its tiles; null for a view without
        /// the depth prepass or without tiles. Called while the lighting prepares (perhaps several times a view).</summary>
        public Texture Bind(RenderView view, SunShadowTiles tiles, float sunTangent)
        {
            if (tiles.Count == 0 || !Wants(view))
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
                (mask.Tiles, mask.SunTangent, mask.Frame) = (tiles, sunTangent, frame);
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
            SunShadowTiles tiles = mask.Tiles;
            if (tiles?.Texture is not { } atlas)
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

            var p = shader.Parameters;
            p.Set(ClipToRay, AtmosphereEffects.ClipToRayOf(renderView));
            p.Set(NearFar, new Vector2(renderView.NearClipPlane, renderView.FarClipPlane));
            p.Set(Eye, Matrix.Invert(renderView.View).TranslationVector);
            p.Set(TileCount, (float)tiles.Count);
            p.Set(WorldToTile, SunShadowTiles.Most, ref tiles.WorldToUV[0]);
            p.Set(TileRects, SunShadowTiles.Most, ref tiles.Rects[0]);
            p.Set(TileScreens, SunShadowTiles.Most, ref tiles.Screens[0]);
            p.Set(TileScales, SunShadowTiles.Most, ref tiles.Scales[0]);
            p.Set(DepthBias, tiles.DepthBias);
            p.Set(OffsetScale, tiles.OffsetScale);
            p.Set(SunTangent, mask.SunTangent);
            p.Set(Throw, tiles.Throw);
            p.Set(ShadowMapSize, new Vector2(atlas.Width, atlas.Height));
            p.Set(ShadowMapTexelSize, new Vector2(1f / atlas.Width, 1f / atlas.Height));
            p.Set(SheetAt, new Vector2(tiles.Sheet.X, tiles.Sheet.Y));
            p.Set(ShadowMap, atlas);
            using (context.PushRenderTargetsAndRestore())
            {
                DrawPyramid(context, tiles, atlas);
                p.Set(PyramidLevels, Levels, ref levels[0]);
                p.Set(PyramidEven, even);
                p.Set(PyramidOdd, odd);
                shader.SetInput(0, depth);
                shader.SetOutput(mask.Texture);
                shader.Draw(context);
            }
        }

        /// <summary>The pyramid over the sheet's used part, anew for each view (another may share the atlas):
        /// level 0 from the atlas' depth, each further level from the one before in the other texture.</summary>
        void DrawPyramid(RenderDrawContext context, SunShadowTiles tiles, Texture atlas)
        {
            int sheet = tiles.Sheet.Width;
            if (even == null || even.Height != sheet / 2)
            {
                even?.Dispose();
                odd?.Dispose();
                even = Texture.New2D(Context.GraphicsDevice, sheet / 2 + sheet / 8, sheet / 2, PixelFormat.R32G32B32A32_Float,
                                     TextureFlags.RenderTarget | TextureFlags.ShaderResource);
                odd = Texture.New2D(Context.GraphicsDevice, sheet / 4 + sheet / 16, sheet / 4, PixelFormat.R32G32B32A32_Float,
                                    TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            }
            Int2 before = new Int2(tiles.Sheet.X, tiles.Sheet.Y);
            for (int level = 0; level < Levels; level++)
            {
                Texture target = (level & 1) == 0 ? even : odd;
                Int2 at = Corner(level, sheet);
                int block = 2 << level;
                levels[level] = new Vector4(at.X, at.Y, 1f / target.Width, 1f / target.Height);
                builder.Parameters.Set(SourceAt, new Vector2(before.X, before.Y));
                builder.Parameters.Set(TargetAt, new Vector2(at.X, at.Y));
                builder.Parameters.Set(First, level == 0 ? 1f : 0f);
                builder.SetInput(0, level == 0 ? atlas : (level & 1) == 0 ? odd : even);
                builder.SetOutput(target);
                builder.SetViewport(new Viewport(at.X, at.Y, Math.Max(1, (tiles.Used.X + block - 1) / block), Math.Max(1, (tiles.Used.Y + block - 1) / block)));
                builder.Draw(context);
                before = at;
            }
        }

        // Where a level lies in its texture: the first of each fills the left, the further ones stack beside it.
        static Int2 Corner(int level, int sheet)
        {
            if (level < 2)
                return Int2.Zero;
            int y = 0;
            for (int above = level - 2; above >= 2; above -= 2)
                y += sheet >> (above + 1);
            return new Int2(sheet >> (1 + (level & 1)), y);
        }
    }
}
