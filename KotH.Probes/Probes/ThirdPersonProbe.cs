using System;
using KotH.Client.Player;
using KotH.Probes.Harness;
using KotH.Shared.Hosting;
using KotH.Shared.Mathematics;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using Stride.BepuPhysics;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    /// <summary>Puts the third-person camera behind every free spot, standing and crouched, at
    /// every view angle; fails if it ends in a wall, ceiling or floor (sphere overlaps more than a
    /// grazed face, a leg of its path blocked, near plane outside the sphere). Runs ViewMath.ThirdPerson.</summary>
    public sealed class ThirdPersonProbe : Probe
    {
        protected override string Tag => "thirdperson";

        // The camera's defaults, see PlayerCamera; a window of 16:9.
        const float Lift = 0.6f, Distance = 3.5f, Radius = 0.1f;
        const float Fov = 85f, Aspect = 16f / 9f, NearMargin = 0.7f;

        // Stride metres; the arena and a margin past its outermost box.
        const float Min = -26f, Max = 26f, Step = 0.5f;

        static readonly float[] Pitches = { -89f, -60f, -30f, -10f, 0f, 30f, 60f, 89f };

        // A sphere swept along a face it merely grazes is not stopped, so the
        // overlap allows a centimetre; the near plane needs far less room.
        const float Grazing = 0.01f;

        readonly CollidableComponent[] overlaps = new CollidableComponent[8];

        public override int Run(HeadlessWorld world, int maxTicks)
        {
            world.LoadArena(Maps.Test);
            WorldQueries q = world.Queries;
            var body = new PlayerMotor();

            float near = ViewMath.NearPlane(Radius, Fov, Aspect, NearMargin);
            float corner = near * ViewMath.Corner(Fov, Aspect);
            Log($"[thirdperson] near plane {near:F4} m, corner {corner:F4} m from the eye, sphere {Radius:F2} m");
            int failures = corner < Radius - Grazing ? 0 : 1;

            int spots = 0, views = 0, logged = 0;
            double shortest = double.MaxValue, total = 0;

            for (float x = Min; x <= Max; x += Step)
            for (float z = Min; z <= Max; z += Step)
            foreach (float floor in Floors(q, x, z))
            for (int pose = 0; pose < 2; pose++)
            {
                float height = pose == 0 ? body.StandHeight : body.CrouchHeight;
                float eyeHeight = pose == 0 ? body.StandEyeHeight : body.CrouchEyeHeight;
                float lifted = floor + body.SkinWidth;
                var bottom = new Vector3(x, lifted + body.BodyRadius, z);
                var top = new Vector3(x, lifted + height - body.BodyRadius, z);
                if (q.OverlapCapsule(bottom, top, body.BodyRadius, WorldQueries.WorldMask, overlaps) > 0)
                    continue;

                spots++;
                var eye = new Vector3(x, lifted + eyeHeight, z);
                for (int yaw = 0; yaw < 360; yaw += 15)
                foreach (float pitch in Pitches)
                {
                    views++;
                    ThirdPersonPath path = ViewMath.ThirdPerson(q, eye, yaw, pitch, Lift, Distance, Radius);
                    Vector3 cam = path.At(Distance);
                    shortest = Math.Min(shortest, path.Free);
                    total += path.Free;

                    string fault = Fault(q, eye, path, cam);
                    if (fault == null)
                        continue;

                    failures++;
                    if (logged++ < 20)
                        LogError($"[thirdperson] ERROR {fault}: eye ({eye.X:F2}; {eye.Y:F2}; {eye.Z:F2}) " +
                                 $"yaw {yaw} pitch {pitch:F0}, camera ({cam.X:F2}; {cam.Y:F2}; {cam.Z:F2}), free {path.Free:F3}");
                }
            }

            Log($"[thirdperson] {spots} free spots, {views} views, mean distance {total / views:F2} m, " +
                $"shortest {shortest:F3} m");
            Log(failures == 0 ? "[thirdperson] PASSED" : $"[thirdperson] {failures} errors");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>Why this camera would show the inside of something, or null.</summary>
        string Fault(WorldQueries q, Vector3 eye, ThirdPersonPath path, Vector3 cam)
        {
            if (q.OverlapCapsule(cam, cam, Radius - Grazing, WorldQueries.WorldMask, overlaps) > 0)
                return "sphere stuck in " + overlaps[0].Entity?.Name;

            if (Blocked(q, eye, path.Pivot) || Blocked(q, path.Pivot, cam))
                return "path blocked";

            return null;
        }

        static bool Blocked(WorldQueries q, Vector3 from, Vector3 to)
        {
            Vector3 d = UMath.Sub(to, from);
            float length = UMath.Magnitude(d);
            return length > 1e-4f &&
                   q.Raycast(from, UMath.Div(d, length), length, WorldQueries.WorldMask, out _);
        }

        /// <summary>Every surface straight below (x, z) a body could stand on, top down.</summary>
        static System.Collections.Generic.IEnumerable<float> Floors(WorldQueries q, float x, float z)
        {
            // A ray from inside a box does not see it, so starting just under
            // one top face finds the next floor below, such as under a slab.
            float from = 40f;
            for (int i = 0; i < 6; i++)
            {
                if (!q.Raycast(new Vector3(x, from, z), UMath.Down, from + 5f, WorldQueries.WorldMask, out QueryHit hit))
                    yield break;
                if (hit.Normal.Y > 0.7f)
                    yield return hit.Point.Y;
                from = hit.Point.Y - 0.01f;
            }
        }
    }
}
