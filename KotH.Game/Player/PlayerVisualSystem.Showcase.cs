using System;
using System.Collections.Generic;
using Stride.Animations;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering;

namespace KotH.Client.Player
{
    /// <summary>Figures without a body, as in the main menu: the idle clip, blinking and
    /// breathing like any figure, the head turned after the mouse pointer. A showcase
    /// figure hangs from its camera's entity (GameRoot), so the pointer's ray is found in
    /// the camera's own frame.</summary>
    public sealed partial class PlayerVisualSystem
    {
        /// <userdoc>How far a showcase figure (main menu) turns its head after the pointer, degrees sideways and up or down.</userdoc>
        [Display("Showcase head turn", "Showcase")] public Vector2 ShowcaseHeadDegrees = new Vector2(40f, 25f);

        /// <userdoc>Seconds a showcase figure's head takes to follow the pointer.</userdoc>
        [Display("Showcase head lag", "Showcase")] public float ShowcaseHeadSeconds = 0.25f;

        /// <userdoc>Height of the eyes above the figure's centre in metres, where the pointer is looked at from.</userdoc>
        [Display("Showcase eye height", "Showcase")] public float ShowcaseEyeHeight = 0.55f;

        /// <userdoc>Seconds a showcase figure takes to glide to a new place (MoveShowcase).</userdoc>
        [Display("Showcase glide", "Showcase")] public float ShowcaseGlideSeconds = 0.18f;

        readonly List<Figure> showcases = new List<Figure>();

        /// <summary>Shows visual as a showcase figure; its parent entity holds the camera.</summary>
        public void RegisterShowcase(Entity visual)
        {
            showcases.Add(new Figure
            {
                Animation = visual.Get<AnimationComponent>(),
                Model = visual.Get<ModelComponent>(),
                Transform = visual.Transform,
                BlinkWait = BlinkPause(),
                MouthClock = (float)random.NextDouble() * 30f,
                ShownAt = visual.Transform.Position,
            });
        }

        /// <summary>Lets a showcase figure glide to to, in its parent's frame.</summary>
        public void MoveShowcase(Entity visual, Vector3 to)
        {
            if (showcases.Find(f => f.Transform == visual?.Transform) is { } f)
                f.ShownAt = to;
        }

        public void UnregisterShowcase(Entity visual) => showcases.RemoveAll(f => f.Transform == visual.Transform);

        void Showcase(float dt)
        {
            foreach (Figure f in showcases)
            {
                if (f.Plays == null && f.Animation != null && f.Animation.Animations.TryGetValue(Clips[IdleIndex], out AnimationClip idle))
                {
                    f.Plays = new PlayingAnimation[Clips.Length];
                    f.Plays[IdleIndex] = f.Animation.Add(idle, 0, AnimationBlendOperation.LinearBlend, 1f, 1f, AnimationRepeatMode.LoopInfinite);
                }
                Blink(f, dt);
                Breathe(f, dt);
                // A showcase's ShownAt is where it glides to.
                f.Transform.Position = Vector3.Lerp(f.ShownAt, f.Transform.Position, MathF.Exp(-dt / MathF.Max(1e-3f, ShowcaseGlideSeconds)));
                FollowPointer(f, dt);
            }
        }

        // The head after the pointer: its ray through the screen, met at the figure's
        // distance, seen from the eyes in the figure's frame (forward -Z).
        void FollowPointer(Figure f, float dt)
        {
            SkeletonUpdater skeleton = f.Model?.Skeleton;
            CameraComponent cam = f.Transform.Parent?.Entity.Get<CameraComponent>();
            if (skeleton == null || cam == null)
                return;
            if (f.Look == -2)
                f.Look = Array.FindIndex(skeleton.Nodes, n => n.Name == LookBone);
            if (f.Look < 0)
                return;

            Vector2 mouse = Input.HasMouse ? Input.MousePosition : new Vector2(0.5f);
            float tan = MathF.Tan(MathUtil.DegreesToRadians(cam.VerticalFieldOfView) * 0.5f);
            float aspect = cam.UseCustomAspectRatio ? cam.AspectRatio : Game.GraphicsDevice.Presenter.BackBuffer.ViewWidth
                           / (float)Math.Max(1, Game.GraphicsDevice.Presenter.BackBuffer.ViewHeight);
            var ray = new Vector3((2f * mouse.X - 1f) * tan * aspect, (1f - 2f * mouse.Y) * tan, -1f);
            Vector3 eye = f.Transform.Position + Vector3.Transform(new Vector3(0f, ShowcaseEyeHeight, 0f), f.Transform.Rotation);
            Vector3 target = ray * MathF.Max(0.5f, -eye.Z);
            Vector3 local = Vector3.Transform(target - eye, Quaternion.Invert(f.Transform.Rotation));

            float yaw = Math.Clamp(MathF.Atan2(-local.X, -local.Z), -MathUtil.DegreesToRadians(ShowcaseHeadDegrees.X),
                                   MathUtil.DegreesToRadians(ShowcaseHeadDegrees.X));
            float pitch = Math.Clamp(MathF.Atan2(local.Y, MathF.Sqrt(local.X * local.X + local.Z * local.Z)),
                                     -MathUtil.DegreesToRadians(ShowcaseHeadDegrees.Y), MathUtil.DegreesToRadians(ShowcaseHeadDegrees.Y));
            float follow = 1f - MathF.Exp(-dt / MathF.Max(1e-3f, ShowcaseHeadSeconds));
            f.HeadYaw += (yaw - f.HeadYaw) * follow;
            f.HeadPitch += (pitch - f.HeadPitch) * follow;
            // As LookUpDown: positive pitch tips the head back.
            skeleton.NodeTransformations[f.Look].Transform.Rotation = Quaternion.RotationYawPitchRoll(f.HeadYaw, f.HeadPitch, 0f);
        }
    }
}
