// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Sean Boettger <sean@whypenguins.com>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Shaders;
using Stride.Rendering.Materials;

namespace Stride.Rendering.Voxels
{
    [DataContract(DefaultMemberMode = DataMemberMode.Default)]
    [Display("Anisotropic (3 sided)")]
    public class VoxelLayoutAnisotropicPaired : VoxelLayoutBase, IVoxelLayout
    {
        protected override int LayoutCount { get; set; } = 3;
        protected override ShaderClassSource Writer { get; set; } = new ShaderClassSource("VoxelAnisotropicPairedWriter_Float4");
        protected override ShaderClassSource Sampler { get; set; } = new ShaderClassSource("VoxelAnisotropicPairedSampler");
        protected override string ApplierKey { get; set; } = "AnisotropicPaired";

        public void UpdateVoxelizationLayout(string compositionName, List<VoxelModifierEmissionOpacity> modifier)
        {
            DirectOutput = VoxelNames.Compose(VoxelAnisotropicPairedWriter_Float4Keys.DirectOutput, compositionName);
            BrightnessInvKey = VoxelNames.Compose(VoxelAnisotropicPairedWriter_Float4Keys.maxBrightnessInv, compositionName);
        }
        public void UpdateSamplingLayout(string compositionName)
        {
            BrightnessKey = VoxelNames.Compose(VoxelAnisotropicPairedSamplerKeys.maxBrightness, compositionName);
            storageTex.UpdateSamplingLayout(VoxelNames.Join("storage.", compositionName));
        }
    }
}
