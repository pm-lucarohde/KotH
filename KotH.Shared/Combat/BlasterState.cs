using System.Runtime.InteropServices;
using Stride.Core.Mathematics;

namespace KotH.Shared.Combat
{
    /// <summary>One spore in flight: where it is after the tick, how fast it goes, and how
    /// many ticks it has flown. Age 0 marks a free slot.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BlasterShot
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public int Age;
    }

    /// <summary>Everything of the blaster a rollback has to put back, packed like
    /// MotorState. The spores fly in three fixed slots on their shooter, so a snapshot,
    /// a transfer and a leaving player carry them without a list of their own.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BlasterState
    {
        /// <summary>The blaster is in hand, else the cane.</summary>
        public bool Selected;

        public int Shots;

        /// <summary>Score ticks earned towards the next shot, and ScoreTicks as last seen.</summary>
        public int Progress;
        public int ScoreSeen;

        /// <summary>Ticks until the next shot, and ticks left of drawing after a switch.</summary>
        public int Cooldown;
        public int Draw;

        public BlasterShot Shot0, Shot1, Shot2;

        public const int Slots = 3;
    }
}
