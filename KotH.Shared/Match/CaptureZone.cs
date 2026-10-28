using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.Shared.Match
{
    /// <summary>The hill: a box in the arena and the rules of the round played for it.
    /// Carries no collider, the box is asked with plain arithmetic, so the zone needs no
    /// physics layer and stays out of every motor query. Read once when a HillMatch
    /// starts; moving it later changes nothing.</summary>
    [DataContract("KotH.CaptureZone")]
    [Display("Capture zone")]
    [ComponentCategory("KotH")]
    public sealed class CaptureZone : EntityComponent
    {
        /// <userdoc>Width, height and depth of the zone in metres, around the entity's centre. What counts is where the body's centre stands.</userdoc>
        [Display("Size", "Zone")] public Vector3 Size = new Vector3(12f, 3.5f, 12f);

        /// <userdoc>How long one must stand alone in the zone to capture it, in seconds. Then it scores a point per second while one stays inside, even when others join.</userdoc>
        [Display("Capture time", "Rules")] public float WarmupSeconds = 3f;

        /// <userdoc>Whoever has this many points wins the round at once.</userdoc>
        [Display("Win points", "Rules")] public int WinPoints = 100;

        /// <userdoc>Length of a round in minutes. Then the most points win; a tie is a draw.</userdoc>
        [Display("Time limit", "Rules")] public float TimeLimitMinutes = 20f;

        /// <userdoc>How long the winner stands there before the next round starts, in seconds.</userdoc>
        [Display("Winner pause", "Rules")] public float WinPauseSeconds = 15f;

        /// <userdoc>Multiplayer: how long the countdown runs once enough players are there, in seconds.</userdoc>
        [Display("Countdown", "Multiplayer")] public float CountdownSeconds = 60f;

        /// <userdoc>Multiplayer: how long after a round starts someone may still join, in seconds.</userdoc>
        [Display("Join until", "Multiplayer")] public float JoinSeconds = 60f;

        /// <userdoc>Whoever falls below this height respawns at a spawn point (with a ring: a new place in the ring), in metres. Damage percent and points stay.</userdoc>
        [Display("Respawn below (m)", "Rules")] public float RespawnBelow = -5f;

        /// <userdoc>At round start and after each respawn everyone stands at a random place in this ring around the zone, facing it: inner horizontal distance from its centre, in metres. Outer 0: fixed spawn points.</userdoc>
        [Display("Ring inner (m)", "Spawns")] public float SpawnRingInner = 0f;

        /// <userdoc>Outer distance of the ring from the zone's centre, in metres. 0 turns the ring off; the spawn points apply.</userdoc>
        [Display("Ring outer (m)", "Spawns")] public float SpawnRingOuter = 0f;

        /// <userdoc>Minimum distance between two players in the ring, in metres.</userdoc>
        [Display("Spacing", "Spawns")] public float SpawnSpacing = 6f;
    }
}
