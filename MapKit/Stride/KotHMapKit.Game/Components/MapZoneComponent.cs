using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.MapKit
{
    /// <summary>The hill's zone: the entity's position is its centre and its scale its size in
    /// metres (the kit's zone box shows it). Exactly one per map; never exported as geometry.</summary>
    [DataContract("KotH.MapKit.MapZone")]
    [Display("Map zone")]
    [ComponentCategory("KotH map")]
    public sealed class MapZoneComponent : EntityComponent
    {
        /// <userdoc>Seconds alone inside to take the zone.</userdoc>
        [DataMember(10)] public float CaptureSeconds { get; set; } = 3f;
        /// <userdoc>Points that win the round (one per second held).</userdoc>
        [DataMember(20)] public int WinPoints { get; set; } = 100;
        [DataMember(30)] public float TimeLimitMinutes { get; set; } = 20f;
        /// <userdoc>Players below this height (metres) respawn.</userdoc>
        [DataMember(40)] public float RespawnBelow { get; set; } = -5f;
        /// <userdoc>Respawns on a ring round the zone, from this radius ... (0: the spawn points)</userdoc>
        [DataMember(50)] public float RingInner { get; set; }
        /// <userdoc>... to this one.</userdoc>
        [DataMember(60)] public float RingOuter { get; set; }
        [DataMember(70)] public float RingSpacing { get; set; } = 6f;
        [DataMember(80)] public float WinPauseSeconds { get; set; } = 15f;
        [DataMember(90)] public float CountdownSeconds { get; set; } = 60f;
        [DataMember(100)] public float JoinSeconds { get; set; } = 60f;
    }
}
