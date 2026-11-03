using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KotH.Probes.Harness;
using KotH.Shared.Combat;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Mathematics;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Probes
{
    /// <summary>The dash down: a crouch press from 1.5 m up falls straight, lower the press
    /// slides as before, one charge back per 10 points, hits within 3.5 m and not through
    /// rock, flights at 24 m / 2.4 m per 100 %, a knock ends the fall, a rollback in it.</summary>
    public sealed class DashProbe : Probe
    {
        protected override string Tag => "dash";

        // Unity coordinates like BlasterProbe's: flat ground along +x.
        static readonly Vector3 DasherSpot = new Vector3(-22f, 1f, -18f);
        static readonly Vector3 AwaySpot = new Vector3(-16f, 1f, -30f);
        // WallKnockProbe's, in Stride metres: in front of the Plateau's face at z -10.
        static readonly Vector3 WallSpot = new Vector3(0f, 0.9f, -7f);

        const int Settle = 60, MaxFlight = 600, RestTicks = 10;

        HeadlessWorld world;
        PlayerMotor dasher, victim;
        Dash dash;
        DamagePercent damage;
        ScriptedBody dasherInput, victimInput;
        float floorY;
        int failures;

        public override int Run(HeadlessWorld world, int maxTicks)
        {
            this.world = world;
            Log("[dash] entering play mode");
            Prototype scene = Prototype.Open(world);
            dasher = scene.Motor;
            dash = scene.Player.Get<Dash>();
            dasherInput = new ScriptedBody();
            scene.Player.Add(dasherInput);

            victimInput = new ScriptedBody();
            damage = new DamagePercent();
            Entity body = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, Ref.Pos(AwaySpot), Quaternion.Identity,
                                          victimInput, damage);
            body.Name = "Victim";
            victim = body.Get<PlayerMotor>();

            if (dash == null)
            {
                LogError("[dash] Dash missing on the player");
                return 3;
            }
            Log($"[dash] {dash.SlamSpeed} m/s from {dash.MinHeight} m, radius {dash.Radius} m, {dash.MaxDashes} dash, " +
                $"one per {dash.PointsPerDash} points, {dash.KnockbackDistance} m / {dash.KnockbackHeight} m at 100 %");

            FromSingleJump();
            FromJump();
            TooLow();
            NoCharge();
            Earn();
            Reach(dash.Radius - 0.1f, true);
            Reach(dash.Radius + 0.1f, false);
            foreach (float percent in new[] { 0f, 50f })
                HitFlight(percent);
            ThroughRock();
            KnockEnds();
            Rollback();

            Log(failures == 0 ? "[dash] ALL CASES PASSED" : $"[dash] ERROR: {failures} cases failed");
            return failures == 0 ? 0 : 1;
        }

        void Check(string name, bool ok, string detail)
        {
            Log($"[dash] {(ok ? "OK  " : "FAIL")} {name}: {detail}");
            if (!ok)
                failures++;
        }

        /// <summary>A full jump with the press at its peak: a dash exactly when the feet get
        /// MinHeight up: on flat ground only right at the 1.5 m peak, off a ledge easily (Luca).</summary>
        void FromSingleJump()
        {
            Place(DasherSpot, charged: true);
            dash.ResetCounters();
            Tap(new MoveInput { Yaw = Ref.Angle(90f), JumpPresses = 1, JumpHeld = true });
            float peak = 0f;
            bool pressed = false;
            for (int t = 0; t < 60 && !pressed; t++)
            {
                float up = dasher.Position.Y - floorY;
                peak = MathF.Max(peak, up);
                pressed = dasher.Velocity.Y < 0f;
                Tap(new MoveInput { Yaw = Ref.Angle(90f), JumpHeld = true, CrouchPressed = pressed });
            }
            world.Simulation.Tick();
            bool reaches = peak >= dash.MinHeight;
            Check("from a jump", pressed && (dash.SlamCount == 1) == reaches,
                  $"peak {peak:F3} m against {dash.MinHeight} m: {dash.SlamCount} dashes ({(reaches ? "reaches" : "too low")})");
            for (int t = 0; t < 40; t++)
                world.Simulation.Tick();
        }

        /// <summary>A sprint jump and a double jump (a jump alone peaks at 1.5 m), the press
        /// once the feet are 1.55 m up: from the next tick straight down at the dash's speed,
        /// landing exactly below where the press tick left it.</summary>
        void FromJump()
        {
            Place(DasherSpot, charged: true);
            dash.ResetCounters();
            dasherInput.Input = new MoveInput { Yaw = Ref.Angle(90f), Move = new Vector2(0f, 1f), Sprint = true };
            for (int t = 0; t < 40; t++)
                world.Simulation.Tick();
            Tap(new MoveInput { Yaw = Ref.Angle(90f), Move = new Vector2(0f, 1f), Sprint = true, JumpPresses = 1, JumpHeld = true });

            float pressedAt = -1f, highest = 0f;
            Vector3 below = default;
            for (int t = 0; t < 40 && pressedAt < 0f; t++)
            {
                highest = MathF.Max(highest, dasher.Position.Y - floorY);
                if (t == 12)
                    Tap(new MoveInput { Yaw = Ref.Angle(90f), Move = new Vector2(0f, 1f), Sprint = true, JumpPresses = 2, JumpHeld = true });
                else if (dasher.Position.Y - floorY >= dash.MinHeight + 0.05f)
                {
                    pressedAt = dasher.Position.Y - floorY;
                    Tap(new MoveInput { Yaw = Ref.Angle(90f), Move = new Vector2(0f, 1f), CrouchPressed = true, JumpHeld = true });
                    below = dasher.Position;
                }
                else
                    Tap(new MoveInput { Yaw = Ref.Angle(90f), Move = new Vector2(0f, 1f), Sprint = true, JumpHeld = true });
            }

            world.Simulation.Tick();
            Vector3 v = dasher.Velocity;
            bool falling = dasher.IsSlamming && v.X == 0f && v.Z == 0f && MathF.Abs(v.Y + dash.SlamSpeed) < 1e-4f;
            int ticks = 1;
            for (; ticks < 40 && !dasher.SlamLanded; ticks++)
                world.Simulation.Tick();
            float off = MathF.Sqrt((dasher.Position.X - below.X) * (dasher.Position.X - below.X) +
                                   (dasher.Position.Z - below.Z) * (dasher.Position.Z - below.Z));
            Check("from a double jump", pressedAt > 0f && falling && dasher.SlamLanded && off < 1e-4f &&
                  dash.SlamCount == 1 && dash.State.Charges == 0,
                  $"pressed {pressedAt:F2} m up (highest {highest:F2}), then {v.Y:F2} m/s down ({(falling ? "straight" : "not straight")}), " +
                  $"landed after {ticks} ticks {off * 1000f:F2} mm off the spot below, {dash.State.Charges} left");
        }

        /// <summary>The press below 1.5 m, crouch held, out of a sprint jump: no dash, the
        /// landing slides as it always did.</summary>
        void TooLow()
        {
            Place(DasherSpot, charged: true);
            dash.ResetCounters();
            dasherInput.Input = new MoveInput { Yaw = Ref.Angle(90f), Move = new Vector2(0f, 1f), Sprint = true };
            for (int t = 0; t < 40; t++)
                world.Simulation.Tick();
            Tap(new MoveInput { Yaw = Ref.Angle(90f), Move = new Vector2(0f, 1f), Sprint = true, JumpPresses = 1, JumpHeld = true });

            bool pressed = false, slid = false, wasHigh = false;
            for (int t = 0; t < 60 && !slid; t++)
            {
                float up = dasher.Position.Y - floorY;
                wasHigh |= up > 1f;
                bool now = !pressed && wasHigh && up < 0.6f && dasher.Velocity.Y < 0f;
                pressed |= now;
                Tap(new MoveInput { Yaw = Ref.Angle(90f), Move = new Vector2(0f, 1f), Sprint = true, Crouch = pressed,
                                    CrouchPressed = now, JumpHeld = true });
                slid |= dasher.IsSliding;
            }
            Check("too low", pressed && slid && dash.SlamCount == 0 && dash.State.Charges == 1,
                  $"pressed below 0.6 m: {(slid ? "slid" : "no slide")}, {dash.SlamCount} dashes, {dash.State.Charges} left");
        }

        /// <summary>Spent, the press high up does nothing.</summary>
        void NoCharge()
        {
            Place(DasherSpot, charged: false);
            dash.ResetCounters();
            Drop(3f);
            Check("no charge", dash.SlamCount == 0 && !dasher.IsSlamming, $"{dash.SlamCount} dashes without a charge");
        }

        /// <summary>One back after 10 points, never more than one.</summary>
        void Earn()
        {
            Place(DasherSpot, charged: false);
            int per = dash.PointsPerDash * HillMatch.TicksPerPoint;
            Score(per - 1);
            int before = dash.State.Charges;
            Score(per);
            int after = dash.State.Charges;
            Score(per + 5 * per);
            Check("earned", before == 0 && after == 1 && dash.State.Charges == dash.MaxDashes && dash.State.Progress == 0,
                  $"{before} after {per - 1} score ticks, {after} after {per}, {dash.State.Charges} after six times as many, " +
                  $"progress {dash.State.Progress} while full");
        }

        // Sets the dasher's points as a round would have and steps once.
        void Score(int scoreTicks)
        {
            BodyState s = dasherInput.CaptureState();
            s.ScoreTicks = scoreTicks;
            dasherInput.RestoreState(s);
            world.Simulation.Tick();
        }

        /// <summary>A body whose nearest point lies this far from the landing: hit or not.</summary>
        void Reach(float gap, bool hit)
        {
            float x = Landing(gap);
            damage.Restore(0f);
            dash.ResetCounters();
            Drop(3f);
            Check($"{gap:F1} m away", (dash.HitCount == 1) == hit,
                  $"{dash.HitCount} hit at {gap:F2} m from the landing to the body (centre {x:F2} m)");
        }

        // The dasher above its spot, the victim off along +x so its nearest point is gap away; returns the centres' distance.
        float Landing(float gap)
        {
            float x = gap + victim.Radius;
            Place(DasherSpot, charged: true);
            victim.Teleport(Ref.Pos(DasherSpot.X + x, DasherSpot.Y, DasherSpot.Z));
            for (int i = 0; i < Settle; i++)
                world.Simulation.Tick();
            return x;
        }

        /// <summary>A victim at percent 2 m away: 1.2 % more, and a flight away from the landing
        /// as KnockbackProbe measures the cane's, at 24 m / 2.4 m per 100 %.</summary>
        void HitFlight(float percent)
        {
            Landing(2f);
            damage.Restore(percent);
            dash.ResetCounters();
            float floor = victim.Position.Y;
            Vector3 start = default;
            bool struck = false;
            Press(3f);
            for (int t = 0; t < 40 && !struck; t++)
            {
                start = Ref.ToUnity(victim.Position);
                world.Simulation.Tick();
                struck = dash.HitCount > 0;
            }
            dasher.Teleport(Ref.Pos(AwaySpot));

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
            float wantDistance = dash.KnockbackDistance * percent / 100f;
            float wantRise = dash.KnockbackHeight * MathF.Max(percent, dash.KnockbackHeightFloor) / 100f;
            bool ok = dash.HitCount == 1 && damage.Percent == percent + dash.DamagePercent && (percent == 0f || end.X > start.X)
                && MathF.Abs(top - start.Y - wantRise) <= MathF.Max(0.003f, wantRise * 0.02f)
                && (off || MathF.Abs(distance - wantDistance) <= MathF.Max(0.02f, wantDistance * 0.01f));
            // 3 mm at the floor's 2.4 cm hop: the start is read a tick before the knock, settling.
            Check($"hit at {percent} %", ok,
                  $"{dash.HitCount} hit, {percent} -> {damage.Percent} %, flew {distance:F2} m (want {wantDistance:F2})" +
                  $"{(off ? " off the edge" : "")}, rose {top - start.Y:F3} m (want {wantRise:F3})");
        }

        /// <summary>Onto the Plateau right above a body standing at its wall: within the radius,
        /// but the rock lies between, so no hit.</summary>
        void ThroughRock()
        {
            Place(DasherSpot, charged: true);
            victim.Teleport(WallSpot);
            for (int i = 0; i < Settle; i++)
                world.Simulation.Tick();

            // The wall's face and the Plateau's top behind it, as the world has them.
            Vector3 into = new Vector3(0f, 0f, -1f);
            bool face = world.Simulation.World.Raycast(victim.BodyCenter, into, 15f, WorldQueries.WorldMask, out QueryHit wall);
            if (!face || world.Simulation.World.Raycast(victim.BodyCenter, -into, 15f, WorldQueries.WorldMask, out QueryHit back) &&
                back.Distance < wall.Distance)
            {
                into = -into;
                face = world.Simulation.World.Raycast(victim.BodyCenter, into, 15f, WorldQueries.WorldMask, out wall);
            }
            // The victim right at the face.
            if (face)
            {
                Vector3 foot = UMath.Sub(wall.Point, UMath.Mul(into, victim.Radius + 0.1f));
                victim.Teleport(new Vector3(foot.X, victim.Position.Y, foot.Z));
                for (int i = 0; i < Settle; i++)
                    world.Simulation.Tick();
            }
            Vector3 above = UMath.Add(wall.Point, UMath.Mul(into, 0.8f));
            above.Y += 20f;
            bool top = world.Simulation.World.Raycast(above, UMath.Down, 40f, WorldQueries.WorldMask, out QueryHit ground);
            if (!face || !top)
            {
                Check("through rock", false, $"no wall within 15 m or no Plateau behind it ({face}, {top})");
                return;
            }

            dasher.Teleport(new Vector3(ground.Point.X, ground.Point.Y + dasher.StandHeight * 0.5f + 3f, ground.Point.Z));
            damage.Restore(0f);
            dash.ResetCounters();
            Press(0f);
            for (int t = 0; t < 40 && !dasher.SlamLanded; t++)
                world.Simulation.Tick();

            Vector3 at = new Vector3(dasher.Position.X, dasher.Position.Y - dasher.StandHeight * 0.5f, dasher.Position.Z);
            float half = victim.Height * 0.5f - victim.Radius;
            float y = UMath.Clamp(at.Y, victim.BodyCenter.Y - half, victim.BodyCenter.Y + half);
            float gap = UMath.Magnitude(UMath.Sub(new Vector3(victim.BodyCenter.X, y, victim.BodyCenter.Z), at)) - victim.Radius;
            Check("through rock", dash.SlamCount == 1 && gap <= dash.Radius && dash.HitCount == 0,
                  $"landed {gap:F2} m from the body below the edge ({wall.Distance:F1} m to the wall), {dash.HitCount} hits");
            dasher.Teleport(Ref.Pos(DasherSpot));
        }

        /// <summary>A hit in the fall ends it: the knock flies, the dash is spent.</summary>
        void KnockEnds()
        {
            Place(DasherSpot, charged: true);
            dash.ResetCounters();
            Press(4f);
            world.Simulation.Tick();
            bool was = dasher.IsSlamming;
            dasher.Knock(Ref.Pos(1f, 0f, 0f), 6f, 1f, 0f, 0f, FixedDeltaTime);
            world.Simulation.Tick();
            Check("a knock ends it", was && !dasher.IsSlamming && dasher.Velocity.Y > -dash.SlamSpeed * 0.5f && dash.State.Charges == 0,
                  $"falling {was}, after the knock {(dasher.IsSlamming ? "still" : "no longer")}, {dasher.Velocity.Y:F2} m/s, {dash.State.Charges} left");
        }

        /// <summary>Back to a tick in the fall and forward through the landing and its hit: the same bytes.</summary>
        void Rollback()
        {
            Landing(1.5f);
            damage.Restore(30f);
            Press(4f);
            world.Simulation.Tick();

            var snap = new WorldSnapshot();
            world.Simulation.Capture(snap);
            var inputs = new List<MoveInput[]>();
            for (int t = 0; t < 40; t++)
            {
                var row = new MoveInput[world.Simulation.Bodies.Count];
                for (int i = 0; i < row.Length; i++)
                    row[i] = world.Simulation.Bodies[i] == dasherInput ? new MoveInput { Yaw = Ref.Angle(90f) }
                                                                       : world.Simulation.Bodies[i].ReadInput();
                inputs.Add(row);
            }

            world.Simulation.Deactivate();
            world.Simulation.Resimulate(snap, inputs, FixedDeltaTime);
            byte[] first = Bytes();
            float hitPercent = damage.Percent;
            world.Simulation.Resimulate(snap, inputs, FixedDeltaTime);
            byte[] second = Bytes();
            world.Simulation.Activate();
            Check("rollback", first.AsSpan().SequenceEqual(second) && hitPercent > 30f,
                  $"{first.Length} bytes after 40 resimulated ticks through the landing, victim at {hitPercent:F1} %");
        }

        byte[] Bytes()
        {
            var all = new List<byte>();
            foreach (SimulationBody b in world.Simulation.Bodies)
            {
                BodyState s = b.CaptureState();
                all.AddRange(MemoryMarshal.AsBytes(new ReadOnlySpan<BodyState>(in s)).ToArray());
            }
            return all.ToArray();
        }

        // One tick with this input, then the input rests.
        void Tap(MoveInput input)
        {
            dasherInput.Input = input;
            world.Simulation.Tick();
            dasherInput.Input = new MoveInput { Yaw = input.Yaw, JumpHeld = input.JumpHeld };
        }

        // The dasher lifted by height (0: where it is), a tick to notice it is off the
        // ground, then the press.
        void Press(float height)
        {
            if (height > 0f)
                dasher.Teleport(UMath.Add(dasher.Position, new Vector3(0f, height, 0f)));
            world.Simulation.Tick();
            Tap(new MoveInput { Yaw = Ref.Angle(90f), CrouchPressed = true });
        }

        // Pressed from height, then on until it rests again.
        void Drop(float height)
        {
            Press(height);
            for (int t = 0; t < 60; t++)
                world.Simulation.Tick();
        }

        /// <summary>The dasher at spot, settled, with its one dash or none; the victim
        /// out of the way unless a case places it.</summary>
        void Place(Vector3 spot, bool charged)
        {
            dasher.Teleport(Ref.Pos(spot));
            victim.Teleport(Ref.Pos(AwaySpot));
            dasherInput.Input = new MoveInput { Yaw = Ref.Angle(90f) };
            victimInput.Input = new MoveInput { Yaw = Ref.Angle(90f) };
            for (int i = 0; i < Settle; i++)
                world.Simulation.Tick();
            floorY = dasher.Position.Y;

            BodyState s = dasherInput.CaptureState();
            s.Dash.Charges = charged ? dash.MaxDashes : 0;
            s.Dash.Progress = 0;
            dasherInput.RestoreState(s);
        }
    }
}
