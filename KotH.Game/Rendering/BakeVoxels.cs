using KotH.Client.Hud;
using KotH.Client.Settings;
using Stride.Engine;
using Stride.Rendering;
using Stride.Rendering.Voxels;
using Stride.Rendering.Voxels.VoxelGI;

namespace KotH.Client.Rendering
{
    /// <summary>Voxelises the map once after loading and keeps it (map and sun never change);
    /// figures come as capsules (FigureOccluders). Then bakes the lightmap and tells LoadScreen.</summary>
    /// <userdoc>Voxelises the map once after loading; the figures join as capsules.</userdoc>
    public sealed class BakeVoxels : SyncScript
    {
        /// <summary>Frames voxelised in full (clipmaps; sparse bricks say when they are done).</summary>
        /// <userdoc>Over this many frames the whole map is voxelised.</userdoc>
        public int Frames { get; set; } = 2;

        int done;
        bool sunLogged, voxelsLogged, lightmapLogged;

        // The Shadows and Lighting settings last applied, and the light's asset values.
        Quality? shadows, lighting;
        int assetLightmap = -1, assetSteps = -1;

        public override void Update()
        {
            if (Entity.Get<VoxelVolumeComponent>() is not { } volume)
                return;
            ApplyQuality();
            // Sparse bricks bake over as many frames as they need.
            if (volume.Storage is VoxelStorageSparse sparse)
            {
                bool sun = StaticShadowDrawn();
                volume.Voxelize = !sparse.Baked && sun;
                Once(ref sunLogged, sun, "static sun shadow drawn");
                Once(ref voxelsLogged, sparse.Baked, "voxels baked");
                if (sparse.Baked && LightmapBaked())
                {
                    Once(ref lightmapLogged, true, "lightmap baked");
                    Services.GetService<LoadScreen>()?.Baked();
                }
                return;
            }
            if (done > Frames)
            {
                Services.GetService<LoadScreen>()?.Baked();
                return;
            }
            volume.Voxelize = ++done <= Frames;
        }

        static void Once(ref bool logged, bool now, string what)
        {
            if (logged || !now)
                return;
            logged = true;
            StartLog.Mark(what);
        }

        /// <summary>The Lighting setting's lightmap size and cone steps, before the first bake; after a
        /// change of it or of Shadows (the static sun map) the map's shadow and lightmap are drawn anew
        /// (a second's hitch). The voxels stay: they hold no sun of their own beyond the static map.</summary>
        void ApplyQuality()
        {
            PlayerSettings s = ClientSettings.Applied;
            if (shadows == s.Shadows && lighting == s.Lighting || Entity.Get<LightComponent>()?.Type is not LightVoxel light)
                return;
            bool first = shadows == null;
            (shadows, lighting) = (s.Shadows, s.Lighting);
            if (assetLightmap < 0)
                assetLightmap = light.LightmapSize;
            light.LightmapSize = assetLightmap > 0 ? QualityLevels.LightmapSize(assetLightmap) : assetLightmap;
            if (light.DiffuseMarcher is VoxelMarchSetBase { Marcher: VoxelMarchConePerMipmap cone })
            {
                if (assetSteps < 0)
                    assetSteps = cone.Steps;
                cone.Steps = QualityLevels.ConeSteps(assetSteps);
            }
            if (first || light.LightmapSize <= 0)
                return;
            var tags = RenderContext.GetShared(Services).Tags;
            if (tags.Get(StaticSunShadowRenderer.Key) is { } sun)
                sun.Ready = false;
            tags.Set(ForwardRendererVoxels.ForgetLightmapKey, true);
            light.Lightmap = null;
        }

        // The voxels are lit with the map's shadow: with a lightmap the map casts only
        // into the static sun shadow, so that has to be complete first.
        bool StaticShadowDrawn()
        {
            if (Entity.Get<LightComponent>()?.Type is not LightVoxel { LightmapSize: > 0 })
                return true;
            return RenderContext.GetShared(Services).Tags.Get(StaticSunShadowRenderer.Key) is { Ready: true };
        }

        // Asks the renderer for the voxel light's lightmap until it is there (none wanted: at once).
        bool LightmapBaked()
        {
            if (Entity.Get<LightComponent>()?.Type is not LightVoxel light || light.LightmapSize <= 0 || light.Lightmap != null)
                return true;
            RenderContext.GetShared(Services).Tags.Set(ForwardRendererVoxels.LightmapRequestKey, light);
            return false;
        }
    }
}
