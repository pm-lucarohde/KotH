using UnityEngine;

namespace KotH.MapKit
{
    /// <summary>Where a player starts: feet at the object's position. Place one per player you
    /// expect (up to 12; fewer are shared). Never exported as geometry.</summary>
    [AddComponentMenu("KotH map/Map spawn")]
    public sealed class MapSpawn : MonoBehaviour
    {
        [Tooltip("Look at the zone; off: look along the object's forward (blue arrow).")]
        public bool faceZone = true;

        // A player's size: 1.8 m tall, 0.8 m wide, and where it looks.
        void OnDrawGizmos()
        {
            Vector3 feet = transform.position;
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 1f);
            Gizmos.DrawWireCube(feet + Vector3.up * 0.9f, new Vector3(0.8f, 1.8f, 0.8f));
            Vector3 look = transform.forward;
            if (faceZone && FindFirstObjectByType<MapZone>() is MapZone zone)
                look = zone.transform.position - feet;
            look.y = 0f;
            if (look.sqrMagnitude > 1e-6f)
                Gizmos.DrawLine(feet + Vector3.up * 1.5f, feet + Vector3.up * 1.5f + look.normalized * 1.5f);
        }
    }
}
