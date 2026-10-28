// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Sean Boettger <sean@whypenguins.com>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Collections;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Core.Storage;
using Stride.Graphics;
using Stride.Rendering.Images;
using Stride.Rendering.Lights;
using Stride.Rendering.Shadows;
using Stride.Rendering.SubsurfaceScattering;
using Stride.VirtualReality;
using Stride.Rendering.Compositing;
using Stride.Rendering.Voxels.Debug;
using Stride.Rendering.Voxels.VoxelGI;

namespace Stride.Rendering.Voxels
{
    /// <summary>
    /// Renders your game. It should use current <see cref="RenderContext.RenderView"/> and <see cref="CameraComponentRendererExtensions.GetCurrentCamera"/>.
    /// </summary>
    [Display("Forward & Voxel renderer")]
    public class ForwardRendererVoxels : ForwardRenderer
    {
        public IVoxelRenderer VoxelRenderer { get; set; }

        protected IShadowMapRenderer ShadowMapRenderer_notPrivate;

        public VoxelDebug VoxelVisualization { get; set; }

        /// <summary>
        /// KotH patch: draws the meshes with a second UV set into the voxel light's lightmap
        /// (<c>LightmapBakeEffect</c>) once it is asked for (<see cref="LightmapRequestKey"/>).
        /// </summary>
        /// <userdoc>Stage in which the voxel light is baked into the lightmap once.</userdoc>
        public RenderStage LightmapBakeRenderStage { get; set; }

        /// <summary>KotH patch: the voxel light whose lightmap is to be baked, in the render context's tags.</summary>
        public static readonly PropertyKey<LightVoxel> LightmapRequestKey = new PropertyKey<LightVoxel>("LightmapRequest", typeof(ForwardRendererVoxels));

        // Rounds drawn before the lightmap counts as baked: effects compile on their own time.
        const int BakeRounds = 3;
        const int DilateSteps = 4;

        /// <summary>KotH patch: the sun past the map per lightmap texel (red; green 1), in the render context's tags once baked.</summary>
        public static readonly PropertyKey<Texture> SunLightmapKey = new PropertyKey<Texture>("SunLightmap", typeof(ForwardRendererVoxels));

        /// <summary>KotH patch: which meshes read the lightmap (null without a bake stage).</summary>
        public LightmapRenderFeature Lightmaps => lightmaps;

        LightmapRenderFeature lightmaps;
        RenderView bakeView;
        LightVoxel baking;
        Texture lightmap;
        Texture sunmap;
        int bakeRounds;
        ImageEffectShader dilate;

        protected override void InitializeCore()
        {
            var meshes = Context.RenderSystem.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault();
            ShadowMapRenderer_notPrivate = meshes?.RenderFeatures.OfType<ForwardLightingRenderFeature>().FirstOrDefault()?.ShadowMapRenderer;
            base.InitializeCore();
            if (meshes != null && LightmapBakeRenderStage != null)
            {
                lightmaps = new LightmapRenderFeature { MainStage = OpaqueRenderStage, BakeStage = LightmapBakeRenderStage };
                meshes.RenderFeatures.Add(lightmaps);
                meshes.PipelineProcessors.Add(new LightmapBakePipelineProcessor { BakeStage = LightmapBakeRenderStage });
                dilate = ToLoadAndUnload(new ImageEffectShader("LightmapDilate"));
            }
        }

        protected override void CollectStages(RenderContext context)
        {
            base.CollectStages(context);
            if (LightmapBakeRenderStage != null)
            {
                var output = new RenderOutputDescription(PixelFormat.R16G16B16A16_Float);
                output.RenderTargetCount = 2;
                output.RenderTargetFormat1 = PixelFormat.R16G16_Float;
                LightmapBakeRenderStage.Output = output;
            }
        }

        protected override void Destroy()
        {
            lightmap?.Dispose();
            sunmap?.Dispose();
            base.Destroy();
        }

