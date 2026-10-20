using System.Runtime.InteropServices;
using Stride.Core.Mathematics;

namespace KotH.Shared.Player
{
    /// <summary>
    /// Everything a PlayerMotor needs to continue from a tick exactly as it
    /// did the first time. Captured after a tick, restored before one. A
    /// field missing here is a rollback that silently diverges, so the
    /// determinism probe hashes every one of them. Packed, so its bytes have
    /// no padding and the network can send and hash them as they are.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MotorState
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;

        public bool IsCrouching;
        public bool Sprinting;
        public bool SprintArmed;
        public bool Sliding;
        public float SlideSpeed;
        public float SlideCooldownLeft;
        public float LastStepSpeed;
        public bool TouchingGround;
        public float CurrentHeight;
        public bool Grounded;
        public bool OnSteepGround;
        public Vector3 GroundNormal;
        public float GroundAngle;
        public float SteepTime;
        public Vector3 ContactNormal;
        public bool HasContactNormal;
        public bool SteppedUp;
        public int StepSettleTicks;
        public bool Wedged;
        public float KnockbackTime;
        public Vector3 Knock;
        public bool DoubleJumpUsed;
        public bool JumpWindowOpen;
        public float TimeSinceGroundJump;
        public bool HasPendingAirPress;
        public float TimeSinceAirPress;
        public bool JumpCutPending;
        public float JumpHeldTime;
        public float JumpStartY;

        // Not render-only after all: contacts read the body centre and a
        // swing starts at the eye, both as the last tick left them.
        public Vector3 EyePosition;
        public Vector3 PreviousEyePosition;
        public Vector3 BodyCenter;
        public Vector3 PreviousBodyCenter;
    }
}
