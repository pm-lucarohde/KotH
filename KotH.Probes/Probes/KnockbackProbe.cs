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
    /// The hit's knockback against the percent before it: 0 % only hops as
    /// high as 1 %, 100 % sends a body that does not steer 12 m along flat ground and 1.2 m
    /// up, linear to 1000 %. Then 100 % with the victim running against and
    /// along the knock: its own running simply adds on. Last the speeds of
    /// both along the line at the hit: a share of them from 10 %, all from
    /// 20 %; head-on at equal speed they cancel. Upward the victim's own
    /// climb or fall in the air adds to the hop.
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

        /// <summary>
        /// One knock as measured. Along is the attacker's speed towards the
        /// victim, Fleeing the victim's own away from it, both at the hit.
        /// Up is its upward speed right after the hit, Climb the one it had
        /// over the swing tick before the hit came in.
        /// </summary>
        struct Flight
        {
            public float Distance, Rise, Angle, After, Along, Fleeing, Up, Climb;
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
                $"Tempo zählt ab {attack.CarryFromPercent:F0} % mit Anteil {attack.CarryStartShare:F2}, " +
                $"voll ab {attack.CarryFullPercent:F0} %");

            int failures = 0;
            float shortFlight = 0f;
            foreach (float percent in Percents)
            {
                Flight f = Hit(percent, 0f);

                float wantDistance = attack.KnockbackDistance * percent / 100f;
                float wantRise = attack.KnockbackHeight * MathF.Max(percent, attack.KnockbackHeightFloor) / 100f;
                bool ok = MathF.Abs(f.Rise - wantRise) <= MathF.Max(0.002f, wantRise * 0.02f)
                    && (percent == 0f ? f.Distance == 0f
                        : MathF.Abs(f.Distance - wantDistance) <= MathF.Max(0.02f, wantDistance * 0.01f)
                          && MathF.Abs(f.Angle) < 1f);
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

            // Both speeds along the line at the hit: their share is added to
            // the launch speed, the distance then follows from braking at the
            // air acceleration. The victim lets go once hit, so nothing of its
            // running is left; holding on it runs on against the knock.
            var carried = new (float percent, float attacker, float victim, bool sprint, float holds, string what)[]
            {
                (0f, 1f, 0f, true, 0f, "Schläger sprintet heran"),
                (5f, 1f, 0f, false, 0f, "Schläger geht heran"),
                (10f, 1f, 0f, false, 0f, "Schläger geht heran"),
                (15f, 1f, 0f, false, 0f, "Schläger geht heran"),
                (20f, 1f, 0f, false, 0f, "Schläger geht heran"),
                (100f, 1f, 0f, false, 0f, "Schläger geht heran"),
                (100f, 1f, 0f, true, 0f, "Schläger sprintet heran"),
                (100f, -1f, 0f, false, 0f, "Schläger geht rückwärts weg"),
                (10f, -1f, 0f, false, 0f, "Schläger geht rückwärts weg"),
                (100f, 0f, -1f, false, 0f, "Getroffener geht heran"),
                (100f, 0f, 1f, false, 0f, "Getroffener geht weg"),
                (100f, 1f, -1f, false, 0f, "beide gehen aufeinander zu"),
                (20f, 1f, -1f, false, 0f, "beide gehen aufeinander zu"),
                (15f, 1f, -1f, false, 0f, "beide gehen aufeinander zu"),
                (100f, 1f, -1f, false, -1f, "beide gehen aufeinander zu, Getroffener hält weiter"),
            };
            foreach (var c in carried)
            {
                Flight f = Hit(c.percent, c.holds, c.attacker, c.sprint, c.victim);
                float share = attack.CarryShare(c.percent);
                float want = Travel(c.percent, share * (f.Along + f.Fleeing));
                // Below the full share the rest of the victim's running stays
                // its own and carries it on a little after the hit; holding on
                // is not worked out either. Both only have to land short, the
                // rest by less than its speed over half a second.
                float rest = (1f - share) * MathF.Abs(f.Fleeing) * 0.5f;
                bool exact = c.holds == 0f && (share >= 1f || f.Fleeing == 0f);
                bool ok = c.percent == 0f ? f.Distance == 0f
                    : c.holds != 0f ? f.Distance < want && !f.Off
                    : !exact ? f.Distance < want && f.Distance > want - rest
                    : MathF.Abs(f.Distance - want) <= MathF.Max(0.02f, want * 0.01f) && MathF.Abs(f.Angle) < 1f;
                if (!ok)
                    failures++;
                Log($"[knockback] == {c.percent,5:F1} %, {c.what} (Schläger {f.Along:F3} m/s, Getroffener {f.Fleeing:F3} m/s), " +
                    $"Anteil {share:F3}: Weite {f.Distance:F3} m ({(exact ? "soll" : "unter")} {want:F3}, " +
                    $"im Stand {Travel(c.percent, 0f):F3})" +
                    (f.Off ? ", über den Rand der Arena (in der Luft gemessen)" : "") + " -> " +
                    (ok ? "ok" : "ABWEICHUNG"));
            }

            // The victim jumps and is hit rising, or falling back: its own
            // upward speed stays and the hop adds to it, so it peaks where that
            // sum carries it. The short jump lets go after the hit, which must
            // no longer trim the rise. Sideways nothing changes.
            float hop = PlayerMotor.LaunchSpeed(attack.KnockbackHeight, -victim.Gravity, FixedDeltaTime);
            var jumps = new (int ticks, int hold, string what)[]
            {
                (4, 99, "Getroffener springt und steigt noch"),
                (2, 3, "Getroffener springt kurz und lässt nach dem Treffer los"),
                (20, 99, "Getroffener fällt nach dem Sprung"),
            };
            foreach (var j in jumps)
            {
                Flight f = Hit(100f, 0f, jumpTicks: j.ticks, holdTicks: j.hold);
                float g = -victim.Gravity;
                float want = f.Up > 0f ? f.Up * (f.Up - g * FixedDeltaTime) / (2f * g) : 0f;
                float own = f.Up - hop;
                bool hit = MathF.Abs(f.After - 100f - attack.DamagePercent) < 1e-3f;
                bool ok = hit && MathF.Abs(own - f.Climb) < 1e-3f
                    && MathF.Abs(f.Rise - want) <= MathF.Max(0.01f, want * 0.02f)
                    && MathF.Abs(f.Distance - shortFlight) <= 0.02f && !f.Off;
                if (!ok)
                    failures++;
                Log($"[knockback] == 100,0 %, {j.what}: eigenes Tempo nach oben {f.Climb:F3} m/s (übernommen {own:F3}), " +
                    $"Höhe {f.Rise:F3} m (soll {want:F3}, im Stand {attack.KnockbackHeight:F3}), Weite {f.Distance:F3} m" +
                    (hit ? "" : ", NICHT GETROFFEN") + " -> " + (ok ? "ok" : "ABWEICHUNG"));
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
        /// hit on (-1 against, 1 along); attackerRun and victimRun are their
        /// inputs along +x up to and through the swing. Distance and angle
        /// are horizontal, signed along +x, from where the victim stood when
        /// hit; rise is the peak above that. With jumpTicks the victim takes
        /// off that many ticks before the swing and holds the key holdTicks.
        /// </summary>
        Flight Hit(float percent, float run, float attackerRun = 0f, bool sprint = false, float victimRun = 0f,
                   int jumpTicks = -1, int holdTicks = 0)
        {
            Place();
            float floor = victim.Position.Y;
            damage.Restore(percent);
            attack.ResetCounters();
            RunUp(attackerRun, victimRun, sprint);

            int jumped = 0;
            if (jumpTicks >= 0)
            {
                victimInput.Input.JumpPresses = 1;
                for (; jumped < jumpTicks; jumped++)
                {
                    victimInput.Input.JumpHeld = jumped < holdTicks;
                    world.Simulation.Tick();
                    victimInput.Input.JumpPresses = 0;
                }
                victimInput.Input.JumpHeld = jumped < holdTicks;
            }
            float climb = victim.Velocity.Y + victim.Gravity * FixedDeltaTime;

            // The victim's speed before the swing tick, which is the one the
            // swing sees once both run at full speed: after it the knock is in.
            Vector3 line = UMath.Normalized(new Vector3(victim.Position.X - attacker.Position.X, 0f,
                                                        victim.Position.Z - attacker.Position.Z));
            float fleeing = UMath.Dot(new Vector3(victim.Velocity.X, 0f, victim.Velocity.Z), line);

            attackerInput.Input.AttackPresses = 1;
            world.Simulation.Tick();
            attackerInput.Input.AttackPresses = 0;
            float up = victim.Velocity.Y;
            jumped++;

            // The same speed and line the swing saw: combat is the last phase.
            line = UMath.Sub(victim.Position, attacker.Position);
            line.Y = 0f;
            Vector3 speed = new Vector3(attacker.Velocity.X, 0f, attacker.Velocity.Z);
            float along = UMath.Dot(speed, UMath.Normalized(line));

            StepAside();
            victimInput.Input = new MoveInput { Yaw = Ref.Angle(90f), Move = new Vector2(0f, run) };

            Vector3 start = Ref.ToUnity(victim.Position);
            float top = start.Y;
            bool off = false;
            int resting = 0, t = 0;
            for (; t < MaxFlight && resting < RestTicks; t++, jumped++)
            {
                victimInput.Input.JumpHeld = jumpTicks >= 0 && jumped < holdTicks;
                world.Simulation.Tick();
                top = MathF.Max(top, victim.Position.Y);
                off |= victim.Position.Y < floor - 1f;
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
                Fleeing = fleeing,
                Up = up,
                Climb = climb,
                Ticks = t,
                Off = off,
            };
        }

        /// <summary>
        /// Gets the bodies moving before the swing, both facing +x: closing in
        /// from 4.9 m until 2.6 m apart, drawing apart from 1.2 m until 2.4 m.
        /// The ground acceleration has them at full speed by then. The victim
        /// starts 2 m further on, so the attacker stays on the ground.
        /// </summary>
        void RunUp(float attackerRun, float victimRun, bool sprint)
        {
            if (attackerRun == 0f && victimRun == 0f)
                return;

            bool closing = attackerRun > 0f || victimRun < 0f;
            float gap = closing ? 4.9f : 1.2f;
            victim.Teleport(new Vector3(victim.Position.X + 2f, victim.Position.Y, victim.Position.Z));
            attacker.Teleport(new Vector3(victim.Position.X - gap, attacker.Position.Y, attacker.Position.Z));
            attackerInput.Input = new MoveInput { Yaw = Ref.Angle(90f), Move = new Vector2(0f, attackerRun), Sprint = sprint };
            victimInput.Input = new MoveInput { Yaw = Ref.Angle(90f), Move = new Vector2(0f, victimRun) };
            for (int i = 0; i < 100; i++)
            {
                float now = victim.Position.X - attacker.Position.X;
                if (closing ? now <= 2.6f : now >= 2.4f)
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
