// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Sean Boettger <sean@whypenguins.com>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Text;
using Stride.Engine;
using Stride.Games;
using Stride.Extensions;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Graphics.GeometricPrimitives;
using Stride.Rendering;
using Stride.Rendering.Voxels;
using Stride.Rendering.Shadows;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;

namespace Stride.Engine.Processors
{
    public class VoxelVolumeProcessor : EntityProcessor<VoxelVolumeComponent>, IEntityComponentRenderProcessor
    {
        private Dictionary<VoxelVolumeComponent, DataVoxelVolume> renderVoxelVolumes = new Dictionary<VoxelVolumeComponent, DataVoxelVolume>();
        public Dictionary<VoxelVolumeComponent, ProcessedVoxelVolume> processedVoxelVolumes = new Dictionary<VoxelVolumeComponent, ProcessedVoxelVolume>();

        public VisibilityGroup VisibilityGroup { get; set; }
        public RenderGroup RenderGroup { get; set; }
        protected override void OnSystemAdd()
        {
            base.OnSystemAdd();

            VisibilityGroup.Tags.Set(VoxelRenderer.CurrentRenderVoxelVolumes, renderVoxelVolumes);
            VisibilityGroup.Tags.Set(VoxelRenderer.CurrentProcessedVoxelVolumes, processedVoxelVolumes);
            VisibilityGroup.Tags.Set(VoxelRenderFeature.CurrentProcessedVoxelVolumes, processedVoxelVolumes);
        }

        protected override void OnEntityComponentRemoved(Entity entity, VoxelVolumeComponent component, VoxelVolumeComponent data)
        {
            base.OnEntityComponentRemoved(entity, component, data);
            renderVoxelVolumes.Remove(component);
            processedVoxelVolumes.Remove(component);
        }

        public override void Draw(RenderContext context)
        {
            RegenerateVoxelVolumes();
        }
        
        public ProcessedVoxelVolume GetProcessedVolumeForComponent(VoxelVolumeComponent component)
        {
            if (!processedVoxelVolumes.TryGetValue(component, out var data))
                return null;
            return data;
        }
        public DataVoxelVolume GetRenderVolumeForComponent(VoxelVolumeComponent component)
        {
            if (!renderVoxelVolumes.TryGetValue(component, out var data))
                return null;
            return data;
        }

        private void RegenerateVoxelVolumes()
        {
            // KotH patch: the volumes live on from frame to frame instead of
            // being made anew (garbage every frame, and a volume that is not
            // voxelised again must keep what the last voxelisation set up).
            foreach (var pair in ComponentDatas)
            {
                if (!pair.Key.Enabled)
                {
                    renderVoxelVolumes.Remove(pair.Key);
                    processedVoxelVolumes.Remove(pair.Key);
                    continue;
                }

                var volume = pair.Key;

                if (!renderVoxelVolumes.TryGetValue(volume, out DataVoxelVolume data))
                    renderVoxelVolumes.Add(volume, data = new DataVoxelVolume());
                if (!processedVoxelVolumes.ContainsKey(volume))
                    processedVoxelVolumes.Add(volume, new ProcessedVoxelVolume());

                data.VolumeTranslation = volume.Entity.Transform.WorldMatrix.TranslationVector;
                data.VolumeSize = new Vector3(volume.VoxelVolumeSize);


                data.Voxelize = volume.Voxelize;
                data.StaticGroups = volume.StaticGroups;
                data.DynamicGroups = volume.DynamicGroups;
                data.AproxVoxelSize = volume.AproximateVoxelSize;

                data.VoxelGridSnapping = volume.VoxelGridSnapping;
                data.VisualizeVoxels = volume.VisualizeVoxels;
                data.VoxelVisualization = volume.Visualization;
                data.Attributes = volume.Attributes;
                data.Storage = volume.Storage;
                data.VoxelizationMethod = volume.VoxelizationMethod;

                if (volume.Attributes.Count > volume.VisualizeIndex)
                {
                    data.VisualizationAttribute = volume.Attributes[volume.VisualizeIndex];
                }
                else
                {
                    data.VisualizationAttribute = null;
                }
            }
        }
    }
}
