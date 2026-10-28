using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// A map's height fog and cloud layer, drawn by <see cref="AtmosphereEffects"/>
    /// (the clouds' shadow by <c>CascadeShadowReceiver</c>). The fog starts at
    /// <see cref="FogStart"/>, grows to <see cref="FogPeak"/> and fades out by
    /// <see cref="FogTop"/>, gathered round <see cref="FogCentre"/>. The clouds are
    /// a thin sheet of cloudlets from <see cref="CloudMap"/> drifting with the wind.
    /// A low coastal fog lies on the water in a ring round the centre, haze and
    /// wisps (the map's blue) drifting with the wind. Only for the picture; maps
    /// without it have none of them.
    /// </summary>
    /// <userdoc>Höhennebel, Küstennebel und Wolkenschicht der Map (nur Darstellung).</userdoc>
    [DataContract("Atmosphere")]
    [Display("Atmosphäre")]
    public sealed class Atmosphere : SyncScript
    {
        public static readonly PropertyKey<Atmosphere> Key = new("Atmosphere", typeof(Atmosphere));

        /// <userdoc>Höhe in Metern, ab der der Nebel beginnt.</userdoc>
        [DataMember(10)] public float FogStart { get; set; } = 30f;
        /// <userdoc>Höhe in Metern, in der der Nebel am dichtesten ist.</userdoc>
        [DataMember(11)] public float FogPeak { get; set; } = 123f;
        /// <userdoc>Höhe in Metern, bis zu der der Nebel über dem dichtesten Punkt ausläuft.</userdoc>
        [DataMember(12)] public float FogTop { get; set; } = 165f;
        /// <userdoc>Dichte pro Meter am dichtesten Punkt in der Mitte (0,008: auf 100 m gut die Hälfte Nebel).</userdoc>
        [DataMember(13)] public float FogDensity { get; set; } = 0.008f;
        /// <userdoc>Anteil der Dichte fern von der Mitte (1 = überall gleich, 0 = nur um die Mitte).</userdoc>
        [DataMember(14)] public float FogBase { get; set; } = 0.1f;
        /// <userdoc>Mitte des Nebels (x, z in Metern), etwa der Berg.</userdoc>
        [DataMember(15)] public Vector2 FogCentre { get; set; }
        /// <userdoc>Radius in Metern, über den der Nebel zur Mitte hin dichter wird.</userdoc>
        [DataMember(16)] public float FogRadius { get; set; } = 110f;
        /// <userdoc>Farbe des Nebels (linear), etwa der Horizont des Himmels.</userdoc>
        [DataMember(17)] public Color3 FogColor { get; set; } = new(0.56f, 0.68f, 0.84f);
        /// <userdoc>Wie viel Sonnenlicht der Nebel zur Sonne hin streut.</userdoc>
        [DataMember(18)] public float FogSun { get; set; } = 0.35f;
        /// <userdoc>Weiter als so viele Meter rechnet der Nebel nicht (Himmel, Horizont).</userdoc>
        [DataMember(19)] public float FogDistance { get; set; } = 1500f;
        /// <userdoc>Strahlen zur Sonne hin im Bild (Berg, Wolken, Bäume schatten sie ab), nur mit Blick Richtung Sonne (0 = keine).</userdoc>
        [DataMember(20)] public float SunRays { get; set; } = 0.7f;

        /// <userdoc>Wolkenkarte: rot die Wölkchen (Höhe), grün die großen Streifen und Lücken, blau die Schwaden des Küstennebels (Tools/maps/Clouds.cs).</userdoc>
        [DataMember(30)] public Texture CloudMap { get; set; }
        /// <userdoc>Untere Kante der Wolkenschicht in Metern.</userdoc>
        [DataMember(31)] public float CloudBase { get; set; } = 78f;
        /// <userdoc>Höhe der größten Wölkchen über der Unterkante in Metern.</userdoc>
        [DataMember(32)] public float CloudThickness { get; set; } = 7.0f;
        /// <userdoc>Meter, nach denen sich die Wölkchen wiederholen.</userdoc>
        [DataMember(33)] public float CloudTile { get; set; } = 540f;
        /// <userdoc>Meter, nach denen sich die großen Streifen wiederholen.</userdoc>
        [DataMember(34)] public float BandTile { get; set; } = 900f;
        /// <userdoc>Bedeckung von 0 (klar) bis 1 (geschlossen).</userdoc>
        [DataMember(35)] public float CloudCoverage { get; set; } = 0.1f;
        /// <userdoc>Dichte pro Meter im Inneren eines Wölkchens.</userdoc>
        [DataMember(36)] public float CloudDensity { get; set; } = 0.2f;
        /// <userdoc>Farbe der sonnigen Oberseite (linear).</userdoc>
        [DataMember(37)] public Color3 CloudTop { get; set; } = new(0.97f, 0.95f, 0.91f);
        /// <userdoc>Farbe der Unterseite im Schatten (linear), bläulich grau.</userdoc>
        [DataMember(38)] public Color3 CloudBottom { get; set; } = new(0.50f, 0.56f, 0.70f);
        /// <userdoc>Weiter als so viele Meter blendet die Wolkenschicht aus.</userdoc>
        [DataMember(39)] public float CloudDistance { get; set; } = 3000f;
        /// <userdoc>Windrichtung in Grad (0 = nach −Z, 90 = nach +X).</userdoc>
        [DataMember(40)] public float WindHeading { get; set; } = 60f;
        /// <userdoc>Windgeschwindigkeit in m/s, mit der die Wolken ziehen.</userdoc>
        [DataMember(41)] public float WindSpeed { get; set; } = 1.0f;
        /// <userdoc>Wie dunkel der Schatten einer Wolke ist (0 = keiner, 1 = ganz).</userdoc>
        [DataMember(42)] public float CloudShadow { get; set; } = 0.45f;

        /// <userdoc>Dichte pro Meter des Küstennebels in seinen Schwaden, direkt über dem Wasser (0 = kein Küstennebel).</userdoc>
        [DataMember(50)] public float CoastDensity { get; set; } = 0.06f;
        /// <userdoc>Anteil des gleichmäßigen Dunsts zwischen den Schwaden (0 = nur Schwaden, 1 = überall gleich).</userdoc>
        [DataMember(51)] public float CoastHaze { get; set; } = 0.15f;
        /// <userdoc>Höhe des Wassers in Metern, auf dem der Küstennebel liegt.</userdoc>
        [DataMember(52)] public float CoastLevel { get; set; } = 0.4f;
        /// <userdoc>Meter über dem Wasser, bis zu denen der Küstennebel ausläuft.</userdoc>
        [DataMember(53)] public float CoastTop { get; set; } = 4.5f;
        /// <userdoc>Abstand zur Mitte in Metern, ab dem der Küstennebel einsetzt (x) und voll ist (y): die Küste.</userdoc>
        [DataMember(54)] public Vector2 CoastInner { get; set; } = new(118f, 135f);
        /// <userdoc>Abstand zur Mitte in Metern, ab dem der Küstennebel dünner wird (x) und weg ist (y).</userdoc>
        [DataMember(55)] public Vector2 CoastOuter { get; set; } = new(270f, 520f);
        /// <userdoc>Meter, nach denen sich die Schwaden wiederholen.</userdoc>
        [DataMember(56)] public float CoastTile { get; set; } = 260f;
        /// <userdoc>Farbe des Küstennebels (linear), heller als der Höhennebel.</userdoc>
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
