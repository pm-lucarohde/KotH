using KotH.Client.Hud;
using KotH.Shared.Combat;
using KotH.Shared.Mathematics;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace KotH.Client.Player
{
    /// <summary>
    /// Places the camera at the player's eye point, or behind and above the
    /// figure in third person. Not a child of the player: the motor moves in
    /// 40 steps per second and a child would show those steps as stutter.
    /// Interpolates between the last two tick positions.
    /// </summary>
    [DataContract("KotH.PlayerCamera")]
    public sealed class PlayerCamera : SyncScript
    {
        // How much of the room around the eye the near plane may use.
        [Display("Nahebenen-Spielraum", "Kamera")] public float NearClipMargin = 0.7f;

        [Display("Abstand", "Third-Person")] public float Distance = 3.5f;

        /// <userdoc>Wie hoch über dem Auge der Drehpunkt der Kamera liegt, damit die Kappe die Bildmitte frei lässt</userdoc>
        [Display("Höhe über dem Auge", "Third-Person")] public float Lift = 0.6f;

        /// <userdoc>Radius der Kugel, mit der die Kamera Wänden und Decken ausweicht</userdoc>
        [Display("Kugelradius", "Third-Person")] public float Radius = 0.1f;

        /// <userdoc>Wie schnell die Kamera nach einem Hindernis wieder auf ihren Abstand zurückgleitet, in m/s</userdoc>
        [Display("Rücklauftempo", "Third-Person")] public float ReturnSpeed = 6f;

        /// <userdoc>Ab diesem Abstand vom Auge wird die eigene Figur gezeichnet, darunter nur ihr Schatten</userdoc>
        [Display("Figur ab Abstand", "Third-Person")] public float ShowFigureFrom = 1.6f;

        [DataMemberIgnore] public PlayerMotor Motor { get; set; }
        [DataMemberIgnore] public PlayerInputReader Reader { get; set; }
        [DataMemberIgnore] public MeleeAttack Melee { get; set; }
        [DataMemberIgnore] public WorldQueries World { get; set; }

        /// <summary>Render group of the own figure, which the view skips up close.</summary>
        [DataMemberIgnore] public RenderGroup OwnFigureGroup { get; set; } = RenderGroup.Group1;

        CameraComponent cam;
        SceneCameraRenderer view;

        // The distance the camera shows: it snaps in when something is in
        // the way and glides back out, so it never shakes against a wall.
        float shown;

        public PlayerCamera()
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

            var backBuffer = GraphicsDevice.Presenter.BackBuffer;
            float aspect = (float)backBuffer.Width / backBuffer.Height;
            Vector3 eye = ViewMath.Eye(Motor, TickInterpolation.Alpha);
            Quaternion rotation = UMath.Euler(Reader.Pitch, Reader.Yaw);
            Entity.Transform.Rotation = rotation;

            if (!Reader.ThirdPerson || World == null)
            {
                Entity.Transform.Position = eye;
                cam.NearClipPlane = ViewMath.NearPlane(Motor, cam.VerticalFieldOfView, aspect, NearClipMargin);
                shown = 0f;
                ShowOwnFigure(false);
                AimMark.Screen = new Vector2(0.5f, 0.5f);
                return;
            }

            ThirdPersonPath path = ViewMath.ThirdPerson(World, eye, Reader.Yaw, Reader.Pitch, Lift, Distance, Radius);
            float dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;
            shown = UMath.Min(UMath.MoveTowards(shown, Distance, ReturnSpeed * dt), path.Free);

            Vector3 position = path.At(shown);
            Entity.Transform.Position = position;
            cam.NearClipPlane = ViewMath.NearPlane(Radius, cam.VerticalFieldOfView, aspect, NearClipMargin);
            ShowOwnFigure(UMath.Magnitude(UMath.Sub(position, eye)) >= ShowFigureFrom);
            AimMark.Screen = Project(AimPoint(eye, rotation), position, rotation, aspect);
        }

        /// <summary>
        /// Where a swing made now would end: the ray MeleeAttack casts, from
        /// the eye along the view, to the first hull or its full reach.
        /// </summary>
        Vector3 AimPoint(Vector3 eye, Quaternion rotation)
        {
            Vector3 aim = UMath.Rotate(rotation, UMath.Forward);
            float reach = Melee?.Range ?? 3.5f;
            if (World.Raycast(eye, aim, reach, WorldQueries.HittableMask, out QueryHit hit, Motor.Hull))
                reach = hit.Distance;
            return UMath.Add(eye, UMath.Mul(aim, reach));
        }

        /// <summary>A world point on screen, 0 to 1 across and down, through this camera.</summary>
        Vector2 Project(Vector3 point, Vector3 position, Quaternion rotation, float aspect)
        {
            Quaternion inverse = rotation;
            inverse.Conjugate();
            Vector3 local = UMath.Rotate(inverse, UMath.Sub(point, position));
            if (local.Z > -0.01f)
                return new Vector2(0.5f, 0.5f);

            float t = (float)System.Math.Tan(cam.VerticalFieldOfView * 0.5f * UMath.Deg2Rad);
            float x = local.X / -local.Z / (t * aspect);
            float y = local.Y / -local.Z / t;
            return new Vector2(0.5f + 0.5f * x, 0.5f - 0.5f * y);
        }

        /// <summary>The own figure is drawn only with room around the eye, else just its shadow.</summary>
        void ShowOwnFigure(bool show)
        {
            view ??= FindView(SceneSystem.GraphicsCompositor?.Game);
            if (view == null)
                return;

            var bit = (RenderGroupMask)(1u << (int)OwnFigureGroup);
            view.RenderMask = show ? view.RenderMask | bit : view.RenderMask & ~bit;
        }

        /// <summary>The camera's renderer, also inside the collection the HUD wraps it in.</summary>
        static SceneCameraRenderer FindView(ISceneRenderer renderer)
        {
            if (renderer is SceneCameraRenderer camera)
                return camera;
            if (renderer is SceneRendererCollection collection)
                foreach (ISceneRenderer child in collection.Children)
                    if (FindView(child) is { } found)
                        return found;
            return null;
        }
    }
}
