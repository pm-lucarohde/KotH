using System.Collections.Generic;

namespace KotH.Shared.Simulation
{
    /// <summary>
    /// Every body's state at the end of one tick. Keyed by body, not by list
    /// position: the order of the body list carries no meaning, and a
    /// restore must not start to give it one. Reused, so taking one each
    /// tick allocates nothing once the lists have grown.
    /// </summary>
    public sealed class WorldSnapshot
    {
        internal readonly List<SimulationBody> bodies = new List<SimulationBody>();
        internal readonly List<BodyState> states = new List<BodyState>();

        /// <summary>The tick this state is the end of.</summary>
        public int Tick { get; internal set; }

        public int Count => bodies.Count;

        public SimulationBody BodyAt(int index) => bodies[index];
        public BodyState StateAt(int index) => states[index];

        internal void Clear()
        {
            bodies.Clear();
            states.Clear();
        }
    }
}
