using KotH.Shared.Player;
using UnityEngine;

namespace KotH.Shared.Simulation
{
    /// <summary>
    /// A body the simulation drives. Where the input comes from is the only
    /// difference between a local player, a dummy and, later, an entity fed
    /// from the network. Registration mirrors PlayerVisual, so switching the
    /// component off takes the body out of the tick entirely.
    /// </summary>
    [RequireComponent(typeof(PlayerMotor))]
    public abstract class SimulationBody : MonoBehaviour
    {
        public PlayerMotor Motor { get; private set; }

        /// <summary>
        /// Input for the tick about to run. Called exactly once per tick by
        /// SimulationSystem, so per-frame state may be consumed here.
        /// </summary>
        public abstract MoveInput ReadInput();

        protected virtual void Awake()
        {
            Motor = GetComponent<PlayerMotor>();
        }

        protected virtual void OnEnable()
        {
            SimulationSystem.Register(this);
        }

        protected virtual void OnDisable()
        {
            SimulationSystem.Unregister(this);
        }

        protected virtual void Start()
        {
            // Without a driver in the scene nothing moves at all and nothing
            // says why. Asked in Start, by when every Awake has run.
            if (!SimulationSystem.HasActive)
                Debug.LogWarning(
                    "Kein SimulationSystem in der Szene, nichts wird getickt.", this);
        }
    }
}
