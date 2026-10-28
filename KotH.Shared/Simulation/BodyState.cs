using System.Runtime.InteropServices;
using KotH.Shared.Player;

namespace KotH.Shared.Simulation
{
    /// <summary>One body's share of a tick: the motor plus what combat keeps. Parts the
    /// body does not have stay at their defaults and are ignored on restore, so a dummy
    /// without fists needs no special case. Packed like MotorState: the bytes are the
    /// state, nothing else.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BodyState
    {
        public MotorState Motor;
        public int AttackCooldown;
        public float DamagePercent;

        // King of the hill, in ticks: points earned and time holding the zone.
        public int ScoreTicks;
        public int AloneTicks;
    }
}
