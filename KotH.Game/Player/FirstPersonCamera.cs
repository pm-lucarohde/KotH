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

            float alpha = TickInterpolation.Alpha;
            Entity.Transform.Position = UMath.Lerp(Motor.PreviousEyePosition, Motor.EyePosition, alpha);
            Entity.Transform.Rotation = UMath.Euler(Reader.Pitch, Reader.Yaw);

            ApplyNearPlane();
        }

        /// <summary>
        /// Keeps the near plane inside the collision capsule. Only the body
        /// is collided, so a near plane reaching past it clips away whatever
        /// the player leans against and makes it see-through.
        /// </summary>
        void ApplyNearPlane()
        {
            // The near plane is a rectangle, not a point: its corners sit
            // near * sqrt(1 + t^2 + (aspect*t)^2) from the eye. Recomputed
            // per frame because the aspect follows the window.
            var backBuffer = GraphicsDevice.Presenter.BackBuffer;
            float aspect = (float)backBuffer.Width / backBuffer.Height;
            float t = (float)System.Math.Tan(cam.VerticalFieldOfView * 0.5f * UMath.Deg2Rad);
            float wide = aspect * t;
            float corner = UMath.Sqrt(1f + t * t + wide * wide);

            // Above the eye there is only standHeight - standEyeHeight of
            // body, 0.12 m, far less than the radius. That is what a ceiling
            // resting on the head has to clear.
            float crown = Motor.BodyCenter.Y + Motor.Height * 0.5f - Motor.EyePosition.Y;
            float room = UMath.Min(crown, Motor.Radius) * NearClipMargin;

            cam.NearClipPlane = UMath.Clamp(room / corner, 0.01f, 0.3f);
        }
    }
}
