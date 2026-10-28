using System.Collections.Generic;
using KotH.Shared.Match;

namespace KotH.Shared.Simulation
{
    /// <summary>Every body's state at the end of one tick. Keyed by body, not by list
    /// position: the order of the body list carries no meaning, and a restore must not
    /// start to give it one. Reused, so taking one each tick allocates nothing once the
    /// lists have grown.</summary>
    public sealed class WorldSnapshot
    {
        internal readonly List<SimulationBody> bodies = new List<SimulationBody>();
        internal readonly List<BodyState> states = new List<BodyState>();

        /// <summary>The tick this state is the end of.</summary>
        public int Tick { get; internal set; }

        /// <summary>The round as this tick left it; default without a match.</summary>
        public MatchState Match { get; internal set; }

        public int Count => bodies.Count;

        public SimulationBody BodyAt(int index) => bodies[index];

        public bool Contains(SimulationBody body) => bodies.Contains(body);

        /// <summary>Appends a body as a decoded transfer had it; see StateCodec.</summary>
        internal void Add(SimulationBody body, in BodyState state)
        {
            bodies.Add(body);
            states.Add(state);
        }
        public BodyState StateAt(int index) => states[index];

        public void Clear()
        {
            bodies.Clear();
            states.Clear();
        }

        /// <summary>Makes this a copy of another: the same bodies, their states and the round.</summary>
        public void CopyFrom(WorldSnapshot other)
        {
            Clear();
            bodies.AddRange(other.bodies);
            states.AddRange(other.states);
            Tick = other.Tick;
            Match = other.Match;
        }
    }
}
