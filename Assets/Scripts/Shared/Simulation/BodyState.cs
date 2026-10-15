using KotH.Shared.Player;

namespace KotH.Shared.Simulation
{
    /// <summary>
    /// One body's share of a tick: the motor plus what combat keeps. Parts
    /// the body does not have stay at their defaults and are ignored on
    /// restore, so a dummy without fists needs no special case.
    /// </summary>
    public struct BodyState
    {
        public MotorState Motor;
        public int AttackCooldown;
        public float DamagePercent;
    }
}
