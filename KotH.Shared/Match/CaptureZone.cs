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

        /// <userdoc>So lange muss man allein in der Zone stehen, um sie einzunehmen, in Sekunden. Danach gibt es einen Punkt pro Sekunde, solange man drin bleibt, auch wenn andere dazukommen.</userdoc>
        [Display("Aufwärmen", "Regeln")] public float WarmupSeconds = 3f;

        /// <userdoc>Wer so viele Punkte hat, gewinnt die Runde sofort.</userdoc>
        [Display("Siegpunkte", "Regeln")] public int WinPoints = 100;

        /// <userdoc>Länge einer Runde in Minuten. Danach gewinnt, wer die meisten Punkte hat; bei Gleichstand ist sie unentschieden.</userdoc>
        [Display("Zeitlimit", "Regeln")] public float TimeLimitMinutes = 20f;

        /// <userdoc>So lange steht der Sieger da, bevor die nächste Runde beginnt, in Sekunden.</userdoc>
        [Display("Siegerpause", "Regeln")] public float WinPauseSeconds = 15f;

        /// <userdoc>Im Mehrspieler: So lange läuft der Countdown, sobald genug Spieler da sind, in Sekunden.</userdoc>
        [Display("Countdown", "Mehrspieler")] public float CountdownSeconds = 60f;

        /// <userdoc>Im Mehrspieler: So lange nach dem Start einer Runde darf noch jemand beitreten, in Sekunden.</userdoc>
        [Display("Beitritt bis", "Mehrspieler")] public float JoinSeconds = 60f;

        /// <userdoc>Wer aus der Arena fliegt und unter diese Höhe fällt, steht wieder an seinem Startplatz (mit Ring: an einem neuen Platz im Ring), in Metern. Schadensprozent und Punkte bleiben.</userdoc>
        [Display("Respawn unter (m)", "Regeln")] public float RespawnBelow = -5f;

        /// <userdoc>Bei Rundenbeginn und nach jedem Respawn steht jeder an einem zufälligen Platz in diesem Ring um die Zone, Blick zur Zone: innerer Abstand von ihrer Mitte, waagerecht, in Metern. Außen 0: feste Startplätze.</userdoc>
        [Display("Ring innen (m)", "Startplätze")] public float SpawnRingInner = 0f;

        /// <userdoc>Äußerer Abstand des Rings von der Mitte der Zone, in Metern. 0 schaltet den Ring ab, dann gelten die Startplätze.</userdoc>
        [Display("Ring außen (m)", "Startplätze")] public float SpawnRingOuter = 0f;

        /// <userdoc>So weit stehen zwei Spieler im Ring mindestens auseinander, in Metern.</userdoc>
        [Display("Abstand", "Startplätze")] public float SpawnSpacing = 6f;
    }
}
