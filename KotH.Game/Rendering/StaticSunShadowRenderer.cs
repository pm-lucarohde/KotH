using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.Lights;
using Stride.Rendering.Voxels.VoxelGI;

namespace KotH.Client.Rendering
{
    /// <summary>The static sun shadow map as the receivers read it.</summary>
    public sealed class StaticSunShadow
    {
        /// <summary>Depth from the sun over every static caster; null until drawn.</summary>
        public Texture Texture;
        /// <summary>World to texture: uv in xy, depth in z.</summary>
        public Matrix WorldToUV;
        /// <summary>Every static caster's effect was compiled when it was drawn: it is complete.</summary>
        public bool Ready;
    }

    /// <summary>
    /// Draws the casters that never move (<see cref="SunShadowStageSelector"/>: the
    /// map with its lightmap UVs) once from the sun, orthographic over all of them,
    /// into one depth map; the cascades of every frame then hold only the figures.
    /// Redraws until every caster's effect is compiled, then keeps it.
    /// </summary>
    /// <userdoc>Draws the still casters (map) once from the sun into a large shadow map.</userdoc>
    [DataContract("StaticSunShadowRenderer")]
    [Display("Static sun shadow")]
    public sealed class StaticSunShadowRenderer : SceneRendererBase
    {
        public static readonly PropertyKey<StaticSunShadow> Key = new("StaticSunShadow", typeof(StaticSunShadowRenderer));

        /// <userdoc>Stage of the still casters (as in SunShadowStageSelector).</userdoc>
        [DataMember(10)]
        public RenderStage StaticRenderStage { get; set; }

        /// <userdoc>Edge length of the shadow map in texels (4096 is about 8 cm on the mountain).</userdoc>
        [DataMember(20)]
        public int Size { get; set; } = 4096;

        readonly StaticSunShadow shadow = new();
        RenderView view;
        Texture depth;
        bool collected;
        int casters;

        protected override void CollectCore(RenderContext context)
        {
            collected = false;
            context.Tags.Set(Key, shadow);
            if (shadow.Ready || StaticRenderStage == null || Size <= 0)
                return;
            RenderLight sun = null;
            foreach (RenderLight light in SceneInstance.GetCurrent(context)?.GetProcessor<LightProcessor>()?.Lights ?? [])
                if (light.Type is LightDirectional { Shadow.Enabled: true })
                    sun = light;
            casters = 0;
            var bounds = BoundingBox.Empty;
            foreach (RenderObject renderObject in context.VisibilityGroup.RenderObjects)
                if (renderObject is RenderMesh { IsShadowCaster: true, Enabled: true } mesh && SunShadowStageSelector.IsStatic(mesh))
                {
                    bounds = BoundingBox.Merge(bounds, (BoundingBox)mesh.BoundingBox);
                    casters++;
                }
            if (sun == null || casters == 0)
                return;

            // A square box round the casters in the sun's view, a metre to spare in depth.
            Vector3 direction = Vector3.Normalize(sun.Direction);
            Vector3 up = MathF.Abs(direction.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
            Vector3 centre = bounds.Center;
            Matrix lightView = Matrix.LookAtRH(centre - direction, centre, up);
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (Vector3 corner in bounds.GetCorners())
            {
                Vector3 p = Vector3.TransformCoordinate(corner, lightView);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            float half = 0.5f * MathF.Max(max.X - min.X, max.Y - min.Y);
            float cx = 0.5f * (min.X + max.X), cy = 0.5f * (min.Y + max.Y);
            Matrix projection = Matrix.OrthoOffCenterRH(cx - half, cx + half, cy - half, cy + half, -max.Z - 1f, -min.Z + 1f);

            view ??= new RenderView { CullingMode = CameraCullingMode.None };
            view.View = lightView;
            view.Projection = projection;
            view.ViewProjection = lightView * projection;
            view.ViewSize = new Vector2(Size);
            view.NearClipPlane = -max.Z - 1f;
            view.FarClipPlane = -min.Z + 1f;
            // The view's stage copies the output, so it is set first.
            StaticRenderStage.Output = new RenderOutputDescription(PixelFormat.None, PixelFormat.D32_Float);
            view.RenderStages.Clear();
            view.RenderStages.Add(StaticRenderStage);
            shadow.WorldToUV = view.ViewProjection * Matrix.Scaling(0.5f, -0.5f, 1f) * Matrix.Translation(0.5f, 0.5f, 0f);
            context.RenderSystem.Views.Add(view);
            context.VisibilityGroup.TryCollect(view);
            collected = true;
        }

        protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
        {
            if (!collected)
                return;
            if (depth == null || depth.Width != Size)
            {
                depth?.Dispose();
                depth = Texture.New2D(context.GraphicsDevice, Size, Size, PixelFormat.D32_Float, TextureFlags.DepthStencil | TextureFlags.ShaderResource);
            }
            var commandList = drawContext.CommandList;
            using (drawContext.PushRenderTargetsAndRestore())
            {
                commandList.ResourceBarrierTransition(depth, BarrierLayout.DepthStencilWrite);
                commandList.Clear(depth, DepthStencilClearOptions.DepthBuffer, 1f, 0);
                commandList.SetRenderTargetAndViewport(depth, (Texture)null);
                context.RenderSystem.Draw(drawContext, view, StaticRenderStage);
            }
            commandList.ResourceBarrierTransition(depth, BarrierLayout.ShaderResource);
            shadow.Texture = depth;
            shadow.Ready = Compiled() == casters;
        }

        // Static casters drawn with a compiled effect this frame (the rest were skipped).
        int Compiled()
        {
            int compiled = 0;
            foreach (RenderViewStage stage in view.RenderStages)
                if (stage.Index == StaticRenderStage.Index && stage.SortedRenderNodes != null)
                    foreach (RenderNodeFeatureReference node in stage.SortedRenderNodes)
                        if (node.RootRenderFeature.GetRenderNode(node.RenderNode).RenderEffect is { State: RenderEffectState.Normal, Effect: not null })
                            compiled++;
            return compiled;
        }

        protected override void Destroy()
        {
            depth?.Dispose();
            depth = null;
            base.Destroy();
        }
    }
}