        protected override void CollectCore(RenderContext context)
        {
            VoxelRenderer?.Collect(Context, ShadowMapRenderer_notPrivate);
            CollectLightmapBake(context);
            base.CollectCore(context);
        }

        // KotH patch: a view over the lightmap, lit like the camera's, with every mesh in it.
        void CollectLightmapBake(RenderContext context)
        {
            var light = context.Tags.Get(LightmapRequestKey);
            if (lightmaps == null || light == null || light.LightmapSize <= 0 || light.Lightmap != null)
            {
                baking = null;
                return;
            }
            if (baking != light)
            {
                baking = light;
                bakeRounds = 0;
                lightmaps.Ready = false;
            }
            bakeView ??= new RenderView { CullingMode = CameraCullingMode.None };
            bakeView.ViewSize = new Vector2(light.LightmapSize);
            bakeView.LightingView = context.RenderView;
            bakeView.RenderStages.Clear();
            bakeView.RenderStages.Add(LightmapBakeRenderStage);
            Context.RenderSystem.Views.Add(bakeView);
            Context.VisibilityGroup.TryCollect(bakeView);
        }

        protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
        {
            using (drawContext.PushRenderTargetsAndRestore())
            {
                VoxelRenderer?.Draw(drawContext, ShadowMapRenderer_notPrivate);
            }
            if (baking != null)
                DrawLightmapBake(drawContext);

            base.DrawCore(context, drawContext);
        }

        // KotH patch: light and sun into two targets; texels nothing covers are marked
        // (alpha or green -1) and grown over from their neighbours; the result goes to
        // the light once every mesh's bake effect has been compiled for a few rounds.
        void DrawLightmapBake(RenderDrawContext drawContext)
        {
            int size = baking.LightmapSize;
            if (lightmap == null || lightmap.Width != size)
            {
                lightmap?.Dispose();
                sunmap?.Dispose();
                lightmap = Texture.New2D(Context.GraphicsDevice, size, size, PixelFormat.R16G16B16A16_Float, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
                sunmap = Texture.New2D(Context.GraphicsDevice, size, size, PixelFormat.R16G16_Float, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            }
            using (drawContext.PushRenderTargetsAndRestore())
            {
                var commandList = drawContext.CommandList;
                commandList.Clear(lightmap, new Color4(0, 0, 0, -1));
                commandList.Clear(sunmap, new Color4(0, -1, 0, 0));
                commandList.SetRenderTargetsAndViewport(null, [lightmap, sunmap]);
                Context.RenderSystem.Draw(drawContext, bakeView, LightmapBakeRenderStage);
            }
            Dilate(drawContext, lightmap);
            Dilate(drawContext, sunmap);

            if (lightmaps.Meshes > 0 && lightmaps.BakeEffectsCompiled == lightmaps.Meshes && ++bakeRounds >= BakeRounds)
            {
                baking.Lightmap = lightmap;
                Context.Tags.Set(SunLightmapKey, sunmap);
                lightmaps.Ready = true;
                Context.Tags.Remove(LightmapRequestKey);
                baking = null;
            }
        }

        void Dilate(RenderDrawContext drawContext, Texture texture)
        {
            var spare = Context.Allocator.GetTemporaryTexture2D(texture.Width, texture.Height, texture.Format);
            using (drawContext.PushRenderTargetsAndRestore())
            {
                for (int i = 0; i < DilateSteps; i++)
                {
                    dilate.SetInput(0, i % 2 == 0 ? texture : spare);
                    dilate.SetOutput(i % 2 == 0 ? spare : texture);
                    dilate.Draw(drawContext);
                }
            }
            Context.Allocator.ReleaseReference(spare);
        }

        protected override void DrawView(RenderContext context, RenderDrawContext drawContext, int eyeIndex, int eyeCount)
        {
            base.DrawView(context, drawContext, eyeIndex, eyeCount);

            // Voxel Debug if enabled
            if (VoxelVisualization != null)
            {
                VoxelVisualization.VoxelRenderer = VoxelRenderer;
                VoxelVisualization.Draw(drawContext, viewOutputTarget);
            }
        }
    }
}


