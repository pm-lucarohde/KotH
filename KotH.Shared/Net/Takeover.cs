using System.Collections.Generic;
using KotH.Shared.Match;

namespace KotH.Shared.Net
{
    /// <summary>What a peer hands its new host session when it takes the game over (host change):
    /// its newest final state, everyone in it, and the round's settings. The new host decides from
    /// Tick on; every other peer takes the state back with its Welcome (PeerSession.Resume).</summary>
    public sealed class Takeover
    {
        /// <summary>The state's tick, the first one the new host decides.</summary>
        public int Tick;
        public byte[] State;

        /// <summary>The players and bots in the state, by NetId; bots flagged.</summary>
        public List<NetEvent> Roster;

        /// <summary>The new host's own player.</summary>
        public int FounderNetId;

        /// <summary>The new timeline's generation, one past the old host's.</summary>
        public byte Generation;
        public MatchStage Stage;
        public string NextMap;

        /// <summary>A private round's key, as the old host's welcome gave it; null for an open round.</summary>
        public PasswordKey Password;
    }
}
