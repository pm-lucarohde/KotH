using UnityEngine;

namespace KotH.MapKit
{
    /// <summary>The hill's zone: the object's position is its centre and its scale its size in
    /// metres (drawn as a yellow box). Exactly one per map; never exported as geometry.</summary>
    [AddComponentMenu("KotH map/Map zone")]
    [DisallowMultipleComponent]
    public sealed class MapZone : MonoBehaviour
    {
        [Tooltip("Seconds alone inside to take the zone.")]
        public float captureSeconds = 3f;
        [Tooltip("Points that win the round (one per second held).")]
        public int winPoints = 100;
        public float timeLimitMinutes = 20f;
        [Tooltip("Players below this height (metres) respawn.")]
        public float respawnBelow = -5f;
        [Tooltip("Respawns on a ring round the zone, from this radius ... (0: the spawn points)")]
        public float ringInner;
        [Tooltip("... to this one.")]
        public float ringOuter;
        public float ringSpacing = 6f;
        public float winPauseSeconds = 15f;
        public float countdownSeconds = 60f;
        public float joinSeconds = 60f;

        void OnDrawGizmos()
        {
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, transform.lossyScale);
            Gizmos.color = new Color(1f, 0.75f, 0.1f, 0.25f);
            Gizmos.DrawCube(Vector3.zero, Vector3.one);
            Gizmos.color = new Color(1f, 0.75f, 0.1f, 1f);
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
        }
    }
}
