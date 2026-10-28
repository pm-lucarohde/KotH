using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

namespace KotH.Client.Rendering
{
    /// <summary>A map's height fog (from <see cref="FogStart"/> to <see cref="FogPeak"/>, gone by
    /// <see cref="FogTop"/>, round <see cref="FogCentre"/>), cloudlet sheet from
    /// <see cref="CloudMap"/> and coastal fog, drifting with the wind; drawn by AtmosphereEffects.</summary>
    /// <userdoc>Height fog, coastal fog and cloud layer of the map (visuals only).</userdoc>
    [DataContract("Atmosphere")]
    [Display("Atmosphere")]
    public sealed class Atmosphere : SyncScript
    {
        public static readonly PropertyKey<Atmosphere> Key = new("Atmosphere", typeof(Atmosphere));

        /// <userdoc>Height in metres where the fog starts.</userdoc>
        [DataMember(10)] public float FogStart { get; set; } = 30f;
        /// <userdoc>Height in metres where the fog is densest.</userdoc>
        [DataMember(11)] public float FogPeak { get; set; } = 123f;
        /// <userdoc>Height in metres up to which the fog fades out above the densest point.</userdoc>
        [DataMember(12)] public float FogTop { get; set; } = 165f;
        /// <userdoc>Density per metre at the densest point in the centre (0.008: over 100 m a good half fog).</userdoc>
        [DataMember(13)] public float FogDensity { get; set; } = 0.008f;
        /// <userdoc>Share of the density far from the centre (1 = the same everywhere, 0 = only around the centre).</userdoc>
        [DataMember(14)] public float FogBase { get; set; } = 0.1f;
        /// <userdoc>Centre of the fog (x, z in metres), about the mountain.</userdoc>
        [DataMember(15)] public Vector2 FogCentre { get; set; }
        /// <userdoc>Radius in metres over which the fog thickens towards the centre.</userdoc>
        [DataMember(16)] public float FogRadius { get; set; } = 110f;
        /// <userdoc>Fog colour (linear), about the sky's horizon.</userdoc>
        [DataMember(17)] public Color3 FogColor { get; set; } = new(0.56f, 0.68f, 0.84f);
        /// <userdoc>How much sunlight the fog scatters towards the sun.</userdoc>
        [DataMember(18)] public float FogSun { get; set; } = 0.35f;
        /// <userdoc>The fog is not computed beyond this many metres (sky, horizon).</userdoc>
        [DataMember(19)] public float FogDistance { get; set; } = 1500f;
        /// <userdoc>Rays towards the sun on screen (mountain, clouds, trees shade them), only when looking towards the sun (0 = none).</userdoc>
        [DataMember(20)] public float SunRays { get; set; } = 0.35f;

        /// <userdoc>Cloud map: red the cloudlets (height), green the large bands and gaps, blue the coastal fog wisps (Tools/maps/Clouds.cs).</userdoc>
        [DataMember(30)] public Texture CloudMap { get; set; }
        /// <userdoc>Lower edge of the cloud layer in metres.</userdoc>
        [DataMember(31)] public float CloudBase { get; set; } = 78f;
        /// <userdoc>Height of the largest cloudlets above the lower edge in metres.</userdoc>
        [DataMember(32)] public float CloudThickness { get; set; } = 7.0f;
        /// <userdoc>Metres after which the cloudlets repeat.</userdoc>
        [DataMember(33)] public float CloudTile { get; set; } = 540f;
        /// <userdoc>Metres after which the large bands repeat.</userdoc>
        [DataMember(34)] public float BandTile { get; set; } = 900f;
        /// <userdoc>Coverage from 0 (clear) to 1 (overcast).</userdoc>
        [DataMember(35)] public float CloudCoverage { get; set; } = 0.1f;
        /// <userdoc>Density per metre inside a cloudlet.</userdoc>
        [DataMember(36)] public float CloudDensity { get; set; } = 0.2f;
        /// <userdoc>Colour of the sunny top (linear).</userdoc>
        [DataMember(37)] public Color3 CloudTop { get; set; } = new(0.97f, 0.95f, 0.91f);
        /// <userdoc>Colour of the shaded underside (linear), bluish grey.</userdoc>
        [DataMember(38)] public Color3 CloudBottom { get; set; } = new(0.50f, 0.56f, 0.70f);
        /// <userdoc>Beyond this many metres the cloud layer fades out.</userdoc>
        [DataMember(39)] public float CloudDistance { get; set; } = 3000f;
        /// <userdoc>Wind direction in degrees (0 = towards −Z, 90 = towards +X).</userdoc>
        [DataMember(40)] public float WindHeading { get; set; } = 60f;
        /// <userdoc>Wind speed in m/s the clouds drift with.</userdoc>
        [DataMember(41)] public float WindSpeed { get; set; } = 1.0f;
        /// <userdoc>How dark a cloud's shadow is (0 = none, 1 = full).</userdoc>
        [DataMember(42)] public float CloudShadow { get; set; } = 0.45f;

