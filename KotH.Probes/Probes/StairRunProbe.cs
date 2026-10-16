using System;
using KotH.Probes.Harness;
using KotH.Shared.Hosting;
using KotH.Shared.Player;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    /// <summary>
    /// Walks and sprints up the climbable flights (Steps20/30/40) from a
    /// grid of starts: across the width, at angles up to 40 degrees and at
    /// twelve run-up phases. Fails if the body stalls on a flight. Before
    /// the step-up settle window a sprint stalled in 154 of 2916 runs: the
    /// next riser arrived while the body still hovered from the last step.
    /// </summary>
    public sealed class StairRunProbe : Probe
    {
        protected override string Tag => "stairs";

        static readonly float[] FlightX = { -6f, -10f, -14f };
        static readonly float[] Risers = { 0.2f, 0.3f, 0.4f };

        // Flights start at z -10.0 and end at -6.4, 2 m wide; only runs that
        // stay well inside that footprint are judged.
        const float FlightStartZ = -10.4f, FlightEndZ = -6.4f, HalfWidth = 0.6f;
        const int Ticks = 160, StallTicks = 2;

        public override int Run(HeadlessWorld world, int maxTicks)
        {
            PlayerMotor motor = Prototype.Open(world).Motor;
            int failures = 0;

            for (int f = 0; f < FlightX.Length; f++)
            for (int s = 0; s < 2; s++)
            {
                bool sprint = s == 1;
                float slow = sprint ? 7.5f : 4.5f;   // m/s, well below either speed
                int runs = 0, stalls = 0, logged = 0;

                for (int xo = -4; xo <= 4; xo++)
                for (int yw = -4; yw <= 4; yw++)
                for (int zo = 0; zo < 12; zo++)
                {
                    float yaw = yw * 10f;
                    var start = new Vector3(FlightX[f] + xo * 0.2f, 0.9f, -12.5f - zo * 0.05f);
                    float top = Risers[f] * 6f;

                    // At rest first, so no run inherits the last one's state.
                    motor.Teleport(Ref.Pos(start));
                    for (int i = 0; i < 60; i++)
                        motor.Tick(new MoveInput { Yaw = Ref.Angle(yaw) }, FixedDeltaTime);

                    bool reached = false;
                    int run = 0, worst = 0, worstAt = -1;
                    for (int t = 0; t < Ticks && !reached; t++)
                    {
                        Vector3 before = Ref.ToUnity(motor.Position);
                        motor.Tick(new MoveInput { Move = new Vector2(0f, 1f), Yaw = Ref.Angle(yaw), Sprint = sprint },
                                   FixedDeltaTime);
                        Vector3 p = Ref.ToUnity(motor.Position);

                        reached = p.Y - 0.9f >= top - 0.03f;
                        float flat = new Vector2(p.X - before.X, p.Z - before.Z).Length() / FixedDeltaTime;
                        bool onFlight = p.Z > FlightStartZ && p.Z < FlightEndZ
                                        && Math.Abs(p.X - FlightX[f]) < HalfWidth;

                        run = !reached && onFlight && t > 5 && flat < slow ? run + 1 : 0;
                        if (run > worst)
                        {
                            worst = run;
                            worstAt = t;
                        }
                    }

                    runs++;
                    if (worst >= StallTicks)
                    {
                        stalls++;
                        if (logged++ < 3)
                            Log($"[stairs]    stockt: x {start.X:F1} z {start.Z:F2} Blick {yaw:F0}°," +
                                $" {worst} Ticks bis Tick {worstAt} -> ABWEICHUNG");
                    }
                }

                failures += stalls;
                Log($"[stairs] == Steps{Risers[f] * 100f:F0} {(sprint ? "Sprint" : "Gehen")}: {runs} Läufe," +
                    $" {stalls} gestockt -> {(stalls == 0 ? "ok" : "ABWEICHUNG")}");
            }

            Log(failures == 0 ? "[stairs] ALLE LÄUFE OHNE STOCKEN" : $"[stairs] {failures} LÄUFE GESTOCKT");
            return failures == 0 ? 0 : 1;
        }
    }
}
