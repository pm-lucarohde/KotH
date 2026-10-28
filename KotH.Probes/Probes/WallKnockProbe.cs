using System;
using KotH.Probes.Harness;
using KotH.Shared.Combat;
using KotH.Shared.Hosting;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Probes
{
    /// <summary>Knocks a body hard into the Plateau's wall, straight and at angles, until it rests:
    /// once the wall stopped it, nothing may carry it back towards the wall, in the air or landing
    /// (in play a dummy above 100 % flew back along the hit on touching ground).</summary>
    public sealed class WallKnockProbe : Probe
    {
        protected override string Tag => "wallknock";

        // Stride metres: the Plateau's face towards +z stands at z -10,
        // 12 m wide. Every hit goes into it, -z, turned by the angle.
        static readonly Vector3 VictimSpot = new Vector3(0f, 0.9f, -7f);
        static readonly Vector3 IntoWall = new Vector3(0f, 0f, -1f);
        const float Reach = 2f;

        static readonly float[] Percents = { 100f, 150f, 250f, 400f, 600f, 1000f };
        static readonly float[] Angles = { 0f, 15f, 30f, 45f };

        // Millimetres per tick towards the wall that still count as resting.
        const float Noise = 0.001f;
        const int Settle = 60, MaxTicks = 400, RestTicks = 10;

        public bool Trace { get; init; }

        public override int Run(HeadlessWorld world, int maxTicks)
        {
            Prototype scene = Prototype.Open(world);
            PlayerMotor attacker = scene.Motor;
            MeleeAttack attack = scene.Player.Get<MeleeAttack>();
            var attackerInput = new ScriptedBody();
            scene.Player.Add(attackerInput);

            var victimInput = new ScriptedBody();
            var damage = new DamagePercent();
            Entity body = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, VictimSpot, Quaternion.Identity,
                                          victimInput, damage);
            body.Name = "Victim";
            PlayerMotor victim = body.Get<PlayerMotor>();

            int failures = 0;
            foreach (float angle in Angles)
            foreach (float percent in Percents)
            {
                // Yaw 0 looks along -z; the hit leaves the attacker along its view.
                float rad = angle * UMath.Deg2Rad;
                var hit = new Vector3(-UMath.Sin(rad), 0f, -UMath.Cos(rad));
                attacker.Teleport(UMath.Sub(VictimSpot, UMath.Mul(hit, Reach)));
                victim.Teleport(VictimSpot);
                attackerInput.Input = new MoveInput { Yaw = angle };
                victimInput.Input = new MoveInput { Yaw = 180f };
                for (int i = 0; i < Settle; i++)
                    world.Simulation.Tick();

                damage.Restore(percent);
                attack.ResetCounters();
                attackerInput.Input.AttackPresses = 1;
                world.Simulation.Tick();
                attackerInput.Input.AttackPresses = 0;
                if (attack.HitCount != 1)
                {
                    LogError($"[wallknock] {angle:F0}° {percent:F0} %: no hit");
                    failures++;
                    continue;
                }

                // Out of the way, so the body can come back freely.
                attacker.Teleport(new Vector3(8f, 0.9f, -5f));

                bool walled = false;
                int backAfterWall = 0, worstTick = -1, rest = 0, t = 0;
                float deepest = float.MaxValue, worst = 0f;
                Vector3 last = victim.Position;
                for (; t < MaxTicks && rest < RestTicks; t++)
                {
                    world.Simulation.Tick();
                    Vector3 p = victim.Position;
                    float towards = UMath.Dot(UMath.Sub(p, last), IntoWall);
                    deepest = Math.Min(deepest, p.Z);

                    // The wall stopped it once it moves away from it at all.
                    if (!walled && towards < -Noise)
                        walled = true;
                    else if (walled && towards > Noise)
                    {
                        backAfterWall++;
                        if (towards > worst)
                        {
                            worst = towards;
                            worstTick = t;
                        }
                    }

                    if (Trace)
                        Log($"[wallknock] {angle:F0}° {percent:F0} % t {t,3} x {p.X:F3} z {p.Z:F3} y {p.Y:F3} vz {victim.Velocity.Z:F3} " +
                            $"vy {victim.Velocity.Y:F3} knock {victim.KnockLeft:F3} ground {victim.IsGrounded}");

                    rest = victim.IsGrounded && victim.KnockLeft == 0f &&
                           UMath.Magnitude(UMath.Sub(p, last)) < Noise ? rest + 1 : 0;
                    last = p;
                }

                bool ok = backAfterWall == 0;
                if (!ok)
                    failures++;
                Log($"[wallknock] {angle,2:F0}° {percent,4:F0} %: {(walled ? "abgeprallt" : "did not bounce")}, " +
                    $"to z {deepest:F2}, rest after {t} ticks at z {victim.Position.Z:F2}, " +
                    $"then towards the wall {backAfterWall} ticks" +
                    (ok ? "" : $", strongest {worst / FixedDeltaTime:F2} m/s in tick {worstTick}") +
                    (ok ? "  ok" : "  ERROR"));
            }

            Log(failures == 0 ? "[wallknock] PASSED" : $"[wallknock] {failures} errors");
            return failures == 0 ? 0 : 1;
        }
    }
}
