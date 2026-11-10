using UnityEngine;

namespace KotH.MapKit
{
    /// <summary>The object and its children stay in the kit: helpers such as the scale figures.
    /// Objects tagged EditorOnly and inactive ones are left out as well.</summary>
    [AddComponentMenu("KotH map/Map ignore")]
    [DisallowMultipleComponent]
    public sealed class MapIgnore : MonoBehaviour
    {
    }
}
