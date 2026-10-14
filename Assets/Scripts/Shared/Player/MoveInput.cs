using UnityEngine;

namespace KotH.Shared.Player
{
    /// <summary>
    /// Player input for a single tick. Value types only, so it can go over
    /// the wire later unchanged. Yaw and Pitch are absolute angles, not
    /// deltas: lost or reordered packets then cannot skew the view.
    /// </summary>
    public struct MoveInput
    {
        /// <summary>x = strafe, y = forward. Each -1..1.</summary>
        public Vector2 Move;

        /// <summary>Absolute yaw in degrees.</summary>
        public float Yaw;

        /// <summary>Absolute pitch in degrees, already clamped.</summary>
        public float Pitch;

        /// <summary>
        /// Jump presses since the last tick. A counter, not a bool: a 25 ms
        /// tick can swallow a fast double tap. The gesture is recognised in
        /// PlayerMotor, so a server can replay it from the same input.
        /// </summary>
        public byte JumpPresses;

        public bool Sprint;
        public bool Crouch;
    }
}
