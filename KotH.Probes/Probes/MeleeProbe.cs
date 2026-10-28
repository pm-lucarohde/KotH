using KotH.Probes.Harness;
using KotH.Shared.Combat;
using KotH.Shared.Dummy;
using KotH.Shared.Mathematics;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.BepuPhysics;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Probes
{
    /// <summary>Measures the swing: the shape it can actually hit, how far it reaches,
    /// how often it lands and where the cap holds. Runs THROUGH SimulationSystem like
    /// ImpactProbe, because the combat pass lives there. Throwaway probe. The font
    /// measurement of the client's DamageLabel is left out: no client yet.</summary>
    public sealed class MeleeProbe : Probe
    {
        protected override string Tag => "melee";

        static readonly Vector3 PlayerSpot = new Vector3(0f, 0.9f, -18f);
        const float Yaw = 90f;          // looking along +x

        const float DistanceStep = 0.02f;

        // Derived from the configured reach, so turning the dial up does not
        // silently leave the sweep ending before the limit it is looking for.
        float firstDistance;
        float lastDistance;

        const int SettleTicks = 3;      // after a teleport, before a swing
        const int ReadTicks = 2;        // after a swing, before reading the count
        const int BurstTicks = 131;     // ticks 0..130, so swings at 0,13,...,130

        enum Phase { Hull, Reach, Burst, Sideways, Done }

        PlayerMotor player;
        PlayerMotor dummy;
        StaticComponent dummyHull;   // the shape a ray can actually meet
        TrainingDummy dummyBody;
        DamagePercent damage;
        MeleeAttack attack;
        ScriptedBody scripted;

        Phase phase = Phase.Hull;
        int tick;
        float distance;
        float lastHitCentre = -1f;
        float lastHitRay = -1f;

        protected override void Start()
        {
            Prototype scene = Prototype.Open(World);
            player = scene.Motor;

            if (World.Simulation == null)
            {
                LogError("[melee] no SimulationSystem in the scene");
                Exit(3);
                return;
            }

            TrainingDummy target = scene.Dummy?.Get<TrainingDummy>();
            if (target == null)
            {
                LogError("[melee] no TrainingDummy in the scene");
                Exit(3);
                return;
            }

            dummyBody = target;
            dummy = target.Motor;
            dummyHull = dummy.Hull;
            damage = target.Entity.Get<DamagePercent>();
            attack = scene.Player.Get<MeleeAttack>();

            // Reach and hit rate against Unity, which had no knockback: a
            // dummy pushed away by the burst would leave the range.
            if (attack != null)
            {
                attack.KnockbackDistance = 0f;
                attack.KnockbackHeight = 0f;
            }

            if (damage == null || attack == null)
            {
                LogError("[melee] DamagePercent on the dummy or MeleeAttack on the player is missing");
                Exit(3);
                return;
            }

            firstDistance = attack.Range - 0.4f;
            lastDistance = attack.Range + 0.8f;

            CapTest();

            // Joining the entity puts the body into the tick, as OnEnable did.
            scripted = new ScriptedBody();
            scene.Player.Add(scripted);
            scripted.BeforeTick = Step;

            // The dummy registered during scene load and would otherwise tick
            // before the player, making every reading a tick old. Unity's
            // enabled = false / true: out of the list and back in at its end.
            World.Simulation.Unregister(dummyBody);
            World.Simulation.Register(dummyBody);

            Place(player, PlayerSpot);
            distance = firstDistance;
            PlaceDummy();
        }

        /// <summary>
        /// The cap as plain arithmetic rather than a run of 10 842 ticks. 900
        /// hits at 1.2 % are 1080 %, so the last 67 of them must do nothing.
        /// </summary>
        void CapTest()
        {
            damage.Reset();

            int accepted = 0;
            for (int i = 0; i < 900; i++)
            {
                if (damage.Add(1.2f))
                    accepted++;
            }

            Log($"[melee] == Cap: after 900 swings of 1.2 %" +
                $" stands at {damage.Percent:F1} % (want 1000.0)," +
                $" accepted {accepted} of 900 (want 834)," +
                $" IsCapped {damage.IsCapped}");

            damage.Reset();
        }

        void Step()
        {
            switch (phase)
            {
                case Phase.Hull: HullStep(); break;
                case Phase.Reach: ReachStep(); break;
                case Phase.Burst: BurstStep(); break;
                case Phase.Sideways: SidewaysStep(); break;
            }
        }

        /// <summary>Measures the shape a ray can actually meet. A CharacterController is
        /// smaller to a query than its own bounds claim, and the difference sits exactly
        /// where a player aims: at head height.</summary>
        void HullStep()
        {
            scripted.Input = new MoveInput { Yaw = Ref.Angle(Yaw) };

            tick++;
            if (tick < SettleTicks)
                return;

            Vector3 d = Ref.ToUnity(dummy.Position);
            HullBounds(out Vector3 bMin, out Vector3 bMax);

            // Straight down the axis finds the crown, sideways at the middle
            // finds the radius. ~0 with triggers is every layer here.
            float crown = float.NaN;
            if (World.Queries.Raycast(Ref.Pos(new Vector3(d.X, d.Y + 3f, d.Z)), Ref.Pos(UMath.Down),
                                      6f, CollisionMask.Everything, out QueryHit top))
                crown = Ref.ToUnity(top.Point).Y;

            // From the far side on purpose: the player stands on the -x side and
            // a ray from there measures him instead of the dummy.
            float radius = float.NaN;
            if (World.Queries.Raycast(Ref.Pos(new Vector3(d.X + 3f, d.Y, d.Z)), Ref.Pos(new Vector3(-1f, 0f, 0f)),
                                      6f, CollisionMask.Everything, out QueryHit side))
                radius = Ref.ToUnity(side.Point).X - d.X;

            Log($"[melee] == Hit area: dummy centre y {d.Y:F3}," +
                $" bounds top {bMax.Y:F3} bottom {bMin.Y:F3} wide {(bMax.X - bMin.X):F3}," +
                $" but queryable: crown {crown:F3} Radius {radius:F3}." +
                $" topmost {(bMax.Y - crown):F3} m are not hittable," +
                $" eye height of the player is {Ref.ToUnity(player.EyePosition).Y:F3}");

            tick = 0;
            phase = Phase.Reach;
        }

        /// <summary>Collider.bounds of the hull, in Unity's coordinates: the AABB of all
        /// its upright shapes together. They turn only about y, so each is its centre +-
        /// radius sideways and +- half its full height vertically (a Bepu capsule's
        /// Length is the segment between the spheres).</summary>
        void HullBounds(out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);
            bool any = false;

            if (dummyHull?.Collider is CompoundCollider compound)
            {
                TransformComponent pose = dummyHull.Entity.Transform;
                foreach (ColliderBase c in compound.Colliders)
                {
                    float radius, half;
                    if (c is CapsuleCollider capsule)
                        (radius, half) = (capsule.Radius, capsule.Length * 0.5f + capsule.Radius);
                    else if (c is CylinderCollider cylinder)
                        (radius, half) = (cylinder.Radius, cylinder.Length * 0.5f);
                    else
                        continue;

                    Vector3 centre = Ref.ToUnity(UMath.Add(pose.Position, UMath.Rotate(pose.Rotation, c.PositionLocal)));
                    Vector3 extent = new Vector3(radius, half, radius);
                    min = Vector3.Min(min, UMath.Sub(centre, extent));
                    max = Vector3.Max(max, UMath.Add(centre, extent));
                    any = true;
                }
            }

            if (!any)
                min = max = new Vector3(float.NaN, float.NaN, float.NaN);
        }

        /// <summary>
        /// Walks the dummy away a step at a time, aiming at its middle, so the
        /// reported limit is measured instead of assumed.
        /// </summary>
        void ReachStep()
        {
            float pitch = AimPitch();
            scripted.Input = new MoveInput { Yaw = Ref.Angle(Yaw), Pitch = Ref.Angle(pitch) };

            if (tick == SettleTicks)
                scripted.Input = new MoveInput
                {
                    Yaw = Ref.Angle(Yaw),
                    Pitch = Ref.Angle(pitch),
                    AttackPresses = 1,
                };

            tick++;
            if (tick < SettleTicks + ReadTicks + 1)
                return;

            if (attack.HitCount > 0)
            {
                lastHitCentre = distance;
                lastHitRay = attack.LastHitDistance;
            }

            distance += DistanceStep;
            tick = 0;
            attack.ResetCounters();

            if (distance <= lastDistance)
            {
                PlaceDummy();
                return;
            }

            Log($"[melee] == Range: last hit at horizontal" +
                $" distance {lastHitCentre:F3} m, the ray ran" +
                $" {lastHitRay:F3} m (limit {attack.Range:F3})," +
                $" step {DistanceStep:F3} m," +
                $" eye at y {Ref.ToUnity(player.EyePosition).Y:F3}");

            distance = 2.0f;
            PlaceDummy();
            damage.Reset();
            attack.ResetCounters();
            tick = 0;
            phase = Phase.Burst;
        }

        void BurstStep()
        {
            // Attack input only inside the window, then one quiet tick: the swing
            // of the last tick resolves AFTER this callback, so reading the
            // counters right away lost it every time.
            scripted.Input = new MoveInput
            {
                Yaw = Ref.Angle(Yaw),
                Pitch = Ref.Angle(AimPitch()),
                AttackPresses = (byte)(tick < BurstTicks ? 1 : 0),
            };

            tick++;
            if (tick <= BurstTicks)
                return;

            Vector3 d = Ref.ToUnity(dummy.Position);
            Log($"[melee] == Rate: {BurstTicks} ticks of constant fire give" +
                $" {attack.SwingCount} swings and {attack.HitCount} hits" +
                $" (want each 11), charge {damage.Percent:F1} % (want 13.2)," +
                $" dummy at ({d.X:F2}/{d.Y:F2}/{d.Z:F2})");

            // Within reach, but a metre off the line of sight.
            Place(dummy,
                  new Vector3(PlayerSpot.X + 1.5f, 0.94f, PlayerSpot.Z + 1.0f));

            damage.Reset();
            attack.ResetCounters();
            tick = 0;
            phase = Phase.Sideways;
        }

        void SidewaysStep()
        {
            // Aims dead ahead and level, not at the dummy: the question is
            // whether a ray hits what it is NOT pointed at.
            scripted.Input = new MoveInput { Yaw = Ref.Angle(Yaw) };

            if (tick == SettleTicks)
                scripted.Input = new MoveInput { Yaw = Ref.Angle(Yaw), AttackPresses = 1 };

            tick++;
            if (tick < SettleTicks + ReadTicks + 1)
                return;

            Vector3 d = Ref.ToUnity(dummy.Position);
            Log($"[melee] == Offset sideways: dummy at" +
                $" ({d.X:F2}/{d.Y:F2}/{d.Z:F2}), player looks horizontally" +
                $" towards +x, swings {attack.SwingCount}," +
                $" hits {attack.HitCount} (want 0)," +
                $" charge {damage.Percent:F1} % (want 0.0)");

            // Unity measured the DamageLabel's font here; the client is not ported yet.
            phase = Phase.Done;
            Exit(0);
        }

        /// <summary>
        /// Pitch that points the ray at the dummy's middle, in Unity's terms.
        /// Unity counts pitch positive downwards, which is why the angle is negated.
        /// </summary>
        float AimPitch()
        {
            Vector3 to = UMath.Sub(Ref.ToUnity(dummy.Position), Ref.ToUnity(player.EyePosition));
            float flat = UMath.Magnitude(new Vector2(to.X, to.Z));
            return -UMath.Atan2(to.Y, flat) * UMath.Rad2Deg;
        }

        void PlaceDummy()
        {
            Place(dummy,
                  new Vector3(PlayerSpot.X + distance, 0.94f, PlayerSpot.Z));
        }

        /// <summary>Teleports a body and clears what it was doing. Velocity survives a
        /// teleport, so without this every case would start with the momentum of the one
        /// before it. at is in Unity's coordinates.</summary>
        static void Place(PlayerMotor motor, Vector3 at)
        {
            motor.Teleport(Ref.Pos(at));
            motor.AddVelocity(UMath.Neg(motor.Velocity), 0f);
        }
    }
}
