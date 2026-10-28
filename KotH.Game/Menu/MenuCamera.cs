using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Client.Menu
{
    /// <summary>The main menu's backdrop: the camera circles the mountain slowly, high up at a
    /// slant, rising and sinking a little, the mountain right of the buttons. Only while
    /// <see cref="Active"/>; the field of view goes back afterwards.</summary>
    /// <userdoc>Camera flight behind the main menu.</userdoc>
    [DataContract("KotH.MenuCamera")]
    [Display("Menu camera flight")]
    public sealed class MenuCamera : SyncScript
    {
        /// <userdoc>Centre of the path (x, z in metres), about the mountain's axis.</userdoc>
        [Display("Centre", "Path")] public Vector2 Centre;

        /// <userdoc>Distance of the camera from the centre in metres.</userdoc>
        [Display("Radius", "Path")] public float Radius = 265f;

        /// <userdoc>Mean height of the camera in metres.</userdoc>
        [Display("Height", "Path")] public float Height = 142f;

        /// <userdoc>How far the camera bobs up and down around the mean height, in metres.</userdoc>
        [Display("Bob", "Path")] public float Bob = 12f;

        /// <userdoc>Seconds for one lap around the mountain.</userdoc>
        [Display("Lap time", "Path")] public float Period = 150f;

        /// <userdoc>Height in metres the camera looks at on the mountain's axis.</userdoc>
        [Display("Look height", "View")] public float LookHeight = 62f;

        /// <userdoc>How far the view passes beside the mountain (metres, positive = mountain moves right).</userdoc>
        [Display("Aside", "View")] public float LookAside = 60f;

        /// <userdoc>Vertical field of view in the menu in degrees.</userdoc>
        [Display("Field of view", "View")] public float FieldOfView = 50f;

        /// <userdoc>Where on the path the flight starts, in degrees.</userdoc>
        [Display("Start angle", "Path")] public float StartAngle = 200f;

        /// <summary>Set by GameRoot while the main menu is open.</summary>
        [DataMemberIgnore] public bool Active { get; set; }

        CameraComponent cam;
        float playFieldOfView;
        bool flying;
        double time;

        public override void Start()
        {
            cam = Entity.Get<CameraComponent>();
        }

        public override void Update()
        {
            if (cam == null)
                return;
            if (!Active)
            {
                if (flying)
                {
                    cam.VerticalFieldOfView = playFieldOfView;
                    flying = false;
                }
                return;
            }
            if (!flying)
            {
                playFieldOfView = cam.VerticalFieldOfView;
                flying = true;
            }

            time += Game.UpdateTime.Elapsed.TotalSeconds;
            double turn = 2.0 * Math.PI * time / Math.Max(Period, 1f);
            double a = MathUtil.DegreesToRadians(StartAngle) + turn;
            var eye = new Vector3(Centre.X + Radius * (float)Math.Sin(a),
                                  Height + Bob * (float)Math.Sin(turn * 2.0 + 0.7),
                                  Centre.Y + Radius * (float)Math.Cos(a));

            // Look at the axis, shifted to the camera's left: the mountain then sits right of centre.
            var toAxis = new Vector3(Centre.X - eye.X, 0f, Centre.Y - eye.Z);
            toAxis.Normalize();
            var left = Vector3.Cross(Vector3.UnitY, toAxis);
            var target = new Vector3(Centre.X, LookHeight, Centre.Y) + left * LookAside;

            Matrix world = Matrix.Invert(Matrix.LookAtRH(eye, target, Vector3.UnitY));
            world.Decompose(out _, out Quaternion rotation, out _);
            Entity.Transform.Position = eye;
            Entity.Transform.Rotation = rotation;
            cam.VerticalFieldOfView = FieldOfView;
        }
    }
}
