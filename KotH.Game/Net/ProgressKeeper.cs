using System;
using KotH.Client.Steam;
using KotH.Shared.Match;
using KotH.Shared.Net;
using KotH.Shared.Simulation;
using Stride.Engine;

namespace KotH.Client.Net
{
    /// <summary>Feeds the player's XpCounter every confirmed tick of a networked game (only through Steam
    /// lobbies: direct IP earns nothing) and hands what it earned to SteamStats at a round's end, every
    /// two minutes and when the game is left. Reads the session, never writes.</summary>
    public sealed class ProgressKeeper : SyncScript
    {
        const float HandEvery = 120f;

        // A stall longer than this many ticks is skipped, not counted.
        const int Behind = 120;

        readonly XpCounter counter = new XpCounter { Solo = Environment.GetEnvironmentVariable("KOTH_XP_SOLO") == "1" };
        PeerSession session;
        SimulationBody self;
        int fed = -1;
        float since;
        bool over;

        /// <summary>The game and the own body; again after a map change.</summary>
        public void Bind(PeerSession session, SimulationBody self)
        {
            if (session != this.session)
                fed = -1;
            this.session = session;
            this.self = self;
        }

        /// <summary>Whether leaving now costs XP: a running round that counts, as of the newest confirmed tick.</summary>
        public bool AtStake => session != null && counter.AtStake;

        /// <summary>The player chose to leave a running round: the penalty goes to Steam with the rest.</summary>
        public void Forfeit()
        {
            Feed();
            counter.Forfeit();
            Hand();
        }

        /// <summary>The game is left: what it earned goes to Steam.</summary>
        public void Unbind()
        {
            Feed();
            Hand();
            session = null;
            self = null;
        }

        public override void Update()
        {
            Feed();
            since += (float)Game.UpdateTime.Elapsed.TotalSeconds;
            if (since >= HandEvery)
                Hand();
        }

        public override void Cancel() => Unbind();

        void Feed()
        {
            if (session == null || self == null)
                return;
            bool connected = LaunchOptions.Current.Steam && SteamClient.Ready;
            int newest = session.NewestFinal;
            if (newest < 0)
                return;
            // From now at the start, after a map's restart (the ticks begin anew) or a stall.
            if (fed < 0 || newest < fed || newest - fed > Behind)
                fed = newest - 1;
            bool ended = false;
            while (fed < newest)
            {
                fed++;
                if (!session.TryFinalState(fed, out WorldSnapshot state))
                    continue;
                counter.Step(state, self, connected);
                ended |= state.Match.Over && !over;
                over = state.Match.Over;
            }
            if (ended)
                Hand();
        }

        void Hand()
        {
            since = 0f;
            (int xp, int wins) = counter.Take();
            SteamStats.Add(xp, wins);
            SteamStats.Flush();
        }
    }
}
