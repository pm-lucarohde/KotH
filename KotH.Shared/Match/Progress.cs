using System;
using KotH.Shared.Net;
using KotH.Shared.Simulation;

namespace KotH.Shared.Match
{
    /// <summary>Player levels from XP (Luca, 11.11.2026): level 2 at 200, level 10 at 2500, at most 100;
    /// each level costs about 19 XP more than the one before. Customize opens at level 10.</summary>
    public static class Levels
    {
        public const int Max = 100;
        public const int CustomizeLevel = 10;

        /// <summary>XP needed in all for a level: (L - 1)(6500 + 350 L) / 36, to the nearest ten.</summary>
        public static int Total(int level)
        {
            if (level <= 1)
                return 0;
            long l = Math.Min(level, Max);
            long exact = (l - 1) * (6500 + 350 * l);
            return (int)((exact + 180) / 360 * 10);
        }

        public static int LevelOf(long xp)
        {
            int level = 1;
            while (level < Max && xp >= Total(level + 1))
                level++;
            return level;
        }

        /// <summary>The level, the XP into it and the XP it takes to the next (0 at the top), for a bar.</summary>
        public static (int Level, int Into, int Span) Into(long xp)
        {
            int level = LevelOf(xp);
            if (level >= Max)
                return (Max, 0, 0);
            int from = Total(level);
            return (level, (int)(xp - from), Total(level + 1) - from);
        }
    }

    /// <summary>The XP and wins one player earns, fed every confirmed tick (no rollback undoes those):
    /// 10 XP a minute of a running round, 1 per second the own team holds the zone, 100 and a win for
    /// winning. Only while online with another person; what is taken (Take) goes to Steam. Leaving a
    /// round that counts costs LeaveXp (Forfeit).</summary>
    public sealed class XpCounter
    {
        public const int TicksPerPlayXp = 6 * HillMatch.TicksPerPoint;
        public const int WinXp = 100;
        public const int LeaveXp = 200;

        int roundTicks, scoreTicks, playLeft, zoneLeft;
        bool over, seen;

        /// <summary>Earned and not yet taken.</summary>
        public int Xp { get; private set; }
        public int Wins { get; private set; }

        /// <summary>Whether the last tick was of a running round that counts: leaving it now costs LeaveXp.</summary>
        public bool AtStake { get; private set; }

        /// <summary>One confirmed tick: the round, the own body's score ticks, whether the own team won it.</summary>
        public void Step(in MatchState m, int ownScoreTicks, bool ownTeamWon, bool online)
        {
            // Running: what the clocks gained since the last tick; a clock that went back is a new round.
            int played = 0, held = 0;
            if (seen && m.Phase == MatchPhase.Running)
            {
                played = m.RoundTicks >= roundTicks ? m.RoundTicks - roundTicks : m.RoundTicks;
                held = ownScoreTicks >= scoreTicks ? ownScoreTicks - scoreTicks : ownScoreTicks;
            }
            bool won = seen && m.Over && !over && ownTeamWon;
            (roundTicks, scoreTicks, over, seen) = (m.RoundTicks, ownScoreTicks, m.Over, true);
            AtStake = online && m.Phase == MatchPhase.Running;
            if (!online)
                return;

            playLeft += played;
            zoneLeft += held;
            Xp += playLeft / TicksPerPlayXp + zoneLeft / HillMatch.TicksPerPoint;
            playLeft %= TicksPerPlayXp;
            zoneLeft %= HillMatch.TicksPerPoint;
            if (won)
            {
                Xp += WinXp;
                Wins++;
            }
        }

        /// <summary>A snapshot's tick for the body: its score, its team's win, and whether another person plays.</summary>
        public void Step(WorldSnapshot snapshot, SimulationBody self, bool connected)
        {
            int score = 0;
            bool person = false;
            for (int i = 0; i < snapshot.Count; i++)
            {
                SimulationBody body = snapshot.BodyAt(i);
                if (body == self)
                    score = snapshot.StateAt(i).ScoreTicks;
                else if (body.NetId >= NetWorld.FirstPlayerId && !body.Bot)
                    person = true;
            }
            MatchState m = snapshot.Match;
            Step(m, score, m.Over && HillMatch.SameTeam(m.Winner, self), connected && (person || Solo));
        }

        /// <summary>Test aid (KOTH_XP_SOLO=1): a round without another person counts too.</summary>
        public bool Solo { get; set; }

        /// <summary>The player leaves: a round at stake costs LeaveXp (Luca, 11.11.2026).</summary>
        public void Forfeit()
        {
            if (AtStake)
                Xp -= LeaveXp;
            AtStake = false;
        }

        /// <summary>The XP and wins earned since the last take.</summary>
        public (int Xp, int Wins) Take()
        {
            (int, int) taken = (Xp, Wins);
            Xp = Wins = 0;
            return taken;
        }
    }
}
