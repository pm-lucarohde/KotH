// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Sean Boettger <sean@whypenguins.com>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core.Mathematics;
using Stride.Games;
using Stride.Engine;
using Stride.Shaders;
using Stride.Rendering.Voxels.Debug;

namespace Stride.Rendering.Voxels
{
    public class DataVoxelVolume
    {
        public Vector3 VolumeSize;
        public Vector3 VolumeTranslation;
        public float AproxVoxelSize;
        public bool Voxelize;
        public RenderGroupMask StaticGroups; // KotH patch
        public RenderGroupMask DynamicGroups;

        public bool VoxelGridSnapping;
        public bool VisualizeVoxels;
        public VoxelAttribute VisualizationAttribute;
        public IVoxelVisualization VoxelVisualization;

        public List<VoxelAttribute> Attributes = new List<VoxelAttribute>();

        public IVoxelStorage Storage;
        public IVoxelizationMethod VoxelizationMethod;
    }
    public enum VoxelizationStage
    {
        Initial,
        Post
    }
    public struct AttributeStream
    {
        public VoxelAttribute Attribute;
        public VoxelizationStage Stage;
        public bool Output;
        public AttributeStream(VoxelAttribute attribute, VoxelizationStage stage, bool output)
        {
            Attribute = attribute;
            Stage = stage;
            Output = output;
        }
    }
    public class ProcessedVoxelVolume
    {
        public bool Voxelize;
        /// <summary>KotH patch: set up by a voxelisation, so it can be traced without one.</summary>
        public bool Prepared;
        /// <summary>KotH patch: this frame only the dynamic groups are voxelised, in their boxes.</summary>
        public bool Dynamic;
        /// <summary>KotH patch: the volume's dynamic groups, none if nothing moves.</summary>
        public RenderGroupMask DynamicGroups;
        /// <summary>KotH patch: world bounds of what the dynamic groups draw this frame.</summary>
        public List<BoundingBox> DynamicBounds = new List<BoundingBox>();
        public bool VisualizeVoxels;
        public VoxelAttribute VisualizationAttribute;
        public IVoxelVisualization VoxelVisualization;

        public IVoxelStorage Storage;
        public IVoxelizationMethod VoxelizationMethod;
        public VoxelStorageContext StorageContext;

        public VoxelizationPassList passList = new VoxelizationPassList();

        public List<List<VoxelizationPass>> groupedPasses = new List<List<VoxelizationPass>>();

        public List<VoxelAttribute> OutputAttributes = new List<VoxelAttribute>();
        public List<AttributeStream> Attributes = new List<AttributeStream>();
    }
}
