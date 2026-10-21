using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.Shared.Bots
{
    /// <summary>
    /// How the bots play and how their navmesh is cut. Sits on an entity of
    /// the arena prefab, so it loads headless with the arena; an arena
    /// without one plays with these defaults.
    /// </summary>
    [DataContract("KotH.BotSettings")]
    [Display("Bot-Einstellungen")]
    [ComponentCategory("KotH")]
    public sealed class BotSettings : EntityComponent
    {
        /// <userdoc>So viele Ticks alt ist alles, was ein Bot sieht (40 Ticks = 1 s). Weniger ist stärker; mindestens 3.</userdoc>
        [Display("Reaktionszeit (Ticks)", "Stärke")] public int ReactionTicks = 8;

        /// <userdoc>Alle so viele Ticks überlegt ein Bot neu, was er will und wohin er geht.</userdoc>
        [Display("Denkpause (Ticks)", "Stärke")] public int ThinkTicks = 6;

        /// <userdoc>So weit zielt ein Bot beim Schlag höchstens daneben, in Grad.</userdoc>
        [Display("Zielfehler (°)", "Stärke")] public float AimError = 4f;

        /// <userdoc>So schnell dreht sich ein Bot höchstens, in Grad pro Tick.</userdoc>
        [Display("Drehtempo (°/Tick)", "Stärke")] public float TurnRate = 24f;

        /// <userdoc>Gegner bis zu dieser Entfernung greift ein Bot an, in Metern.</userdoc>
        [Display("Angriffsweite (m)", "Entscheiden")] public float EngageRange = 9f;

        /// <userdoc>Absichten, die höchstens diesen Anteil schlechter sind als die beste, kommen per Zufall dran. 0 entscheidet immer gleich.</userdoc>
        [Display("Zufallsspielraum", "Entscheiden")] public float Randomness = 0.12f;

        /// <userdoc>Bonus für die Absicht, die gerade läuft, damit ein Bot nicht hin und her springt.</userdoc>
        [Display("Beharrlichkeit", "Entscheiden")] public float Persistence = 0.3f;

        /// <userdoc>Ticks pro Manöver einer Folge (Sprint, Sprung, Schlag …).</userdoc>
        [Display("Manöverlänge (Ticks)", "Manöver")] public int SlotTicks = 8;

        /// <userdoc>So viele Manöver plant die Wave Function Collapse auf einmal.</userdoc>
        [Display("Manöver pro Folge", "Manöver")] public int SlotsPerPlan = 8;

        /// <userdoc>Rastergröße des Navmesh waagerecht, in Metern. Kleiner ist genauer und langsamer zu bauen.</userdoc>
        [Display("Zellgröße (m)", "Navmesh")] public float CellSize = 0.15f;

        /// <userdoc>Rastergröße des Navmesh senkrecht, in Metern.</userdoc>
        [Display("Zellhöhe (m)", "Navmesh")] public float CellHeight = 0.05f;

        /// <userdoc>Höher als das springt ein Bot nur mit Doppelsprung, in Metern.</userdoc>
        [Display("Sprung bis (m)", "Navmesh")] public float JumpUp = 1.2f;

        /// <userdoc>Höher als das versucht ein Bot gar nicht hinaufzuspringen, in Metern.</userdoc>
        [Display("Doppelsprung bis (m)", "Navmesh")] public float DoubleJumpUp = 2.2f;

        /// <userdoc>Weiter springt ein Bot nicht über eine Lücke, in Metern.</userdoc>
        [Display("Lücke bis (m)", "Navmesh")] public float GapMax = 3f;
    }
}
