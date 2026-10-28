using Stride.Core.Mathematics;

namespace KotH.Shared.Player
{
    /// <summary>Player input for a single tick. Value types only, so it can go over the
    /// wire later unchanged. Yaw and Pitch are absolute angles, not deltas: lost or
    /// reordered packets then cannot skew the view.</summary>
    public struct MoveInput
    {
        /// <summary>x = strafe right, y = forward. Each -1..1.</summary>
        public Vector2 Move;

        /// <summary>
        /// Absolute yaw in degrees, Stride convention: positive turns left,
        /// seen from above. Unity's yaw turned right, so it is negated.
        /// </summary>
        public float Yaw;

        /// <summary>Absolute pitch in degrees, already clamped; positive looks up.</summary>
        public float Pitch;

        /// <summary>Jump presses since the last tick. A counter, not a bool: a 25 ms tick
        /// can swallow a fast double tap. The gesture is recognised in PlayerMotor, so a
        /// server can replay it from the same input.</summary>
        public byte JumpPresses;

        /// <summary>Attack presses since the last tick, counted for the same reason as
        /// JumpPresses: the click is true for one frame, a tick spans three or four of
        /// them.</summary>
        public byte AttackPresses;

        /// <summary>Whether the jump key is still down. The hold time is counted in
        /// PlayerMotor, so a server can replay the height from the same input. Left false
        /// next to a JumpPresses of 1, the jump comes out at minimum height -- a trap for
        /// every rig that builds its own.</summary>
        public bool JumpHeld;

        /// <summary>Crouch key newly pressed, collected per frame. A bool and not a
        /// counter like JumpPresses: crouching has no double tap gesture, so two presses
        /// inside one tick need not both survive. Only the slide reads it; the crouch
        /// pose follows Crouch, which is held.</summary>
        public bool CrouchPressed;

        public bool Sprint;
        public bool Crouch;

        /// <summary>The blaster is wanted in hand (key 2), else the cane (key 1). Absolute
        /// like Yaw, so a lost packet cannot flip the choice.</summary>
        public bool Blaster;
    }
}
