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

        /// <summary>
        /// Anzahl der Sprungtasten-Drücke seit dem letzten Tick.
        ///
        /// Ein Zähler statt eines bool, weil ein Tick bei 40 Hz 25 ms dauert:
        /// ein schneller Doppeltipp kann komplett hineinfallen, und mit einem
        /// bool ginge der zweite Druck verloren. Die Doppelsprung-Geste wird
        /// daraus im PlayerMotor erkannt — nicht im Client, damit ein Server
        /// sie aus demselben Eingabestrom nachrechnen kann.
        /// </summary>
        public byte JumpPresses;

        public bool Sprint;
        public bool Crouch;
    }
}
