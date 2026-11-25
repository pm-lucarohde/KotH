namespace KotH.Shared.Player
{
    /// <summary>A mode's factors on the motor (HillMatch's ModeRules): run speeds, the jump's and the double
    /// jump's height, a knock's distance. Not state: SimulationSystem sets it every tick from the round's mode,
    /// so a rollback, a guest and a bot's model see the same. All 1 multiplies bit-exactly (solo as before).</summary>
    public readonly struct MotorTuning
    {
        public readonly float Speed, Jump, DoubleJump, Knock;

        public MotorTuning(float speed, float jump, float doubleJump, float knock)
        {
            Speed = speed;
            Jump = jump;
            DoubleJump = doubleJump;
            Knock = knock;
        }

        public static readonly MotorTuning None = new MotorTuning(1f, 1f, 1f, 1f);
    }
}
