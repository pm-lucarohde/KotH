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
    /// <summary>
    /// The hit's knockback against the percent before it: 0 % moves nothing,
    /// 100 % sends a body that does not steer 4 m along flat ground and 0.4 m
    /// up, linear to 1000 %. Then 100 % with the victim running against and
    /// along the knock: its own running simply adds on.
    /// </summary>
    public sealed class KnockbackProbe : Probe
    {
        protected override string Tag => "knockback";

        // Unity coordinates: open flat ground along +x, 8 m clear of the stairs.
        static readonly Vector3 AttackerSpot = new Vector3(-22f, 1f, -18f);
        static readonly Vector3 VictimSpot = new Vector3(-20f, 1f, -18f);

        static readonly float[] Percents = { 0f, 1.2f, 50f, 100f, 250f, 500f, 1000f };

        const int Settle = 60, MaxFlight = 600, RestTicks = 10;

        PlayerMotor attacker, victim;
        HeadlessWorld world;
        MeleeAttack attack;
        DamagePercent damage;
        ScriptedBody attackerInput, victimInput;

        public override int Run(HeadlessWorld world, int maxTicks)
        {
            this.world = world;
            Prototype scene = Prototype.Open(world);
            attacker = scene.Motor;
            attack = scene.Player.Get<MeleeAttack>();
            attackerInput = new ScriptedBody();
            scene.Player.Add(attackerInput);

            // A player body of its own, so it can be steered; it takes a
            // DamagePercent here, the game gives one only to the dummy.
            victimInput = new ScriptedBody();
            damage = new DamagePercent();
            Entity body = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, Ref.Pos(VictimSpot),
                                          Quaternion.Identity, victimInput, damage);
            body.Name = "Victim";
            victim = body.Get<PlayerMotor>();

            Log($"[knockback] Weite bei 100 % {attack.KnockbackDistance:F2} m, Höhe {attack.KnockbackHeight:F2} m");

            int failures = 0;
            float shortFlight = 0f;
            foreach (float percent in Percents)
            {
                (float distance, float rise, float angle, float after, _) = Hit(world, percent, 0f);

                float wantDistance = attack.KnockbackDistance * percent / 100f;
                float wantRise = attack.KnockbackHeight * percent / 100f;
                bool ok = percent == 0f
                    ? distance == 0f && rise == 0f
                    : MathF.Abs(distance - wantDistance) <= MathF.Max(0.02f, wantDistance * 0.01f)
                      && MathF.Abs(rise - wantRise) <= MathF.Max(0.01f, wantRise * 0.02f)
                      && MathF.Abs(angle) < 1f;
                ok &= MathF.Abs(after - MathF.Min(percent + attack.DamagePercent, 1000f)) < 1e-3f;

                if (!ok)
                    failures++;
                if (percent == 100f)
                    shortFlight = distance;

                Log($"[knockback] == {percent,6:F1} %: Weite {distance:F3} m (soll {wantDistance:F3}), " +
                    $"Höhe {rise:F3} m (soll {wantRise:F3}), Richtung {angle:F2}°, danach {after:F1} % -> " +
                    (ok ? "ok" : "ABWEICHUNG"));
            }

            // Running against the knock takes the running speed off, with it
            // adds it on. Each is compared to the flight plus the distance the
            // body ran on its own over the same time, measured alone. Without
            // the hop: in the air it could not speed up as fast as on the ground.
            float height = attack.KnockbackHeight;
            attack.KnockbackHeight = 0f;
            foreach (float run in new[] { -1f, 1f })
            {
                (float steered, _, _, _, int ticks) = Hit(world, 100f, run);
                float own = Walk(world, run, ticks);
                float want = shortFlight + own;
                bool ok = MathF.Abs(steered - want) <= 0.01f;
                if (!ok)
                    failures++;
                Log($"[knockback] == 100,0 % {(run < 0f ? "gegen den Stoß gelaufen" : "mit dem Stoß gelaufen")}: " +
                    $"{steered:F3} m in {ticks} Ticks (Stoß {shortFlight:F3} + eigenes Laufen {own:F3} = {want:F3}) -> " +
                    (ok ? "ok" : "ABWEICHUNG"));
            }
            attack.KnockbackHeight = height;

            Log(failures == 0 ? "[knockback] RÜCKSTOSS WIE VORGEGEBEN" : $"[knockback] {failures} FÄLLE WEICHEN AB");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// One swing at a victim charged to percent, then waits until the
        /// knock is spent. run is the victim's own input along +x from the
        /// hit on (-1 against, 1 along). Distance and angle are horizontal,
        /// signed along +x, from where it stood when hit; rise is the peak.
        /// </summary>
        (float distance, float rise, float angle, float after, int ticks) Hit(HeadlessWorld world, float percent, float run)
        {
            Place();
            damage.Restore(percent);
            attack.ResetCounters();
            attackerInput.Input.AttackPresses = 1;
            world.Simulation.Tick();
            attackerInput.Input.AttackPresses = 0;
            StepAside();
            victimInput.Input = new MoveInput { Yaw = Ref.Angle(90f), Move = new Vector2(0f, run) };

            Vector3 start = Ref.ToUnity(victim.Position);
            float top = start.Y;
            int resting = 0, t = 0;
            for (; t < MaxFlight && resting < RestTicks; t++)
            {
                world.Simulation.Tick();
                top = MathF.Max(top, victim.Position.Y);
                bool spent = victim.KnockLeft == 0f && victim.IsGrounded;
                resting = spent ? resting + 1 : 0;
            }

            victimInput.Input.Move = Vector2.Zero;
            Vector3 end = Ref.ToUnity(victim.Position);
            float dx = end.X - start.X, dz = end.Z - start.Z;
            float distance = MathF.Sign(dx) * MathF.Sqrt(dx * dx + dz * dz);
            float angle = distance != 0f ? MathF.Atan2(dz, MathF.Abs(dx)) * 180f / MathF.PI : 0f;
            return (distance, top - start.Y, angle, damage.Percent, t);
        }

        /// <summary>How far the victim runs on its own in ticks, from standing, signed along +x.</summary>
        float Walk(HeadlessWorld world, float run, int ticks)
        {
            Place();
            StepAside();
            float start = Ref.ToUnity(victim.Position).X;
            victimInput.Input = new MoveInput { Yaw = Ref.Angle(90f), Move = new Vector2(0f, run) };
            for (int t = 0; t < ticks; t++)
                world.Simulation.Tick();
            victimInput.Input.Move = Vector2.Zero;
            return Ref.ToUnity(victim.Position).X - start;
        }

        /// <summary>
        /// The attacker off the line once it has swung: a victim running back
        /// would otherwise run into it, and the bodies' push would count.
        /// </summary>
        void StepAside() => attacker.Teleport(Ref.Pos(new Vector3(-22f, 1f, -14f)));

        /// <summary>Both bodies to their spots, settled with no input.</summary>
        void Place()
        {
            // Unity yaw 90 looks along +x: the attacker at the victim, the
            // victim away from it, so its forward key runs with the knock.
            attacker.Teleport(Ref.Pos(AttackerSpot));
            victim.Teleport(Ref.Pos(VictimSpot));
            attackerInput.Input = new MoveInput { Yaw = Ref.Angle(90f) };
            victimInput.Input = new MoveInput { Yaw = Ref.Angle(90f) };
            for (int i = 0; i < Settle; i++)
                world.Simulation.Tick();
        }
    }
}
