using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using KotH.Client.Player;
using KotH.Probes.Harness;
using KotH.Shared.Hosting;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    /// <summary>
    /// The client's clock against Unity's player loop. Replays the frame
    /// times Unity measured (dump/clock.txt, hitches up to a second) through
    /// TickClock and compares, frame by frame, the ticks run, the alpha, the
    /// interpolated eye the camera sits at and its near plane. At frame 30
    /// the player is lifted 3 m, as in Unity, so the camera follows a fall.
    /// </summary>
    public sealed class ClockProbe : Probe
    {
        const int LiftFrame = 30;
        const float FieldOfView = 85f, NearClipMargin = 0.7f;

        // Unity's alpha came from float Time.time, about 5e-7 s coarse at
        // 7 s; the camera inherits that times the fall speed.
        const float AlphaTolerance = 1e-4f, PositionTolerance = 1e-4f, NearTolerance = 1e-6f;

        readonly string dumpPath;

        public ClockProbe(string dumpPath)
        {
            this.dumpPath = dumpPath;
        }

        protected override string Tag => "clock";

        sealed class Frame
        {
            public int Index, Fixed, Tick;
            public double Udt, Time, FixedTime;
            public float Alpha, Near, Aspect;
            public Vector3 Camera, Eye, Previous;
        }

        public override int Run(HeadlessWorld world, int maxTicks)
        {
            List<Frame> frames = Load(Path.Combine(dumpPath, "clock.txt"));
            Prototype p = Prototype.Open(world);
            SimulationSystem system = world.Simulation;

            // In Unity the player ticked with its input reader on and no
            // device input; a scripted body with an empty input is the same.
            p.Player.Add(new ScriptedBody());

            // Unity's first two frames run on a fixed 0.02 s with one tick at
            // time 0; the engines start differently, so the replay begins
            // from Unity's state after frame 1.
            Frame start = frames[1];
            for (int i = 0; i < start.Tick; i++)
                system.Tick();
            var clock = new TickClock { SinceTick = start.Time - start.FixedTime };

            int tickMisses = 0, compared = 0;
            float alphaMax = 0f, eyeMax = 0f, cameraMax = 0f, nearMax = 0f;

            for (int f = 2; f < frames.Count; f++)
            {
                Frame u = frames[f];
                int due = clock.Advance(u.Udt);
                for (int i = 0; i < due; i++)
                    system.Tick();

                if (f == LiftFrame)
                    p.Motor.Teleport(p.Player.Transform.Position + Vector3.UnitY * 3f);

                float alpha = clock.Alpha;
                Vector3 camera = Ref.ToUnity(ViewMath.Eye(p.Motor, alpha));
                Vector3 eye = Ref.ToUnity(p.Motor.EyePosition);
                Vector3 previous = Ref.ToUnity(p.Motor.PreviousEyePosition);
                float near = ViewMath.NearPlane(p.Motor, FieldOfView, u.Aspect, NearClipMargin);

                float dAlpha = Math.Abs(alpha - u.Alpha);
                float dEye = Math.Max((eye - u.Eye).Length(), (previous - u.Previous).Length());
                float dCamera = (camera - u.Camera).Length();
                float dNear = Math.Abs(near - u.Near);
                bool tickOk = due == u.Fixed && SimulationSystem.CurrentTick == u.Tick;

                alphaMax = Math.Max(alphaMax, dAlpha);
                eyeMax = Math.Max(eyeMax, dEye);
                cameraMax = Math.Max(cameraMax, dCamera);
                nearMax = Math.Max(nearMax, dNear);
                compared++;

                if (!tickOk)
                    tickMisses++;

                if (!tickOk || dAlpha > AlphaTolerance || dCamera > PositionTolerance || dEye > PositionTolerance
                    || dNear > NearTolerance)
                {
                    Log($"[clock] ABWEICHUNG f {u.Index}: Ticks {due} (Unity {u.Fixed}), Zähler {SimulationSystem.CurrentTick} " +
                        $"(Unity {u.Tick}), Alpha {alpha:F6} (Unity {u.Alpha:F6}), Kamera {Ref.V(Ref.Pos(camera))} " +
                        $"(Unity {Ref.V(Ref.Pos(u.Camera))}), Nahebene {near:F6} (Unity {u.Near:F6})");
                }
            }

            bool ok = tickMisses == 0 && alphaMax <= AlphaTolerance && eyeMax <= PositionTolerance
                      && cameraMax <= PositionTolerance && nearMax <= NearTolerance;
            Log($"[clock] == {compared} Frames, Ticks je Frame abweichend {tickMisses}, " +
                $"Alpha max {alphaMax:E2}, Auge max {eyeMax:E2} m, Kamera max {cameraMax:E2} m, " +
                $"Nahebene max {nearMax:E2} m");
            Log(ok ? "[clock] UHR WIE UNITY" : "[clock] UHR WEICHT AB");
            return ok ? 0 : 1;
        }

        static List<Frame> Load(string path)
        {
            var c = CultureInfo.InvariantCulture;
            var frames = new List<Frame>();
            foreach (string line in File.ReadLines(path))
            {
                string[] w = line.Split(' ');
                var values = new Dictionary<string, int>();
                for (int i = 1; i < w.Length; i++)
                    if (!char.IsDigit(w[i][0]) && w[i][0] != '-' && !values.ContainsKey(w[i]))
                        values[w[i]] = i;

                double D(string key, int offset = 1) => double.Parse(w[values[key] + offset], c);
                float F(string key, int offset = 1) => (float)D(key, offset);
                Vector3 V(string key) => new Vector3(F(key, 1), F(key, 2), F(key, 3));

                frames.Add(new Frame
                {
                    Index = (int)D("f"),
                    Udt = D("udt"),
                    Time = D("t"),
                    FixedTime = D("ft"),
                    Fixed = (int)D("fixed"),
                    Tick = (int)D("tick"),
                    Alpha = F("alpha"),
                    Camera = V("cam"),
                    Eye = V("eye"),
                    Previous = V("prev"),
                    Near = F("near"),
                    Aspect = F("aspect"),
                });
            }
            return frames;
        }
    }
}
