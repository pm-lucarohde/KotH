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
    /// 100 % sends a body that does not steer 12 m along flat ground and 1.2 m
    /// up, linear to 1000 %. Then 100 % with the victim running against and
    /// along the knock: its own running simply adds on. Last the attacker's
    /// own speed: a share of it from 10 %, all of it from 20 %.
    /// </summary>
    public sealed class KnockbackProbe : Probe
    {
        protected override string Tag => "knockback";

        // Unity coordinates: open flat ground along +x, 8 m clear of the stairs.
        // The ground ends at x 25; a knock that carries further is measured
        // in the air, where it sheds just the same.
        static readonly Vector3 AttackerSpot = new Vector3(-22f, 1f, -18f);
        static readonly Vector3 VictimSpot = new Vector3(-20f, 1f, -18f);

        static readonly float[] Percents = { 0f, 1.2f, 50f, 100f, 250f, 500f, 1000f };

        const int Settle = 60, MaxFlight = 600, RestTicks = 10;

        PlayerMotor attacker, victim;
        HeadlessWorld world;
        MeleeAttack attack;
        DamagePercent damage;
        ScriptedBody attackerInput, victimInput;

        /// <summary>One knock as measured; Along is the attacker's speed towards the victim.</summary>
        struct Flight
        {
            public float Distance, Rise, Angle, After, Along;
            public int Ticks;
            public bool Off;
        }

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

            Log($"[knockback] Weite bei 100 % {attack.KnockbackDistance:F2} m, Höhe {attack.KnockbackHeight:F2} m, " +
                $"Schlägertempo ab {attack.CarryFromPercent:F0} % mit Anteil {attack.CarryStartShare:F2}, " +
                $"voll ab {attack.CarryFullPercent:F0} %");

            int failures = 0;
            float shortFlight = 0f;
            foreach (float percent in Percents)
            {
                Flight f = Hit(percent, 0f);

                float wantDistance = attack.KnockbackDistance * percent / 100f;
                float wantRise = attack.KnockbackHeight * percent / 100f;
                bool ok = percent == 0f
                    ? f.Distance == 0f && f.Rise == 0f
                    : MathF.Abs(f.Distance - wantDistance) <= MathF.Max(0.02f, wantDistance * 0.01f)
                      && MathF.Abs(f.Rise - wantRise) <= MathF.Max(0.01f, wantRise * 0.02f)
                      && MathF.Abs(f.Angle) < 1f;
                ok &= MathF.Abs(f.After - MathF.Min(percent + attack.DamagePercent, 1000f)) < 1e-3f;

                if (!ok)
                    failures++;
                if (percent == 100f)
                    shortFlight = f.Distance;

                Log($"[knockback] == {percent,6:F1} %: Weite {f.Distance:F3} m (soll {wantDistance:F3}), " +
                    $"Höhe {f.Rise:F3} m (soll {wantRise:F3}), Richtung {f.Angle:F2}°, danach {f.After:F1} %" +
                    (f.Off ? ", über den Rand der Arena (in der Luft gemessen)" : "") + " -> " +
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
                Flight f = Hit(100f, run);
                float own = Walk(run, f.Ticks);
                float want = shortFlight + own;
                bool ok = MathF.Abs(f.Distance - want) <= 0.01f && !f.Off;
                if (!ok)
                    failures++;
                Log($"[knockback] == 100,0 % {(run < 0f ? "gegen den Stoß gelaufen" : "mit dem Stoß gelaufen")}: " +
                    $"{f.Distance:F3} m in {f.Ticks} Ticks (Stoß {shortFlight:F3} + eigenes Laufen {own:F3} = {want:F3}) -> " +
                    (ok ? "ok" : "ABWEICHUNG"));
            }
            attack.KnockbackHeight = height;

            // The attacker's speed towards the victim: its share is added to
            // the launch speed, the distance then follows from braking at the
            // air acceleration. Backing away takes some off, never past zero.
            var carried = new (float percent, float run, bool sprint, string what)[]
            {
                (0f, 1f, true, "sprintet heran"),
                (5f, 1f, false, "geht heran"),
                (10f, 1f, false, "geht heran"),
                (15f, 1f, false, "geht heran"),
                (20f, 1f, false, "geht heran"),
                (100f, 1f, false, "geht heran"),
                (100f, 1f, true, "sprintet heran"),
                (100f, -1f, false, "geht rückwärts weg"),
                (10f, -1f, false, "geht rückwärts weg"),
            };
            foreach (var c in carried)
            {
                Flight f = Hit(c.percent, 0f, c.run, c.sprint);
                float share = attack.CarryShare(c.percent);
                float want = Travel(c.percent, share * f.Along);
                bool ok = c.percent == 0f
                    ? f.Distance == 0f && f.Rise == 0f
                    : MathF.Abs(f.Distance - want) <= MathF.Max(0.02f, want * 0.01f) && MathF.Abs(f.Angle) < 1f;
                if (!ok)
                    failures++;
                Log($"[knockback] == {c.percent,5:F1} %, Schläger {c.what} mit {f.Along:F3} m/s, Anteil {share:F3}: " +
                    $"Weite {f.Distance:F3} m (soll {want:F3}, im Stand {Travel(c.percent, 0f):F3})" +
                    (f.Off ? ", über den Rand der Arena (in der Luft gemessen)" : "") + " -> " +
                    (ok ? "ok" : "ABWEICHUNG"));
            }

            Log(failures == 0 ? "[knockback] RÜCKSTOSS WIE VORGEGEBEN" : $"[knockback] {failures} FÄLLE WEICHEN AB");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// How far a knock at percent carries with extra launch speed carry on
        /// top: the motor brakes before it moves, so a launch speed v covers
        /// v(v - a*dt)/2a, the inverse of PlayerMotor.LaunchSpeed.
        /// </summary>
        float Travel(float percent, float carry)
        {
            float a = victim.AirAcceleration;
            float v = PlayerMotor.LaunchSpeed(attack.KnockbackDistance * percent / 100f, a, FixedDeltaTime) + carry;
            if (percent <= 0f || v <= 0f)
                return 0f;
            return v * (v - a * FixedDeltaTime) / (2f * a);
        }

        /// <summary>
        /// One swing at a victim charged to percent, then waits until the
        /// knock is spent. run is the victim's own input along +x from the
        /// hit on (-1 against, 1 along); attackerRun is the attacker's while
        /// it swings (1 at the victim, -1 backing away). Distance and angle
        /// are horizontal, signed along +x, from where the victim stood when
        /// hit; rise is the peak.
        /// </summary>
        Flight Hit(float percent, float run, float attackerRun = 0f, bool sprint = false)
        {
            Place();
            damage.Restore(percent);
            attack.ResetCounters();
            RunUp(attackerRun, sprint);
            attackerInput.Input.AttackPresses = 1;
            world.Simulation.Tick();
            attackerInput.Input.AttackPresses = 0;

            // The same speed and line the swing saw: combat is the last phase.
            Vector3 line = UMath.Sub(victim.Position, attacker.Position);
            line.Y = 0f;
            Vector3 speed = new Vector3(attacker.Velocity.X, 0f, attacker.Velocity.Z);
            float along = UMath.Dot(speed, UMath.Normalized(line));

            StepAside();
            victimInput.Input = new MoveInput { Yaw = Ref.Angle(90f), Move = new Vector2(0f, run) };

            Vector3 start = Ref.ToUnity(victim.Position);
            float top = start.Y;
            bool off = false;
            int resting = 0, t = 0;
            for (; t < MaxFlight && resting < RestTicks; t++)
            {
                world.Simulation.Tick();
                top = MathF.Max(top, victim.Position.Y);
                off |= victim.Position.Y < start.Y - 1f;
                bool spent = victim.KnockLeft == 0f && (victim.IsGrounded || off);
                resting = spent ? resting + 1 : 0;
            }

            victimInput.Input.Move = Vector2.Zero;
            Vector3 end = Ref.ToUnity(victim.Position);
            float dx = end.X - start.X, dz = end.Z - start.Z;
            float distance = MathF.Sign(dx) * MathF.Sqrt(dx * dx + dz * dz);
            return new Flight
            {
                Distance = distance,
                Rise = top - start.Y,
                Angle = distance != 0f ? MathF.Atan2(dz, MathF.Abs(dx)) * 180f / MathF.PI : 0f,
                After = damage.Percent,
                Along = along,
                Ticks = t,
                Off = off,
            };
        }

        /// <summary>
        /// Gets the attacker moving before the swing: run 1 from 4.4 m behind
        /// the victim towards it until 2.6 m apart, -1 from 1.2 m backing off
        /// until 2.4 m. The ground acceleration has it at full speed by then.
        /// </summary>
        void RunUp(float run, bool sprint)
        {
            if (run == 0f)
                return;

            float gap = run > 0f ? 4.4f : 1.2f;
            attacker.Teleport(new Vector3(victim.Position.X - gap, attacker.Position.Y, attacker.Position.Z));
            attackerInput.Input = new MoveInput { Yaw = Ref.Angle(90f), Move = new Vector2(0f, run), Sprint = sprint };
            for (int i = 0; i < 100; i++)
            {
                float now = victim.Position.X - attacker.Position.X;
                if (run > 0f ? now <= 2.6f : now >= 2.4f)
                    break;
                world.Simulation.Tick();
            }
        }

        /// <summary>How far the victim runs on its own in ticks, from standing, signed along +x.</summary>
        float Walk(float run, int ticks)
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
        void StepAside()
        {
            attacker.Teleport(Ref.Pos(new Vector3(-22f, 1f, -14f)));
            attackerInput.Input = new MoveInput { Yaw = Ref.Angle(90f) };
        }

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
