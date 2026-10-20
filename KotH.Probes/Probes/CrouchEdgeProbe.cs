using System;
using KotH.Probes.Harness;
using KotH.Shared.Hosting;
using KotH.Shared.Player;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    /// <summary>
    /// Creeps crouched into the stair flights whose first riser is too tall
    /// to step onto crouched, straight on and at an angle, and keeps pushing.
    /// The body must come to rest against the riser or slide along it, never
    /// jitter: a reversal of its vertical or forward motion that follows the
    /// last one within a few ticks counts. One turn is a path, not a jitter,
    /// and only ticks at this flight are judged, not wherever a run strays.
    /// Found in play: against Steps30 the body bobbed on the lip.
    /// </summary>
    public sealed class CrouchEdgeProbe : Probe
    {
        protected override string Tag => "crouchedge";

        // Unity coordinates: flights sit at z -10 and climb towards +z.
        static readonly (string name, float x)[] Flights =
        {
            ("Steps30", -10f),
            ("Steps40", -14f),
            ("Steps50", -18f),
        };

        const int Settle = 60, Ticks = 120, Judged = 60;

        // Half a millimetre: a direction change smaller than that is float
        // noise of the sweep, not something anyone sees.
        const float Noise = 0.0005f;
        const int Window = 8;

        // Flights are 2 m wide; this much beside the axis still touches one.
        const float AtFlight = 1f + 0.4f + 0.1f;

        public override int Run(HeadlessWorld world, int maxTicks)
        {
            PlayerMotor motor = Prototype.Open(world).Motor;
            int failures = 0;

            foreach (var flight in Flights)
            {
                int runs = 0, jittered = 0, logged = 0;

                for (int xo = -2; xo <= 2; xo++)
                for (int yw = -4; yw <= 4; yw++)
                {
                    float yaw = yw * 15f;
                    var start = new Vector3(flight.x + xo * 0.3f, 1.0f, -11.5f);

                    // A little above the ground, so the body lands and keeps
                    // its skin like in play. Put exactly on it, it rests with
                    // no gap and every seam of the ground mesh catches it.
                    motor.Teleport(Ref.Pos(start));
                    for (int i = 0; i < Settle; i++)
                        motor.Tick(new MoveInput { Yaw = Ref.Angle(yaw), Crouch = true }, FixedDeltaTime);

                    Vector3 last = Ref.ToUnity(motor.Position);
                    var y = new Axis();
                    var z = new Axis();

                    for (int t = 0; t < Ticks; t++)
                    {
                        motor.Tick(new MoveInput { Move = new Vector2(0f, 1f), Yaw = Ref.Angle(yaw), Crouch = true },
                                   FixedDeltaTime);

                        Vector3 now = Ref.ToUnity(motor.Position);
                        if (t >= Ticks - Judged && Math.Abs(now.X - flight.x) < AtFlight)
                        {
                            y.Step(now.Y - last.Y, t);
                            z.Step(now.Z - last.Z, t);
                        }
                        last = now;
                    }

                    runs++;
                    if (y.Jitter + z.Jitter > 0)
                    {
                        jittered++;
                        if (logged++ < 3)
                            Log($"[crouchedge]    zappelt: x {start.X:F1} Blick {yaw:F0}°, {y.Jitter} schnelle Umkehrungen " +
                                $"senkrecht, {z.Jitter} vorwärts, Ende bei {Ref.V(motor.Position)} -> ABWEICHUNG");
                    }
                }

                failures += jittered;
                Log($"[crouchedge] == {flight.name} geduckt: {runs} Läufe, {jittered} zappeln -> " +
                    (jittered == 0 ? "ok" : "ABWEICHUNG"));
            }

            Log(failures == 0 ? "[crouchedge] KEIN ZAPPELN AN DEN STUFEN" : $"[crouchedge] {failures} LÄUFE ZAPPELN");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>Counts reversals above the noise that come within Window of the last.</summary>
        struct Axis
        {
            int sign, lastFlip;
            public int Jitter;

            public void Step(float delta, int tick)
            {
                if (Math.Abs(delta) < Noise)
                    return;

                int now = delta > 0f ? 1 : -1;
                if (sign != 0 && now != sign)
                {
                    if (lastFlip > 0 && tick - lastFlip <= Window)
                        Jitter++;
                    lastFlip = tick;
                }
                sign = now;
            }
        }
    }
}