        /// <userdoc>Density per metre of the coastal fog in its wisps, right above the water (0 = no coastal fog).</userdoc>
        [DataMember(50)] public float CoastDensity { get; set; } = 0.06f;
        /// <userdoc>Share of even haze between the wisps (0 = wisps only, 1 = the same everywhere).</userdoc>
        [DataMember(51)] public float CoastHaze { get; set; } = 0.15f;
        /// <userdoc>Height of the water in metres the coastal fog lies on.</userdoc>
        [DataMember(52)] public float CoastLevel { get; set; } = 0.4f;
        /// <userdoc>Metres above the water up to which the coastal fog fades out.</userdoc>
        [DataMember(53)] public float CoastTop { get; set; } = 4.5f;
        /// <userdoc>Distance from the centre in metres where the coastal fog starts (x) and is full (y): the coast.</userdoc>
        [DataMember(54)] public Vector2 CoastInner { get; set; } = new(118f, 135f);
        /// <userdoc>Distance from the centre in metres where the coastal fog thins (x) and is gone (y).</userdoc>
        [DataMember(55)] public Vector2 CoastOuter { get; set; } = new(270f, 520f);
        /// <userdoc>Metres after which the wisps repeat.</userdoc>
        [DataMember(56)] public float CoastTile { get; set; } = 260f;
        /// <userdoc>Colour of the coastal fog (linear), lighter than the height fog.</userdoc>
        [DataMember(57)] public Color3 CoastColor { get; set; } = new(0.82f, 0.86f, 0.90f);

        /// <summary>The wind's direction in x, z: texture x of the cloud map runs along it.</summary>
        [DataMemberIgnore] public Vector2 WindAxis { get; private set; } = Vector2.UnitX;

        /// <summary>Where the wind has carried both layers, in their texture's repeats: cloudlets xy, streaks zw.</summary>
        [DataMemberIgnore] public Vector4 CloudOffset { get; private set; }

        /// <summary>Where the wind has carried the coastal fog's wisps, in their texture's repeats along it.</summary>
        [DataMemberIgnore] public float CoastOffset { get; private set; }

        double cloudlets, streaks, wisps;

        public override void Update()
        {
            double heading = MathUtil.DegreesToRadians(WindHeading);
            WindAxis = new Vector2((float)Math.Sin(heading), (float)-Math.Cos(heading));
            // The streaks drift a little slower, so the sheet changes as it goes.
            double travel = WindSpeed * Game.UpdateTime.Elapsed.TotalSeconds;
            cloudlets = Wrap(cloudlets + travel / Math.Max(CloudTile, 1f));
            streaks = Wrap(streaks + 0.8 * travel / Math.Max(BandTile, 1f));
            // Down at the water the wind is weaker.
            wisps = Wrap(wisps + 0.6 * travel / Math.Max(CoastTile, 1f));
            CloudOffset = new Vector4((float)cloudlets, 0f, (float)streaks, 0f);
            CoastOffset = (float)wisps;
            RenderContext.GetShared(Services).Tags.Set(Key, this);
        }

        public override void Cancel()
        {
            var tags = RenderContext.GetShared(Services).Tags;
            if (tags.Get(Key) == this)
                tags.Remove(Key);
        }

        static double Wrap(double x) => x - Math.Floor(x);
    }
}
