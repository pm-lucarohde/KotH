using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Client.Menu
{
    /// <summary>
    /// The main menu's backdrop: the camera circles the mountain slowly,
    /// from high up at a slant, rising and sinking a little, and looks a
    /// little past it so the mountain stands right of the buttons. Only while
    /// <see cref="Active"/>; the field of view goes back afterwards.
    /// </summary>
    /// <userdoc>Kameraflug hinter dem Hauptmenü.</userdoc>
    [DataContract("KotH.MenuCamera")]
    [Display("Menü-Kameraflug")]
    public sealed class MenuCamera : SyncScript
    {
        /// <userdoc>Mitte der Bahn (x, z in Metern), etwa die Bergachse.</userdoc>
        [Display("Mitte", "Bahn")] public Vector2 Centre;

        /// <userdoc>Abstand der Kamera von der Mitte in Metern.</userdoc>
        [Display("Radius", "Bahn")] public float Radius = 265f;

        /// <userdoc>Mittlere Höhe der Kamera in Metern.</userdoc>
        [Display("Höhe", "Bahn")] public float Height = 142f;

        /// <userdoc>Wie weit die Kamera um die mittlere Höhe auf und ab schwingt, in Metern.</userdoc>
        [Display("Schwingen", "Bahn")] public float Bob = 12f;

        /// <userdoc>Sekunden für eine Runde um den Berg.</userdoc>
        [Display("Rundenzeit", "Bahn")] public float Period = 150f;

        /// <userdoc>Höhe in Metern, auf die die Kamera an der Bergachse schaut.</userdoc>
        [Display("Blickhöhe", "Blick")] public float LookHeight = 62f;

        /// <userdoc>Wie weit der Blick seitlich neben dem Berg vorbeigeht (Meter, positiv = Berg rückt nach rechts).</userdoc>
        [Display("Seitlich", "Blick")] public float LookAside = 60f;

        /// <userdoc>Vertikales Sichtfeld im Menü in Grad.</userdoc>
        [Display("Sichtfeld", "Blick")] public float FieldOfView = 50f;

        /// <userdoc>Wo auf der Bahn der Flug beginnt, in Grad.</userdoc>
        [Display("Startwinkel", "Bahn")] public float StartAngle = 200f;

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
