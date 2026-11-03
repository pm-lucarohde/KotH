using System.Runtime.InteropServices;

namespace KotH.Shared.Combat
{
    /// <summary>Everything of the dash a rollback has to put back, packed like
    /// MotorState; the fall itself is the motor's (MotorState.SlamSpeed).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DashState
    {
        public int Charges;

        /// <summary>Score ticks earned towards the next dash, and ScoreTicks as last seen.</summary>
        public int Progress;
        public int ScoreSeen;
    }
}
