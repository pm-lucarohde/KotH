using KotH.Shared.Player;

namespace KotH.Shared.Match
{
    /// <summary>What a mode changes on the bodies. Mighty & High (Luca, 25.11.2026): jumps 1.5 times as high,
    /// the double jump 3 times, runs 1.25 times as fast, every attack's knock 3 times as far (not higher).</summary>
    public static class ModeRules
    {
        // The heights carry the tick's loss pre-corrected like the motor's own (1.6 for 1.5 m): x1.4825 rises
        // 1.5 times as high, x2.90 the double jump 3 times (probe "modes mighty" measures both).
        static readonly MotorTuning Mighty = new MotorTuning(1.25f, 1.4825f, 2.90f, 3f);

        public static MotorTuning For(MatchMode mode) => mode == MatchMode.Mighty ? Mighty : MotorTuning.None;

        /// <summary>The damage a round starts with: 30 % in 1, 2, 3, Free (catcher and runners alike, Luca), else 0.</summary>
        public static float StartPercent(MatchMode mode) => mode == MatchMode.Free ? 30f : 0f;
    }
}
