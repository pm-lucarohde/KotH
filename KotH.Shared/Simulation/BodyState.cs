using System.Runtime.InteropServices;
using KotH.Shared.Combat;
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

        public BlasterState Blaster;
        public DashState Dash;

        // Teams: the team's id (0 alone), the one it came with (Wish), and whether a bot drives it.
        public int Team;
        public int Wish;
        public byte Bot;

        // 1, 2, 3, Free (FreeRole): the role, the count in the zone in ticks, the order it was caught in (1 first),
        // the catcher who marked or caught it, and its weight in the next draw (255: certain). 0 in other modes.
        public byte Role;
        public int RoleTicks;
        public int CaughtOrder;
        public int MarkedBy;
        public byte Odds;
    }
}
