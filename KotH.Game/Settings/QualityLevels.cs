using System.Collections.Generic;
using Stride.Engine;
using Stride.Rendering;

namespace KotH.Client.Settings
{
    /// <summary>What each graphics level changes; High is always the assets' own value (today's look).
    /// The renderers ask here every frame, BakeVoxels bakes the map again when shadows or lighting change.</summary>
    public static class QualityLevels
    {
        static PlayerSettings S => ClientSettings.Applied;

        /// <summary>Edge of the sheet the moving casters' shadow tiles share.</summary>
        public static int ShadowSheet(int asset) => S.Shadows switch { Quality.Low => asset / 2, Quality.Ultra => asset * 2, _ => asset };

        /// <summary>The tiles' texels as a multiple of the asset's (High: about one per pixel).</summary>
        public static float ShadowTexel => S.Shadows switch { Quality.Low => 4f, Quality.Medium => 2f, Quality.Ultra => 0.5f, _ => 1f };

        /// <summary>The static sun shadow's edge (the map's shadows, baked into the lightmap too).</summary>
        public static int StaticSunSize(int asset) => S.Shadows switch { Quality.Low => asset / 2, Quality.Ultra => asset * 2, _ => asset };

        /// <summary>The map's lightmap edge.</summary>
        public static int LightmapSize(int asset) => S.Lighting == Quality.Low ? asset / 2 : asset;

        /// <summary>The GI cones' steps (7 in the asset: 4, 5, 7, 9).</summary>
        public static int ConeSteps(int asset) => S.Lighting switch
        {
            Quality.Low => asset - 3, Quality.Medium => asset - 2, Quality.Ultra => asset + 2, _ => asset,
        };

        /// <summary>Fog and clouds are marched at the screen's size divided by this.</summary>
        public static int MarchDivisor => S.Volumetrics switch { Quality.Low => 4, Quality.Medium => 3, Quality.Ultra => 1, _ => 2 };

        /// <summary>How far grass and shells reach, as a share of the assets' reach.</summary>
        public static float Reach => S.EffectsQuality switch { Quality.Low => 0.5f, Quality.Medium => 0.75f, Quality.Ultra => 1.5f, _ => 1f };

        /// <summary>Mip levels added to the painted textures' bias (they are a quarter sharper already).</summary>
        public static float TextureBias => S.Textures switch { Quality.Low => 1.5f, Quality.Medium => 0.75f, Quality.Ultra => -0.25f, _ => 0f };

        // One field each: Stride's assembly processor names keys by their field (an array broke the build).
        static readonly ValueParameterKey<float> TerrainBias = ParameterKeys.NewValue(0f, "TerrainTriplanar.TextureBias");
        static readonly ValueParameterKey<float> PaintedBias = ParameterKeys.NewValue(0f, "PaintedTriplanar.TextureBias");

        /// <summary>The texture level on every material of these entities and their children (the map).</summary>
        public static void ApplyTextures(IEnumerable<Entity> entities)
        {
            float bias = TextureBias;
            foreach (Entity e in entities)
                Textures(e, bias);
        }

        static void Textures(Entity e, float bias)
        {
            if (e.Get<ModelComponent>() is { } model)
            {
                foreach (var m in model.Model?.Materials ?? [])
                    Set(m.Material, bias);
                foreach (var m in model.Materials)
                    Set(m.Value, bias);
            }
            foreach (TransformComponent child in e.Transform.Children)
                Textures(child.Entity, bias);
        }

        static void Set(Material material, float bias)
        {
            if (material == null)
                return;
            foreach (var pass in material.Passes)
            {
                pass.Parameters.Set(TerrainBias, bias);
                pass.Parameters.Set(PaintedBias, bias);
            }
        }
    }
}
