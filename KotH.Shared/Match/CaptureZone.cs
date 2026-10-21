using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.Shared.Match
{
    /// <summary>
    /// The hill: a box in the arena and the rules of the round played for it.
    /// Carries no collider, the box is asked with plain arithmetic, so the
    /// zone needs no physics layer and stays out of every motor query.
    /// Read once when a HillMatch starts; moving it later changes nothing.
    /// </summary>
    [DataContract("KotH.CaptureZone")]
    [Display("Capture-Zone")]
    [ComponentCategory("KotH")]
    public sealed class CaptureZone : EntityComponent
    {
        /// <userdoc>Breite, Höhe und Tiefe der Zone in Metern, um den Mittelpunkt des Entitys. Zählt, wo die Körpermitte steht.</userdoc>
        [Display("Größe", "Zone")] public Vector3 Size = new Vector3(12f, 3.5f, 12f);

        /// <userdoc>So lange muss man allein in der Zone stehen, bevor Punkte kommen, in Sekunden. Danach gibt es einen Punkt pro Sekunde.</userdoc>
        [Display("Aufwärmen", "Regeln")] public float WarmupSeconds = 3f;

        /// <userdoc>Wer so viele Punkte hat, gewinnt die Runde sofort.</userdoc>
        [Display("Siegpunkte", "Regeln")] public int WinPoints = 150;

        /// <userdoc>Länge einer Runde in Minuten. Danach gewinnt, wer die meisten Punkte hat; bei Gleichstand ist sie unentschieden.</userdoc>
        [Display("Zeitlimit", "Regeln")] public float TimeLimitMinutes = 20f;

        /// <userdoc>So lange steht der Sieger da, bevor die nächste Runde beginnt, in Sekunden.</userdoc>
        [Display("Siegerpause", "Regeln")] public float WinPauseSeconds = 5f;

        /// <userdoc>Im Mehrspieler: So lange läuft der Countdown, sobald genug Spieler da sind, in Sekunden.</userdoc>
        [Display("Countdown", "Mehrspieler")] public float CountdownSeconds = 60f;

        /// <userdoc>Im Mehrspieler: So lange nach dem Start einer Runde darf noch jemand beitreten, in Sekunden.</userdoc>
        [Display("Beitritt bis", "Mehrspieler")] public float JoinSeconds = 60f;

        /// <userdoc>Wer aus der Arena fliegt und unter diese Höhe fällt, steht wieder an seinem Startplatz, in Metern. Schadensprozent und Punkte bleiben.</userdoc>
        [Display("Respawn unter (m)", "Regeln")] public float RespawnBelow = -5f;
    }
}
