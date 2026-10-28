using System;
using System.Buffers;
using System.Runtime.InteropServices;
using KotH.Probes.Harness;
using KotH.Shared.Combat;
using KotH.Shared.Hosting;
using KotH.Shared.Mathematics;
using KotH.Shared.Net;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Probes
{
    /// <summary>The mushroom blaster: a spore's path against the closed form, its lifetime,
    /// three shots that come back with points, the draw after a switch, a hit's percent
    /// and its flight at 1.5 times the cane's reach, and a rollback and a transfer with
    /// spores in the air.</summary>
    public sealed class BlasterProbe : Probe
    {
        protected override string Tag => "blaster";

        // Unity coordinates like KnockbackProbe's: flat ground along +x.
        static readonly Vector3 ShooterSpot = new Vector3(-22f, 1f, -18f);
        static readonly Vector3 VictimSpot = new Vector3(-16f, 1f, -18f);
        static readonly Vector3 AsideSpot = new Vector3(-22f, 1f, -10f);

        const int Settle = 60, MaxFlight = 600, RestTicks = 10;

        HeadlessWorld world;
        PlayerMotor shooter, victim;
        Blaster blaster;
        MeleeAttack cane;
        DamagePercent damage;
        ScriptedBody shooterInput, victimInput;
        int failures;

        public override int Run(HeadlessWorld world, int maxTicks)
        {
            this.world = world;
            Log("[blaster] entering play mode");
            Prototype scene = Prototype.Open(world);
            shooter = scene.Motor;
            blaster = scene.Player.Get<Blaster>();
            cane = scene.Player.Get<MeleeAttack>();
            shooterInput = new ScriptedBody();
            scene.Player.Add(shooterInput);

            victimInput = new ScriptedBody();
            damage = new DamagePercent();
            Entity body = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, Ref.Pos(VictimSpot),
                                          Quaternion.Identity, victimInput, damage);
            body.Name = "Victim";
            victim = body.Get<PlayerMotor>();

            if (blaster == null || cane == null)
            {
                LogError("[blaster] Blaster or MeleeAttack missing on the player");
                return 3;
            }
            Log($"[blaster] speed {blaster.Speed} m/s, gravity {blaster.Gravity}, {blaster.MaxShots} shots, " +
                $"one per {blaster.PointsPerShot} points, {blaster.KnockbackDistance} m / {blaster.KnockbackHeight} m at 100 %");

            Path();
            Lifetime();
            Ammo();
            Draw();
            foreach (float percent in new[] { 0f, 100f, 250f })
                HitFlight(percent);
            Rollback();
            Transfer();

            Log(failures == 0 ? "[blaster] ALL CASES PASSED" : $"[blaster] ERROR: {failures} cases failed");
            return failures == 0 ? 0 : 1;
        }

        void Check(string name, bool ok, string detail)
        {
            Log($"[blaster] {(ok ? "OK  " : "FAIL")} {name}: {detail}");
            if (!ok)
                failures++;
        }

        /// <summary>Upwards at 50°, away from everything: after n ticks the spore sits where
        /// the closed form of the semi-implicit step puts it.</summary>
        void Path()
        {
            Place(blasterInHand: true, aside: true);
            Vector3 eye = shooter.EyePosition;
            Fire(50f);
            BlasterShot s = Flying();
            float dt = FixedDeltaTime, g = blaster.Gravity;
            Vector3 v0 = new Vector3(s.Velocity.X, s.Velocity.Y + g * dt, s.Velocity.Z);
            Check("muzzle speed", MathF.Abs(UMath.Magnitude(v0) - blaster.Speed) < 1e-3f,
                  $"{UMath.Magnitude(v0):F4} m/s");

            float worst = 0f;
            for (int n = 1; n <= 20; n++)
            {
                if (n > 1)
                    world.Simulation.Tick();
                s = Flying();
                Vector3 want = UMath.Add(eye, UMath.Mul(v0, n * dt));
                want.Y -= g * dt * dt * n * (n + 1) / 2f;
                worst = MathF.Max(worst, UMath.Magnitude(UMath.Sub(s.Position, want)));
            }
            Check("path", s.Age == 20 && worst < 2e-3f, $"age {s.Age}, worst {worst * 1000f:F2} mm off the closed form");
        }

        /// <summary>Straight up the spore never lands: it is gone after its lifetime.</summary>
        void Lifetime()
        {
            Place(blasterInHand: true, aside: true);
            Fire(89f);
            int age = 0;
            for (int t = 1; t < blaster.LifetimeTicks; t++)
            {
                world.Simulation.Tick();
                age = Flying().Age;
            }
            world.Simulation.Tick();
            Check("lifetime", age == blaster.LifetimeTicks && Flying().Age == 0,
                  $"age {age} at the last tick, then {Flying().Age}");
        }

        /// <summary>Three shots, then none; one more per 30 points, none saved up while full.</summary>
        void Ammo()
        {
            Place(blasterInHand: true, aside: true);
            blaster.ResetCounters();
            shooterInput.Input.Pitch = 60f;
            shooterInput.Input.AttackPresses = 1;
            for (int t = 0; t < 4 * blaster.CooldownTicks; t++)
                world.Simulation.Tick();
            shooterInput.Input.AttackPresses = 0;
            Check("magazine", blaster.ShotCount == blaster.MaxShots && blaster.State.Shots == 0,
                  $"{blaster.ShotCount} shots fired out of {4 * blaster.CooldownTicks} ticks of pressing, {blaster.State.Shots} left");

            int per = blaster.PointsPerShot * Shared.Match.HillMatch.TicksPerPoint;
            Score(per - 1);
            int before = blaster.State.Shots;
            Score(per);
            int after = blaster.State.Shots;
            Score(per + 3 * per);
            int full = blaster.State.Shots;
            Score(per + 5 * per);
            Check("earned", before == 0 && after == 1 && full == blaster.MaxShots && blaster.State.Progress == 0,
                  $"{before} after {per - 1} score ticks, {after} after {per}, {full} when topped up, progress {blaster.State.Progress} while full");
        }

        // Sets the shooter's points as a round would have and steps once.
        void Score(int scoreTicks)
        {
            BodyState s = shooterInput.CaptureState();
            s.ScoreTicks = scoreTicks;
            shooterInput.RestoreState(s);
            world.Simulation.Tick();
        }

        /// <summary>After a switch nothing fires for the draw; with the blaster up the cane rests.</summary>
        void Draw()
        {
            Place(blasterInHand: false, aside: true);
            blaster.ResetCounters();
            int swings = cane.SwingCount;
            int first = -1;
            shooterInput.Input.Blaster = true;
            shooterInput.Input.Pitch = 60f;
            shooterInput.Input.AttackPresses = 1;
            for (int t = 0; t < 20 && first < 0; t++)
            {
                world.Simulation.Tick();
                if (blaster.ShotCount > 0)
                    first = t;
            }
            for (int t = 0; t < 20; t++)
                world.Simulation.Tick();
            shooterInput.Input.AttackPresses = 0;
            Check("draw", first == blaster.DrawTicks && cane.SwingCount == swings,
                  $"first shot {first} ticks after the switch, {cane.SwingCount - swings} cane swings meanwhile");
        }

        /// <summary>One spore at a standing victim charged to percent: 1.2 % more, and a flight
        /// as KnockbackProbe measures the cane's, at 18 m / 1.8 m per 100 %.</summary>
        void HitFlight(float percent)
        {
            Place(blasterInHand: true, aside: false);
            damage.Restore(percent);
            blaster.ResetCounters();
            float floor = victim.Position.Y;

            Vector3 to = UMath.Sub(victim.BodyCenter, shooter.EyePosition);
            float pitch = MathF.Atan2(to.Y, MathF.Sqrt(to.X * to.X + to.Z * to.Z)) * 180f / MathF.PI;
            Fire(pitch);
            for (int t = 0; t < 10 && blaster.HitCount == 0; t++)
                world.Simulation.Tick();
            shooter.Teleport(Ref.Pos(AsideSpot));

            Vector3 start = Ref.ToUnity(victim.Position);
            float top = start.Y;
            bool off = false;
            int resting = 0;
            for (int t = 0; t < MaxFlight && resting < RestTicks; t++)
            {
                world.Simulation.Tick();
                top = MathF.Max(top, victim.Position.Y);
                off |= victim.Position.Y < floor - 1f;
                resting = victim.KnockLeft == 0f && (victim.IsGrounded || off) ? resting + 1 : 0;
            }
            Vector3 end = Ref.ToUnity(victim.Position);
            float distance = MathF.Sqrt((end.X - start.X) * (end.X - start.X) + (end.Z - start.Z) * (end.Z - start.Z));
            float wantDistance = blaster.KnockbackDistance * percent / 100f;
            float wantRise = blaster.KnockbackHeight * MathF.Max(percent, blaster.KnockbackHeightFloor) / 100f;
            bool ok = blaster.HitCount == 1 && damage.Percent == percent + blaster.DamagePercent
                && MathF.Abs(top - start.Y - wantRise) <= MathF.Max(0.002f, wantRise * 0.02f)
                && (off || MathF.Abs(distance - wantDistance) <= MathF.Max(0.02f, wantDistance * 0.01f));
            Check($"hit at {percent} %", ok,
                  $"{blaster.HitCount} hit, {percent} -> {damage.Percent} %, flew {distance:F2} m (want {wantDistance:F2})" +
                  $"{(off ? " off the edge" : "")}, rose {top - start.Y:F3} m (want {wantRise:F3})");
        }

        /// <summary>Back to a tick with spores in the air and forward again: the same bytes.</summary>
        void Rollback()
        {
            Place(blasterInHand: true, aside: false);
            damage.Restore(50f);
            Vector3 to = UMath.Sub(victim.BodyCenter, shooter.EyePosition);
            float pitch = MathF.Atan2(to.Y, MathF.Sqrt(to.X * to.X + to.Z * to.Z)) * 180f / MathF.PI;
            shooterInput.Input.Pitch = pitch + 30f;
            shooterInput.Input.AttackPresses = 1;
            world.Simulation.Tick();
            shooterInput.Input.AttackPresses = 0;

            var snap = new WorldSnapshot();
            world.Simulation.Capture(snap);
            var inputs = new System.Collections.Generic.List<MoveInput[]>();
            for (int t = 0; t < 40; t++)
            {
                var row = new MoveInput[world.Simulation.Bodies.Count];
                for (int i = 0; i < row.Length; i++)
                {
                    SimulationBody b = world.Simulation.Bodies[i];
                    row[i] = b == shooterInput ? Aimed(t, pitch) : b.ReadInput();
                }
                inputs.Add(row);
            }

            world.Simulation.Deactivate();
            world.Simulation.Resimulate(snap, inputs, FixedDeltaTime);
            byte[] first = Bytes();
            world.Simulation.Resimulate(snap, inputs, FixedDeltaTime);
            byte[] second = Bytes();
            world.Simulation.Activate();
            Check("rollback", first.AsSpan().SequenceEqual(second) && blaster.State.Shots < blaster.MaxShots - 1,
                  $"{first.Length} bytes after 40 resimulated ticks with {blaster.MaxShots - blaster.State.Shots} shots fired");
        }

        // Fires at the victim again on tick 12, holding the blaster the whole time.
        static MoveInput Aimed(int t, float pitch)
            => new MoveInput { Yaw = Ref.Angle(90f), Pitch = pitch, Blaster = true, AttackPresses = (byte)(t == 12 ? 1 : 0) };

        /// <summary>A transfer with a spore in the air reads back to the same states.</summary>
        void Transfer()
        {
            Place(blasterInHand: true, aside: true);
            Fire(40f);
            // Offline every body is NetId 0; a transfer finds them by it.
            for (int i = 0; i < world.Simulation.Bodies.Count; i++)
                world.Simulation.Bodies[i].NetId = 4 + i;
            var snap = new WorldSnapshot();
            world.Simulation.Capture(snap);
            var buffer = new ArrayBufferWriter<byte>();
            StateCodec.Write(snap, buffer);

            var back = new WorldSnapshot();
            bool read = StateCodec.Read(buffer.WrittenSpan, id => FindNet(id), back);
            bool same = read && back.Count == snap.Count;
            for (int i = 0; same && i < snap.Count; i++)
            {
                BodyState a = snap.StateAt(i), b = back.StateAt(i);
                same = MemoryMarshal.AsBytes(new ReadOnlySpan<BodyState>(in a))
                    .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<BodyState>(in b)));
            }
            Check("transfer", same && Flying().Age > 0,
                  $"{buffer.WrittenCount} bytes, body state {StateCodec.BodyStateSize} bytes, read back {(same ? "equal" : "different")}");
        }

        SimulationBody FindNet(int id)
        {
            foreach (SimulationBody b in world.Simulation.Bodies)
                if (b.NetId == id)
                    return b;
            return null;
        }

        byte[] Bytes()
        {
            var all = new System.Collections.Generic.List<byte>();
            foreach (SimulationBody b in world.Simulation.Bodies)
            {
                BodyState s = b.CaptureState();
                all.AddRange(MemoryMarshal.AsBytes(new ReadOnlySpan<BodyState>(in s)).ToArray());
            }
            return all.ToArray();
        }

        // The spore in flight: the youngest busy slot.
        BlasterShot Flying()
        {
            BlasterState s = blaster.State;
            BlasterShot best = default;
            for (int i = 0; i < BlasterState.Slots; i++)
            {
                BlasterShot shot = Blaster.Slot(ref s, i);
                if (shot.Age > 0 && (best.Age == 0 || shot.Age < best.Age))
                    best = shot;
            }
            return best;
        }

        // One press at this pitch (Stride: positive up), looking along +x.
        void Fire(float pitch)
        {
            shooterInput.Input.Pitch = pitch;
            shooterInput.Input.AttackPresses = 1;
            world.Simulation.Tick();
            shooterInput.Input.AttackPresses = 0;
        }

        /// <summary>Both bodies to their spots, settled, the shooter's magazine full and its
        /// blaster drawn or the cane in hand; aside puts the victim out of the line.</summary>
        void Place(bool blasterInHand, bool aside)
        {
            shooter.Teleport(Ref.Pos(ShooterSpot));
            victim.Teleport(Ref.Pos(aside ? new Vector3(-16f, 1f, -26f) : VictimSpot));
            shooterInput.Input = new MoveInput { Yaw = Ref.Angle(90f), Blaster = blasterInHand };
            victimInput.Input = new MoveInput { Yaw = Ref.Angle(90f) };
            for (int i = 0; i < Settle; i++)
                world.Simulation.Tick();

            BodyState s = shooterInput.CaptureState();
            s.Blaster.Shots = blaster.MaxShots;
            s.Blaster.Progress = 0;
            s.Blaster.Shot0 = s.Blaster.Shot1 = s.Blaster.Shot2 = default;
            shooterInput.RestoreState(s);
        }
    }
}
