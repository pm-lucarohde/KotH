using System;
using KotH.Probes.Harness;
using KotH.Shared.Hosting;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Probes
{
    /// <summary>
    /// Crawls crouched under Ramp20 and Ramp35 towards their foot, where the
    /// tilted box meets the ground and the gap underneath closes: one body
    /// alone, along the ramps' side edges, then a crowd that shoves itself
    /// apart while it crawls. Fails if any body ends up below the ground.
    /// Found by the server's load test: bodies in that wedge sank through.
    /// </summary>
    public sealed class RampFootProbe : Probe
    {
        protected override string Tag => "rampfoot";

        // Unity coordinates: ramp centre x and z; both climb towards +z.
        static readonly (string name, float x, float z)[] Ramps =
        {
            ("Ramp20", -3.5f, 3.23f),
            ("Ramp35", 3.5f, 6.6f),
        };

        const int Settle = 60, Ticks = 160;
        const int EdgeTicks = 80;
        const int CrowdSize = 6, CrowdTicks = 300, CrowdRounds = 4;

        public override int Run(HeadlessWorld world, int maxTicks)
        {
            PlayerMotor motor = Prototype.Open(world).Motor;
            int failures = 0;

            foreach (var ramp in Ramps)
            for (int s = 0; s < 2; s++)
            {
                bool sprint = s == 1;
                int runs = 0, sunk = 0, logged = 0;

                for (int xo = -3; xo <= 3; xo++)
                for (int yw = -2; yw <= 2; yw++)
                {
                    float yaw = 180f + yw * 15f;
                    var start = new Vector3(ramp.x + xo * 0.5f, 0.9f, ramp.z + 1.5f);

                    // Crouched at rest first, so no run inherits the last one's state.
                    motor.Teleport(Ref.Pos(start));
                    for (int i = 0; i < Settle; i++)
                        motor.Tick(new MoveInput { Yaw = Ref.Angle(yaw), Crouch = true }, FixedDeltaTime);

                    float lowest = float.MaxValue;
                    int lowestAt = -1;
                    for (int t = 0; t < Ticks; t++)
                    {
                        motor.Tick(new MoveInput
                        {
                            Move = new Vector2(0f, 1f), Yaw = Ref.Angle(yaw), Crouch = true, Sprint = sprint,
                        }, FixedDeltaTime);

                        float y = motor.Position.Y;
                        if (y < lowest)
                        {
                            lowest = y;
                            lowestAt = t;
                        }
                    }

                    runs++;
                    if (lowest < 0.5f)
                    {
                        sunk++;
                        if (logged++ < 3)
                            Log($"[rampfoot]    sunk in: x {start.X:F1} view {yaw:F0}°, lowest height {lowest:F3} " +
                                $"in tick {lowestAt}, end at {Ref.V(motor.Position)} -> DEVIATION");
                    }
                }

                failures += sunk;
                Log($"[rampfoot] == {ramp.name} crouched{(sprint ? " with sprint" : "")}: {runs} runs, " +
                    $"{sunk} sunk in -> {(sunk == 0 ? "ok" : "ABWEICHUNG")}");
            }

            failures += SideEdges(motor);
            failures += Crowds(world, motor);

            Log(failures == 0 ? "[rampfoot] NO BODY BELOW THE GROUND" : $"[rampfoot] {failures} RUNS SUNK IN");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Starts beside each side edge, from half under it to a body width
        /// clear, and crawls in at an angle towards the foot. Starts deep in
        /// the ramp that leave no footing after settling are skipped.
        /// </summary>
        int SideEdges(PlayerMotor motor)
        {
            int failures = 0;
            foreach (var ramp in Ramps)
            for (int side = -1; side <= 1; side += 2)
            {
                int runs = 0, skipped = 0, sunk = 0, logged = 0;
                for (int xi = 0; xi <= 10; xi++)
                for (int zi = 0; zi <= 14; zi++)
                for (int yi = 0; yi <= 8; yi++)
                {
                    var start = new Vector3(ramp.x + side * (2f + 0.1f * xi), 0.9f, ramp.z - 5f + 0.5f * zi);
                    float yaw = 180f + side * 5f * yi;

                    motor.Teleport(Ref.Pos(start));
                    for (int i = 0; i < Settle; i++)
                        motor.Tick(new MoveInput { Yaw = Ref.Angle(yaw), Crouch = true }, FixedDeltaTime);
                    if (!motor.IsGrounded || motor.Position.Y < 0.5f)
                    {
                        skipped++;
                        continue;
                    }

                    float lowest = float.MaxValue;
                    for (int t = 0; t < EdgeTicks && lowest >= 0.5f; t++)
                    {
                        motor.Tick(new MoveInput { Move = new Vector2(0f, 1f), Yaw = Ref.Angle(yaw), Crouch = true },
                                   FixedDeltaTime);
                        lowest = Math.Min(lowest, motor.Position.Y);
                    }

                    runs++;
                    if (lowest < 0.5f)
                    {
                        sunk++;
                        if (logged++ < 3)
                            Log($"[rampfoot]    sunk in: x {start.X:F1} z {start.Z:F1} view {yaw:F0}°, " +
                                $"end at {Ref.V(motor.Position)} -> DEVIATION");
                    }
                }

                failures += sunk;
                Log($"[rampfoot] == {ramp.name} edge {(side < 0 ? "links" : "rechts")}: {runs} runs " +
                    $"({skipped} starts without hold), {sunk} sunk in -> {(sunk == 0 ? "ok" : "ABWEICHUNG")}");
            }
            return failures;
        }

        /// <summary>
        /// A crowd crawling under each ramp through the real SimulationSystem,
        /// since only there do bodies separate each other. Strafing flips at
        /// seeded random ticks, so the bodies keep shoving sideways.
        /// </summary>
        int Crowds(HeadlessWorld world, PlayerMotor alone)
        {
            alone.Teleport(Ref.Pos(-20f, 0.9f, -20f));   // out of the way
            var bodies = new ScriptedBody[CrowdSize];
            for (int i = 0; i < CrowdSize; i++)
            {
                bodies[i] = new ScriptedBody();
                world.SpawnBody(HeadlessWorld.PlayerBodyUrl, Ref.Pos(-20f + i * 2f, 0.9f, 20f),
                                Quaternion.Identity, bodies[i]);
            }

            int failures = 0;
            uint rng = 12345u;
            foreach (var ramp in Ramps)
            for (int round = 0; round < CrowdRounds; round++)
            {
                // Two rows of three under the ramp's high end, crouched first.
                for (int i = 0; i < CrowdSize; i++)
                {
                    var at = new Vector3(ramp.x - 1.2f + (i % 3) * 1.2f, 0.9f, ramp.z + 1.0f + (i / 3) * 1.0f);
                    bodies[i].Motor.Teleport(Ref.Pos(at));
                    bodies[i].Input = new MoveInput { Yaw = Ref.Angle(180f), Crouch = true };
                }
                for (int t = 0; t < Settle; t++)
                    world.Simulation.Tick();

                var strafe = new float[CrowdSize];
                float lowest = float.MaxValue;
                int lowestBody = -1, lowestTick = -1;
                for (int t = 0; t < CrowdTicks; t++)
                {
                    for (int i = 0; i < CrowdSize; i++)
                    {
                        rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                        if (rng % 10u == 0u)
                            strafe[i] = (rng >> 8) % 3u - 1f;
                        bodies[i].Input = new MoveInput
                        {
                            Move = new Vector2(strafe[i] * 0.8f, 1f), Yaw = Ref.Angle(180f), Crouch = true,
                        };
                    }
                    world.Simulation.Tick();

                    for (int i = 0; i < CrowdSize; i++)
                        if (bodies[i].Motor.Position.Y < lowest)
                        {
                            lowest = bodies[i].Motor.Position.Y;
                            lowestBody = i;
                            lowestTick = t;
                        }
                }

                bool sunk = lowest < 0.5f;
                if (sunk)
                    failures++;
                Log($"[rampfoot] == {ramp.name} crowd {round + 1}: {CrowdSize} bodies, lowest height {lowest:F3}" +
                    (sunk ? $" (body {lowestBody}, tick {lowestTick}) -> DEVIATION" : " -> ok"));
            }
            return failures;
        }
    }
}
