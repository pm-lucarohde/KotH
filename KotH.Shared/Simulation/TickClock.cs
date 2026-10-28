namespace KotH.Shared.Simulation
{
    /// <summary>Turns frame times into fixed 40 Hz ticks like Unity's player loop: game time
    /// advances by the frame time capped at Maximum Allowed Timestep, a tick runs per full tick
    /// piled up. After a hitch the game slows down instead of bursting through catch-up ticks
    /// (ClockProbe).</summary>
    public sealed class TickClock
    {
        /// <summary>Unity's Maximum Allowed Timestep, a float there.</summary>
        public const double MaxFrameSeconds = 0.33333334f;

        /// <summary>
        /// Unity's fixed step as it kept time: 3528000 / 141120000 in double.
        /// The step handed to the simulation stays the float TickSeconds.
        /// </summary>
        public const double TickSeconds = 3528000.0 / 141120000.0;

        double sinceTick;

        /// <summary>
        /// How far game time sits between the last tick and the next, 0 to 1:
        /// Unity's (time - fixedTime) / fixedDeltaTime.
        /// </summary>
        public float Alpha => (float)(sinceTick / TickSeconds);

        /// <summary>Game time since the last tick, in seconds.</summary>
        public double SinceTick
        {
            get => sinceTick;
            set => sinceTick = value;
        }

        /// <summary>Adds one frame and returns how many ticks are due now.</summary>
        public int Advance(double frameSeconds)
        {
            sinceTick += frameSeconds < MaxFrameSeconds ? frameSeconds : MaxFrameSeconds;

            int due = 0;
            while (sinceTick >= TickSeconds)
            {
                sinceTick -= TickSeconds;
                due++;
            }
            return due;
        }
    }
}
