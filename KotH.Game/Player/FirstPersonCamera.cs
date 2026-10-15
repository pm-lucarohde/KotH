using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using Stride.Core;
using Stride.Engine;

namespace KotH.Client.Player
{
    /// <summary>
    /// Places the camera at the player's eye point. Not a child of the player:
    /// the motor moves in 40 steps per second and a child would show those
    /// steps as stutter. Interpolates between the last two tick positions.
    /// </summary>
    [DataContract("KotH.FirstPersonCamera")]
    public sealed class FirstPersonCamera : SyncScript
    {
        // How much of the room around the eye the near plane may use.
        [Display("Nahebenen-Spielraum", "Kamera")] public float NearClipMargin = 0.7f;

        [DataMemberIgnore] public PlayerMotor Motor { get; set; }
        [DataMemberIgnore] public PlayerInputReader Reader { get; set; }

        CameraComponent cam;

        public FirstPersonCamera()
        {
            // After the driver and every body: the camera sees the final frame.
            Priority = 1000;
        }

        public override void Start()
        {
            cam = Entity.Get<CameraComponent>();
        }

        public override void Update()
        {
            if (Motor == null || Reader == null || cam == null)
                return;

            Entity.Transform.Position = ViewMath.Eye(Motor, TickInterpolation.Alpha);
            Entity.Transform.Rotation = UMath.Euler(Reader.Pitch, Reader.Yaw);

            var backBuffer = GraphicsDevice.Presenter.BackBuffer;
            float aspect = (float)backBuffer.Width / backBuffer.Height;
            cam.NearClipPlane = ViewMath.NearPlane(Motor, cam.VerticalFieldOfView, aspect, NearClipMargin);
        }
    }
}
