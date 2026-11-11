using KotH.Probes.Harness;
using KotH.Shared.Hosting;
using KotH.Shared.Match;

namespace KotH.Probes.Probes
{
    /// <summary>Levels and XP (Luca, 11.11.2026): the curve's fixed points and its order, then XpCounter
    /// on made-up confirmed ticks: a running minute, the zone, a win, offline, a new round, the win pause.</summary>
    public sealed class ProgressProbe : Probe
    {
        protected override string Tag => "progress";

        int failures;

        public override int Run(HeadlessWorld unused, int maxTicks)
        {
            Curve();
            Counter();
            Log(failures == 0 ? "[progress] PASSED" : $"[progress] {failures} errors");
            return failures == 0 ? 0 : 1;
        }

        void Curve()
        {
            Check(Levels.Total(1) == 0 && Levels.Total(2) == 200 && Levels.Total(10) == 2500, "level 2 at 200, level 10 at 2500");
            Check(Levels.Total(3) == 420 && Levels.Total(5) == 920 && Levels.Total(20) == 7130 && Levels.Total(50) == 32670
                  && Levels.Total(100) == 114130, $"3, 5, 20, 50, 100 at 420, 920, 7130, 32670, 114130 (100: {Levels.Total(100)})");
            bool steeper = true;
            for (int l = 2; l < Levels.Max; l++)
                steeper &= Levels.Total(l + 1) - Levels.Total(l) > Levels.Total(l) - Levels.Total(l - 1);
            Check(steeper, "every level costs more than the one before");
            Check(Levels.LevelOf(0) == 1 && Levels.LevelOf(199) == 1 && Levels.LevelOf(200) == 2 && Levels.LevelOf(2499) == 9
                  && Levels.LevelOf(2500) == 10 && Levels.LevelOf(114130) == 100 && Levels.LevelOf(int.MaxValue) == 100, "levels at the edges");
            Check(Levels.Into(2200) == (9, 2200 - Levels.Total(9), 2500 - Levels.Total(9)) && Levels.Into(200000) == (100, 0, 0), "the bar");
        }

        void Counter()
        {
            const int Minute = 60 * 40;
            var c = new XpCounter();
            var m = new MatchState { Phase = MatchPhase.Running };
            int score = 0;
            void Play(int ticks, bool zone, bool online = true)
            {
                for (int i = 0; i < ticks; i++)
                {
                    m.RoundTicks++;
                    if (zone)
                        score++;
                    c.Step(m, score, false, online);
                }
            }

            c.Step(m, 0, false, true);
            Play(Minute, false);
            Check(c.Take() == (10, 0), "a running minute: 10 XP");
            Play(30 * 40, true);
            Check(c.Take() == (5 + 30, 0), "30 s holding the zone: 30 XP and 5 for the time");
            Play(Minute, true, online: false);
            Check(c.Take() == (0, 0), "offline: nothing");

            m.Phase = MatchPhase.Over;
            m.PhaseTicks = 400;
            c.Step(m, score, true, true);
            for (int i = 0; i < 400; i++)
                c.Step(m, score, true, true);
            Check(c.Take() == (100, 1), "a win: 100 XP and a win, once, the pause nothing");

            // A new round: the clocks start at 0 again.
            m = new MatchState { Phase = MatchPhase.Running };
            score = 0;
            Play(Minute / 2, false);
            Play(Minute / 2, false);
            Check(c.Take() == (10, 0), "a new round counts from its start");

            m.Phase = MatchPhase.Countdown;
            for (int i = 0; i < Minute; i++)
            {
                m.RoundTicks = 0;
                c.Step(m, 0, false, true);
            }
            Check(c.Take() == (0, 0), "the countdown: nothing");
        }

        void Check(bool ok, string what)
        {
            Log((ok ? "  ok   " : "  FAIL ") + what);
            if (!ok)
                failures++;
        }
    }
}
