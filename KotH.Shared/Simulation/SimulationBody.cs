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

        // Optional: a body without fists does not have to say so.
        MeleeAttack attack;
        DamagePercent damage;

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
            Motor = Entity.Get<PlayerMotor>();
            attack = Entity.Get<MeleeAttack>();
            damage = Entity.Get<DamagePercent>();

            Motor.Initialize(world);
            attack?.Initialize(Motor);
            Entity.Get<Hittable>()?.Check(Motor.Hull);
        }

        /// <summary>This body's state as the last tick left it.</summary>
        public BodyState CaptureState()
        {
            return new BodyState
            {
                Motor = Motor.CaptureState(),
                AttackCooldown = attack != null ? attack.TicksUntilReady : 0,
                DamagePercent = damage != null ? damage.Percent : 0f,
            };
        }

        /// <summary>Puts this body back to a captured tick.</summary>
        public void RestoreState(in BodyState state)
        {
            Motor.RestoreState(state.Motor);

            if (attack != null)
                attack.TicksUntilReady = state.AttackCooldown;

            if (damage != null)
                damage.Restore(state.DamagePercent);
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
