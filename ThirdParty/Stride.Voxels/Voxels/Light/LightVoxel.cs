// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Sean Boettger <sean@whypenguins.com>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Graphics;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering.Lights;
using Stride.Rendering.Skyboxes;

namespace Stride.Rendering.Voxels.VoxelGI
{
    /// <summary>
    /// A light casting from a voxel representation.
    /// </summary>
    [DataContract("LightVoxel")]
    [Display("Voxel")]
    public class LightVoxel : IEnvironmentLight
    {
        [DataMember(1)]
        public VoxelVolumeComponent Volume { get; set; }
        [DataMember(10)]
        public int AttributeIndex { get; set; } = 0;

        [DataMember(20)]
        public IVoxelMarchSet DiffuseMarcher { get; set; } = new VoxelMarchSetHemisphere6(new VoxelMarchConePerMipmap());
        [DataMember(30)]
        public IVoxelMarchMethod SpecularMarcher { get; set; } = new VoxelMarchCone(30, 0.5f, 1.0f);

        [DataMember(40)]
        public float BounceIntensityScale { get; set; }
        [DataMember(50)]
        public float SpecularIntensityScale { get; set; }

        // KotH patch: sky light that only reaches where the cones get out.
        /// <userdoc>
        /// Skybox whose light falls through the gaps of the cones (diffuse and specular), instead of a separate skybox light.
        /// </userdoc>
        [DataMember(60)]
        public Skybox Sky { get; set; }

        /// <userdoc>
        /// Strength of the sky light, independent of the light's intensity.
        /// </userdoc>
        [DataMember(70)]
        public float SkyIntensity { get; set; } = 1.0f;

        /// <summary>
        /// KotH patch: side in texels of the lightmap the light is baked into, once, for
        /// meshes with a second UV set (0 = none, they are lit live like the rest).
        /// </summary>
        /// <userdoc>
        /// Side of the lightmap in texels: meshes with a second UV set get the light baked once instead of every frame (0 = off).
        /// </userdoc>
        [DataMember(80)]
        public int LightmapSize { get; set; }

        /// <summary>KotH patch: the baked lightmap (set by ForwardRendererVoxels), null until then.</summary>
        [DataMemberIgnore]
        public Texture Lightmap { get; set; }

        /// <summary>KotH patch: most groups and capsules per group in <see cref="Occluders"/>.</summary>
        public const int MaxOccluderGroups = 12, MaxOccludersPerGroup = 8;

        /// <summary>
        /// KotH patch: capsules not in the voxels (figures) that darken the bounce and the
        /// sky, filled every frame: group g, capsule k at 2 (g * OccluderGroupSize + k), end a
        /// with the radius in w (0 = none), then end b; a group's sphere (centre, reach) in
        /// <see cref="OccluderGroups"/>.
        /// </summary>
        [DataMemberIgnore]
        public Vector4[] Occluders { get; } = new Vector4[2 * MaxOccluderGroups * MaxOccludersPerGroup];

        /// <summary>KotH patch: the sphere round each group's capsules and their reach.</summary>
        [DataMemberIgnore]
        public Vector4[] OccluderGroups { get; } = new Vector4[MaxOccluderGroups];

        /// <summary>KotH patch: groups used.</summary>
        [DataMemberIgnore]
        public int OccluderGroupCount { get; set; }

        /// <summary>KotH patch: capsules per group.</summary>
        [DataMemberIgnore]
        public int OccluderGroupSize { get; set; }

        /// <summary>KotH patch: a capsule reaches this many radii from its axis, fading out.</summary>
        [DataMemberIgnore]
        public float OccluderFade { get; set; } = 4.0f;

        public bool Update(RenderLight light)
        {
            return true;
        }
    }
}
