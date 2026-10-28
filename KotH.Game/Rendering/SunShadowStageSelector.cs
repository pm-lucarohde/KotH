using Stride.Core;
using Stride.Rendering;
using Stride.Rendering.Shadows;
using Stride.Rendering.Voxels.VoxelGI;

namespace KotH.Client.Rendering
{
    /// <summary>Stride's shadow caster selector, split: meshes with lightmap UVs (terrain, trunks,
    /// leaves; their shadow keeps the wind of its moment) go to <see cref="StaticRenderStage"/>,
    /// drawn once; the rest (figures) to the per-frame stage. Without a static stage as before.</summary>
    [DataContract("SunShadowStageSelector")]
    public sealed class SunShadowStageSelector : ShadowMapRenderStageSelector
    {
        /// <userdoc>Stage for the casters that never move (map with lightmap); empty = all every frame.</userdoc>
        public RenderStage StaticRenderStage { get; set; }

        /// <summary>The mesh never moves: it belongs to a model with lightmap UVs (the map).</summary>
        public static bool IsStatic(RenderMesh mesh)
        {
            foreach (RenderMesh part in mesh.RenderModel?.Meshes ?? [mesh])
                if (part != null && LightmapRenderFeature.HasSecondUV(part))
                    return true;
            return false;
        }

        public override void Process(RenderObject renderObject)
        {
            if (StaticRenderStage != null && renderObject is RenderMesh { IsShadowCaster: true } mesh
                && ((1u << (int)mesh.RenderGroup) & (uint)RenderGroup) != 0 && IsStatic(mesh))
                mesh.ActiveRenderStages[StaticRenderStage.Index] = new ActiveRenderStage(EffectName);
            else
                base.Process(renderObject);
        }
    }
}
