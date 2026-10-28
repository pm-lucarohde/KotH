using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.Shared.Bots
{
    /// <summary>
    /// How the bots play and how their climbing grid is cut. Sits on an entity of
    /// the arena prefab, so it loads headless with the arena; an arena
    /// without one plays with these defaults.
    /// </summary>
    [DataContract("KotH.BotSettings")]
    [Display("Bot Settings")]
    [ComponentCategory("KotH")]
    public sealed class BotSettings : EntityComponent
    {
        /// <userdoc>How many ticks old everything a bot sees is (40 ticks = 1 s). Less is stronger; at least 3.</userdoc>
        [Display("Reaction time (ticks)", "Strength")] public int ReactionTicks = 8;

        /// <userdoc>Every this many ticks a bot rethinks what it wants and where it goes.</userdoc>
        [Display("Think interval (ticks)", "Strength")] public int ThinkTicks = 6;

        /// <userdoc>How far a bot's swing may miss its aim at most, in degrees.</userdoc>
        [Display("Aim error (°)", "Strength")] public float AimError = 4f;

        /// <userdoc>How fast a bot turns at most, in degrees per tick.</userdoc>
        [Display("Turn rate (°/tick)", "Strength")] public float TurnRate = 24f;

        /// <userdoc>A bot attacks opponents up to this distance, in metres.</userdoc>
        [Display("Engage range (m)", "Decisions")] public float EngageRange = 9f;

        /// <userdoc>Intents at most this share worse than the best one are picked at random. 0 always decides the same.</userdoc>
        [Display("Randomness", "Decisions")] public float Randomness = 0.12f;

        /// <userdoc>Bonus for the running intent, so a bot does not flip back and forth.</userdoc>
        [Display("Persistence", "Decisions")] public float Persistence = 0.3f;

        /// <userdoc>How long a bot stands still at round start before it sets off, in seconds.</userdoc>
        [Display("Wait at round start (s)", "Decisions")] public float StartWait = 3f;

        /// <userdoc>Ticks per manoeuvre of a sequence (sprint, jump, swing …).</userdoc>
        [Display("Manoeuvre length (ticks)", "Manoeuvres")] public int SlotTicks = 8;

        /// <userdoc>How many manoeuvres the wave function collapse plans at once.</userdoc>
        [Display("Manoeuvres per plan", "Manoeuvres")] public int SlotsPerPlan = 8;

        /// <userdoc>Cell size of the climb grid, in metres. Smaller is more precise and slower to build.</userdoc>
        [Display("Cell size (m)", "Climb grid")] public float CellSize = 0.5f;

        /// <userdoc>How many distinct routes to the zone exist; each bot takes another. 0: all take the fastest.</userdoc>
        [Display("Route variants", "Climb grid")] public int RouteVariants = 4;

        /// <userdoc>How much slower than the fastest a route may be at most (0.25 = 25 %).</userdoc>
        [Display("Max detour", "Climb grid")] public float RouteDetour = 0.25f;

        /// <userdoc>Above this a bot only jumps with a double jump, in metres.</userdoc>
        [Display("Jump up to (m)", "Climb grid")] public float JumpUp = 1.2f;

        /// <userdoc>Above this a bot does not try to jump up at all, in metres.</userdoc>
        [Display("Double jump up to (m)", "Climb grid")] public float DoubleJumpUp = 2.2f;

        /// <userdoc>A bot jumps no wider gap than this, in metres.</userdoc>
        [Display("Gap up to (m)", "Climb grid")] public float GapMax = 3f;
    }
}
