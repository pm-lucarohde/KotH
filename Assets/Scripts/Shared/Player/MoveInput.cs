using UnityEngine;

namespace KotH.Shared.Player
{
    /// <summary>
    /// Die Spielereingabe eines einzelnen Ticks.
    ///
    /// Enthält bewusst ausschließlich Werttypen. Dadurch bleibt das Struct
    /// kopierbar und lässt sich später unverändert über das Netzwerk schicken —
    /// genau das ist der Grund, warum Eingabe und Bewegung getrennt sind.
    ///
    /// Yaw und Pitch sind absolute Winkel, keine Mausdeltas: verlorene oder
    /// umsortierte Pakete können die Blickrichtung dann nicht dauerhaft
    /// verdrehen, weil der Empfänger nichts aufsummieren muss.
    /// </summary>
    public struct MoveInput
    {
        /// <summary>x = seitwärts, y = vorwärts. Jeweils -1..1.</summary>
        public Vector2 Move;

        /// <summary>Absolute Drehung um die Hochachse, in Grad.</summary>
        public float Yaw;

        /// <summary>Absoluter Blickwinkel hoch/runter, in Grad, bereits geklemmt.</summary>
        public float Pitch;

        /// <summary>Flanke: wurde seit dem letzten Tick gedrückt. Kein Dauerzustand.</summary>
        public bool Jump;

        public bool Sprint;
        public bool Crouch;
    }
}
