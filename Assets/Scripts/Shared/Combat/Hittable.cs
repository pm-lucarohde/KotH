using UnityEngine;

namespace KotH.Shared.Combat
{
    /// <summary>
    /// Marks a body that shots may hit. Nothing but a marker on purpose:
    /// health, damage and hit zones are not decided yet, and RaycastHit
    /// already carries the point of impact. This is where they attach.
    /// </summary>
    public sealed class Hittable : MonoBehaviour
    {
    }
}
