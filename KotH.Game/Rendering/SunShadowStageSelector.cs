using Stride.Core;
using Stride.Rendering;
using Stride.Rendering.Shadows;
using Stride.Rendering.Voxels.VoxelGI;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// Stride's shadow caster selector, split for the static sun shadow: the meshes
    /// of a model with lightmap UVs (the map's terrain, trunks and leaves, which never
    /// move; the leaves' shadow keeps the wind of the moment it is drawn) go to
    /// <see cref="StaticRenderStage"/>, drawn once after loading; all others (the
    /// figures) stay in the shadow map stage of every frame. Without a static stage
    /// everything casts as before.
    /// </summary>
    [DataContract("SunShadowStageSelector")]
    public sealed class SunShadowStageSelector : ShadowMapRenderStageSelector
    {
        /// <userdoc>Stufe für die Werfer, die sich nie bewegen (Map mit Lightmap); leer = alle jeden Frame.</userdoc>
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
