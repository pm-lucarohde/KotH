using KotH.Shared.Combat;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.Shared.Simulation
{
    /// <summary>
    /// A body the simulation drives. Where the input comes from is the only
    /// difference between a local player, a dummy and, later, an entity fed
    /// from the network. Joining a scene puts it into the tick, leaving the
    /// scene takes it out again, which Unity's OnEnable and OnDisable did.
    /// </summary>
    [DataContract(Inherited = true)]
    [DefaultEntityComponentProcessor(typeof(SimulationBodyProcessor), ExecutionMode = ExecutionMode.Runtime)]
    public abstract class SimulationBody : EntityComponent
    {
        [DataMemberIgnore]
        public PlayerMotor Motor { get; private set; }

        /// <summary>The world that ticks this body, set when it joins.</summary>
        [DataMemberIgnore]
        public SimulationSystem Simulation { get; internal set; }

        // Optional: a body without fists does not have to say so.
        MeleeAttack attack;
        DamagePercent damage;

        // Where the body joined, for the next round of a HillMatch.
        BodyState spawn;

        /// <summary>Points earned in this round, in ticks; see HillMatch.Points.</summary>
        [DataMemberIgnore] public int ScoreTicks { get; internal set; }

        /// <summary>Ticks in a row this body has held the zone alone.</summary>
        [DataMemberIgnore] public int AloneTicks { get; internal set; }

        [DataMemberIgnore] public bool HasSpawn { get; private set; }

        /// <summary>
        /// Input for the tick about to run. Called exactly once per tick by
        /// SimulationSystem, so per-frame state may be consumed here.
        /// </summary>
        public abstract MoveInput ReadInput();

        /// <summary>
        /// Wires the body to its siblings and the motor to the world. Runs
        /// when the body joins, which is where Unity's Awake ran.
        /// </summary>
        protected internal virtual void Attach(WorldQueries world)
        {
            Motor = Wire(Entity, world);
            attack = Entity.Get<MeleeAttack>();
            damage = Entity.Get<DamagePercent>();
        }

        /// <summary>
        /// Binds a body's motor, fists and hull to the world. Also for a body
        /// that is ticked by hand instead of by the system, as the probes do
        /// with the player, whose input reader Unity switched off.
        /// </summary>
        public static PlayerMotor Wire(Entity entity, WorldQueries world)
        {
            PlayerMotor motor = entity.Get<PlayerMotor>();
            motor.Initialize(world);
            entity.Get<MeleeAttack>()?.Initialize(motor);
            entity.Get<Hittable>()?.Check(motor.Hull);
            return motor;
        }

        /// <summary>This body's state as the last tick left it.</summary>
        public BodyState CaptureState()
        {
            return new BodyState
            {
                Motor = Motor.CaptureState(),
                AttackCooldown = attack != null ? attack.TicksUntilReady : 0,
                DamagePercent = damage != null ? damage.Percent : 0f,
                ScoreTicks = ScoreTicks,
                AloneTicks = AloneTicks,
            };
        }

        /// <summary>Takes the present state as where a new round starts this body.</summary>
        public void MarkSpawn()
        {
            spawn = CaptureState();
            spawn.ScoreTicks = 0;
            spawn.AloneTicks = 0;
            HasSpawn = true;
        }

        /// <summary>Back to where it joined, with no points: a new round.</summary>
        public void ResetToSpawn()
        {
            if (HasSpawn)
                RestoreState(spawn);
        }

        /// <summary>Puts this body back to a captured tick.</summary>
        public void RestoreState(in BodyState state)
        {
            Motor.RestoreState(state.Motor);

            if (attack != null)
                attack.TicksUntilReady = state.AttackCooldown;

            if (damage != null)
                damage.Restore(state.DamagePercent);

            ScoreTicks = state.ScoreTicks;
            AloneTicks = state.AloneTicks;
        }

        /// <summary>
        /// Combat for this tick. Driven separately from movement so every
        /// swing sees where all bodies ended up, not where the ones ahead of
        /// it in the list happened to be.
        /// </summary>
        public void TickCombat(in MoveInput input, float dt)
        {
            attack?.Tick(input, dt);
        }
    }
}
