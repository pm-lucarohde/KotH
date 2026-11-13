using Stride.Core.Mathematics;

namespace KotH.Shared.Physics
{
    /// <summary>A ladder as the motor reads it: the foot's middle on the wall's face, the horizontal
    /// directions out of the wall and along it, half its width and the height of its top.</summary>
    public readonly struct LadderStrip
    {
        public readonly Vector3 Foot;
        public readonly Vector3 Out;
        public readonly Vector3 Along;
        public readonly float HalfWidth;
        public readonly float Top;

        public LadderStrip(Vector3 foot, Vector3 out_, Vector3 along, float halfWidth, float top)
        {
            Foot = foot;
            Out = out_;
            Along = along;
            HalfWidth = halfWidth;
            Top = top;
        }
    }

    /// <summary>A bounce pad as the motor reads it: the middle of its top, half its size in X and Z,
    /// the share of the landing speed it gives back.</summary>
    public readonly struct PadArea
    {
        public readonly Vector3 Top;
        public readonly float HalfX;
        public readonly float HalfZ;
        public readonly float Bounce;

        public PadArea(Vector3 top, float halfX, float halfZ, float bounce)
        {
            Top = top;
            HalfX = halfX;
            HalfZ = halfZ;
            Bounce = bounce;
        }
    }
}
