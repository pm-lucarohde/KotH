using System;
using KotH.Client.Rendering;
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
    /// Interpolates between the last two tick positions. A hit taken shakes
    /// the view, always as hard; only the drawn view, the aim stays still.
    /// </summary>
    [DataContract("KotH.PlayerCamera")]
    public sealed class PlayerCamera : SyncScript
    {
        // How much of the room around the eye the near plane may use.
        [Display("Near clip margin", "Camera")] public float NearClipMargin = 0.7f;

        [Display("Distance", "Third person")] public float Distance = 3.5f;

        /// <userdoc>How high above the eye the camera pivots, so the cap leaves the centre of the view free</userdoc>
        [Display("Lift above eye", "Third person")] public float Lift = 0.6f;

        /// <userdoc>Radius of the sphere the camera dodges walls and ceilings with</userdoc>
        [Display("Sphere radius", "Third person")] public float Radius = 0.1f;

        /// <userdoc>How fast the camera glides back to its distance after an obstacle, in m/s</userdoc>
        [Display("Return speed", "Third person")] public float ReturnSpeed = 6f;

        /// <userdoc>From this distance to the eye the own figure is drawn, below it only its shadow</userdoc>
        [Display("Figure from distance", "Third person")] public float ShowFigureFrom = 1.6f;

        /// <userdoc>How long the view shakes after a hit, in seconds. 0 turns shaking off.</userdoc>
        [Display("Duration", "Shake on hit")] public float ShakeSeconds = 0.3f;

        /// <userdoc>How far the view tilts at most at first, in degrees; then it fades. Independent of the damage percent.</userdoc>
        [Display("Strength", "Shake on hit")] public float ShakeDegrees = 1.125f;

        /// <userdoc>How often per second the view goes back and forth.</userdoc>
        [Display("Rate", "Shake on hit")] public float ShakeHertz = 8f;

        [DataMemberIgnore] public PlayerMotor Motor { get; set; }
        [DataMemberIgnore] public PlayerInputReader Reader { get; set; }
        [DataMemberIgnore] public MeleeAttack Melee { get; set; }
        [DataMemberIgnore] public WorldQueries World { get; set; }

        /// <summary>Render group of the own figure, which the view skips up close.</summary>
        [DataMemberIgnore] public RenderGroup OwnFigureGroup { get; set; } = RenderGroup.Group1;

        /// <summary>Render group of the first-person arms, drawn only in first person.</summary>
        [DataMemberIgnore] public RenderGroup ArmsGroup { get; set; } = RenderGroup.Group2;

        CameraComponent cam;
        SceneCameraRenderer view;

        // The distance the camera shows: it snaps in when something is in
        // the way and glides back out, so it never shakes against a wall.
        float shown;

        // The shake: which hit it is for, how long it has run, and a phase
        // per axis so no two shakes look alike.
        DamagePercent own;
        int shakenFor = -1;
        float shakeTime = float.MaxValue;
        Vector3 shakePhase;
        readonly System.Random shakeRandom = new System.Random();

        public PlayerCamera()
        {
            // After the driver and every body: the camera sees the final frame.
            Priority = 1000;
        }

        public override void Start()
        {
            cam = Entity.Get<CameraComponent>();
            shown = Distance;
        }

        public override void Update()
        {
            if (Motor == null || Reader == null || cam == null)
                return;

            var backBuffer = GraphicsDevice.Presenter.BackBuffer;
            float aspect = (float)backBuffer.Width / backBuffer.Height;
            Vector3 eye = ViewMath.Eye(Motor, TickInterpolation.Alpha);
            Quaternion rotation = UMath.Euler(Reader.Pitch, Reader.Yaw);
            // Stride's a * b turns by a first: the shake about the view's own axes.
            Entity.Transform.Rotation = Shake((float)Game.UpdateTime.Elapsed.TotalSeconds) * rotation;

            if (!Reader.ThirdPerson || World == null)
            {
                Entity.Transform.Position = eye;
                cam.NearClipPlane = ViewMath.NearPlane(Motor, cam.VerticalFieldOfView, aspect, NearClipMargin);
                // Steps out at full distance at once; only a wall pulls it in.
                shown = Distance;
                ShowOwnFigure(false, true);
                Reader.AimAt(null);
                return;
            }

            ThirdPersonPath path = ViewMath.ThirdPerson(World, eye, Reader.Yaw, Reader.Pitch, Lift, Distance, Radius);
            float dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;
            shown = UMath.Min(UMath.MoveTowards(shown, Distance, ReturnSpeed * dt), path.Free);

            Vector3 position = path.At(shown);
            Entity.Transform.Position = position;
            cam.NearClipPlane = ViewMath.NearPlane(Radius, cam.VerticalFieldOfView, aspect, NearClipMargin);
            ShowOwnFigure(UMath.Magnitude(UMath.Sub(position, eye)) >= ShowFigureFrom, false);
            Reader.AimAt(AimTarget(path.Pivot, eye, rotation));
        }

        /// <summary>
        /// The view's wobble this frame, identity when still. Starts again with
        /// every new hit on the own body, at full strength whatever its percent,
        /// and fades out quadratically over ShakeSeconds.
        /// </summary>
        Quaternion Shake(float dt)
        {
            if (own?.Entity != Motor.Entity)
            {
                own = Motor.Entity.Get<DamagePercent>();
                shakenFor = own?.LastHitTick ?? -1;
            }
            if (own != null && own.LastHitTick != shakenFor)
            {
                shakenFor = own.LastHitTick;
                shakeTime = 0f;
                shakePhase = new Vector3((float)shakeRandom.NextDouble(), (float)shakeRandom.NextDouble(),
                                         (float)shakeRandom.NextDouble()) * MathUtil.TwoPi;
            }

            if (ShakeSeconds <= 0f || shakeTime >= ShakeSeconds)
                return Quaternion.Identity;
            shakeTime += dt;

            float fade = 1f - MathUtil.Clamp(shakeTime / ShakeSeconds, 0f, 1f);
            float a = ShakeDegrees * fade * fade;
            float w = MathUtil.TwoPi * ShakeHertz * shakeTime;

            // Slightly different rates per axis, so it never swings along one line.
            float pitch = a * MathF.Sin(w + shakePhase.X);
            float yaw = a * MathF.Sin(w * 1.13f + shakePhase.Y);
            float roll = 0.5f * a * MathF.Sin(w * 0.87f + shakePhase.Z);
            return Quaternion.RotationYawPitchRoll(MathUtil.DegreesToRadians(yaw), MathUtil.DegreesToRadians(pitch),
                                                   MathUtil.DegreesToRadians(roll));
        }

        /// <summary>
        /// What a swing should aim at: the first thing on the centre line, but
        /// no further than where the line leaves the reach around the eye.
        /// Cast from the pivot, which lies on that line, so whatever is
        /// between the camera and the figure counts as behind it.
        /// </summary>
        Vector3 AimTarget(Vector3 pivot, Vector3 eye, Quaternion rotation)
        {
            // |pivot + forward*s - eye| = reach; the pivot lies inside the
            // reach, so the larger root is ahead of it.
            Vector3 forward = UMath.Rotate(rotation, UMath.Forward);
            Vector3 w = UMath.Sub(pivot, eye);
            float reach = Melee?.Range ?? 3.5f;
            float b = UMath.Dot(forward, w);
            float s = -b + UMath.Sqrt(UMath.Max(0f, b * b - UMath.SqrMagnitude(w) + reach * reach));

            if (World.Raycast(pivot, forward, s, WorldQueries.WorldMask | WorldQueries.HittableMask,
                              out QueryHit hit, Motor.Hull))
                s = hit.Distance;
            return UMath.Add(pivot, UMath.Mul(forward, s));
        }

        /// <summary>
        /// The own figure is drawn only with room around the eye, else just its
        /// shadow; the arms in front of the eye only in first person.
        /// </summary>
        void ShowOwnFigure(bool show, bool arms)
        {
            view ??= FindView(SceneSystem.GraphicsCompositor?.Game);
            if (view == null)
                return;

            var bit = (RenderGroupMask)(1u << (int)OwnFigureGroup);
            view.RenderMask = show ? view.RenderMask | bit : view.RenderMask & ~bit;
            var armsBit = (RenderGroupMask)(1u << (int)ArmsGroup);
            view.RenderMask = arms ? view.RenderMask | armsBit : view.RenderMask & ~armsBit;
        }

        /// <summary>The camera's renderer, also inside the collection and SceneThrottle the HUD wraps it in.</summary>
        static SceneCameraRenderer FindView(ISceneRenderer renderer)
        {
            if (renderer is SceneCameraRenderer camera)
                return camera;
            if (renderer is SceneThrottle throttle)
                return FindView(throttle.Child);
            if (renderer is SceneRendererCollection collection)
                foreach (ISceneRenderer child in collection.Children)
                    if (FindView(child) is { } found)
                        return found;
            return null;
        }
    }
}
