using System;
using Avalonia.Controls;
using KotH.Client.Menu;
using KotH.Client.Net;
using KotH.Shared.Match;
using Stride.Core.Mathematics;
using Corner = KotH.Client.Menu.MenuOverlay.Corner;

namespace KotH.Client.Hud
{
    /// <summary>The round's XP at its end (Luca, 20.11.2026): a card under the winner, its lines coming in
    /// one by one while the sum counts up, then the level bar filling with what the round gave. A filled
    /// level flashes, its badge pops and a stamp says so; level 10 shows that Customize opened. A round
    /// that gave nothing shows why. HillBoard places and uploads its overlays with its own.</summary>
    sealed class XpCard
    {
        // The card's top in HUD units, under the crosshair; the parts' places in it as XpView lays them out.
        const float Top = 596f, CardWidth = 688f, LinesTop = 96f, LineHeight = 38f;
        const float BarLeft = 204f, BarWidth = 436f, BarTop = 234f, BarHeight = 14f;

        // Seconds: after the banner, the entrance, a line's slide and the next line's, the bar after the
        // last line, a whole level's fill, the stop on a full bar, a flash, the badge's and stamp's pop.
        const float Delay = 0.9f, EnterSeconds = 0.4f, LineSeconds = 0.3f, LineGap = 0.32f, BarGap = 0.35f;
        const float LevelSeconds = 1.2f, HoldSeconds = 0.4f, FlashSeconds = 0.6f, PopSeconds = 0.45f, CloseSeconds = 0.2f;

        // Test aid for shots: KOTH_SHOT_HUD=xp shows the card with a sample round (from KOTH_XP's XP),
        // xp0 with one that gave nothing.
        static readonly string Shot = Environment.GetEnvironmentVariable("KOTH_SHOT_HUD");
        public static readonly bool Pretend = Shot is "xp" or "xp0";

        readonly XpView view = new XpView();
        readonly MenuOverlay card, head, note, badge, old, gain, flash, spark, numbers, hint, levelUp, unlock;
        readonly MenuOverlay[] lines = new MenuOverlay[XpView.Lines];

        /// <summary>Whether a card is up and not closing: then Escape closes it, not opening the pause
        /// menu (PauseMenu asks, and sets CloseAsked; Luca: it hides the view).</summary>
        public static bool Showing { get; private set; }
        public static bool CloseAsked { get; set; }

        /// <summary>The HUD is gone (map change, menu): no card stays up to swallow an Escape.</summary>
        public static void Forget() => Showing = CloseAsked = false;

        /// <summary>Every part, for HillBoard to place, show behind the load screen and upload.</summary>
        public readonly MenuOverlay[] Overlays;

        // The bar's run as stretches within one level each, laid out when the card begins.
        struct Stretch
        {
            public int Level, From, To;
            public float Start, End;
            public bool Fills;
        }

        readonly Stretch[] stretches = new Stretch[8];
        int stretchCount;

        RoundXp round;
        int before, lineCount, startLevel, shownSum = -1, shownLevel = -1, shownInto = -1, shownSpan = -1;
        bool begun;
        float since = -1f, closedAt = -1f, barEnd, flashAt = -1f, flashStrength, popAt = -1f, stampAt = -1f, unlockAt = -1f;
        readonly float[] lineValue = new float[XpView.Lines];

        public XpCard(float size)
        {
            view.TakeParts();
            card = At(view.CardPart, 0f, 0f, size);
            head = At(view.HeadPart, 24f, 24f, size);
            Control[] rows = { view.Line0Part, view.Line1Part, view.Line2Part };
            for (int i = 0; i < lines.Length; i++)
                lines[i] = At(rows[i], 24f, LinesTop + i * LineHeight, size);
            note = At(view.NotePart, 24f, LinesTop, size);
            badge = At(view.BadgePart, 39f, BarTop + BarHeight * 0.5f - 32f, size);
            gain = At(view.GainPart, BarLeft, BarTop, size);
            old = At(view.OldPart, BarLeft, BarTop, size);
            flash = At(view.FlashPart, BarLeft - 24f, BarTop - 24f, size);
            spark = At(view.SparkPart, BarLeft - 24f, BarTop + BarHeight * 0.5f - 24f, size);
            numbers = At(view.NumbersPart, BarLeft, BarTop + 24f, size);
            hint = At(view.HintPart, 48f, BarTop + 46f, size);   // clear under the badge (Luca: Esc sat on it)
            // The stamp over the card's top edge, right, clear of the sum.
            levelUp = At(view.LevelUpPart, 490f, -54f, size);
            unlock = At(view.UnlockPart, 24f, 326f, size);
            levelUp.Angle = -0.1f;
            // Drawn in this order: the gain under the old share, the flash and spark over both.
            Overlays = new[] { card, head, lines[0], lines[1], lines[2], note, badge, gain, old, flash, spark, numbers, hint, levelUp, unlock };
        }

        static MenuOverlay At(Control part, float left, float top, float size)
            => new MenuOverlay(part, Corner.TopCenter, left + (float)part.Width * 0.5f - CardWidth * 0.5f, Top + top, size);

        /// <summary>One frame: while the banner shows (active) the card comes after Delay; scale is pixels per
        /// HUD unit; the keeper (null offline) tells what the round gave. Closed, it drops away until the
        /// next round's end.</summary>
        public void Show(float seconds, bool active, float scale, ProgressKeeper keeper)
        {
            since = active ? since < 0f ? 0f : since + seconds : -1f;
            if (since < 0f)
                closedAt = -1f;
            else if (CloseAsked && closedAt < 0f && begun)
                closedAt = since;
            CloseAsked = false;
            Showing = begun && closedAt < 0f && since >= Delay;
            // Laid out half a second early, unseen, so the texts are rendered when it shows.
            if (since < Delay * 0.5f)
            {
                // Always: behind the load screen HillBoard shows every part once to render it.
                foreach (MenuOverlay overlay in Overlays)
                    overlay.Visible = false;
                begun = false;
                return;
            }
            if (!begun)
                Begin(keeper);
            Animate(since - Delay, scale);
            if (closedAt >= 0f)
            {
                float gone = Math.Clamp((since - closedAt) / CloseSeconds, 0f, 1f);
                var drop = new Vector2(0f, 20f * scale * gone);
                foreach (MenuOverlay overlay in Overlays)
                {
                    overlay.Alpha *= 1f - gone;
                    overlay.Shift += drop;
                    overlay.Visible &= gone < 1f;
                }
            }
        }

        void Begin(ProgressKeeper keeper)
        {
            if (Shot == "xp")
            {
                round = new RoundXp { Played = 42, PlayedTicks = 252 * HillMatch.TicksPerPoint, Zone = 91,
                                      ZoneTicks = 91 * HillMatch.TicksPerPoint, Win = XpCounter.WinXp, Won = true, Counted = true };
                before = PlayerProgress.Xp;
            }
            else if (keeper != null && !Pretend)
                (round, before) = keeper.LastRound;
            else
                (round, before) = (default, PlayerProgress.Xp);
            if (!round.Counted)
                before = PlayerProgress.Xp;

            lineCount = !round.Counted ? 0 : round.Won ? 3 : 2;
            view.Line(0, "Time played", Clock(round.PlayedTicks), "+" + round.Played);
            view.Line(1, "Zone held", Clock(round.ZoneTicks), "+" + round.Zone);
            view.Line(2, "Victory", "", "+" + round.Win);
            (lineValue[0], lineValue[1], lineValue[2]) = (round.Played, round.Zone, round.Win);

            // The bar: from the XP before the round to after it, a stretch per level it runs through.
            int to = before + (round.Counted ? round.Total : 0);
            float time = LineStart(Math.Max(lineCount, 1) - 1) + LineSeconds + BarGap;
            stretchCount = 0;
            for (int xp = before; xp < to && stretchCount < stretches.Length;)
            {
                int level = Levels.LevelOf(xp);
                if (level >= Levels.Max)
                    break;
                int floor = Levels.Total(level), next = Levels.Total(level + 1), end = Math.Min(to, next);
                float length = Math.Max(0.3f, (float)(end - xp) / (next - floor) * LevelSeconds);
                stretches[stretchCount++] = new Stretch { Level = level, From = xp, To = end, Start = time, End = time + length, Fills = end == next };
                time += length + (end == next ? HoldSeconds : 0f);
                xp = end;
            }
            barEnd = stretchCount > 0 ? stretches[stretchCount - 1].End : 0f;
            startLevel = Levels.LevelOf(before);
            (flashAt, popAt, stampAt, unlockAt) = (-1f, -1f, -1f, -1f);
            shownLevel = shownInto = shownSpan = -1;
            shownSum = 0;
            begun = true;
            view.Sum("+0 XP");
            foreach (MenuOverlay overlay in Overlays)
                overlay.Visible = true;
        }

        float LineStart(int i) => EnterSeconds * 0.5f + i * LineGap;

        void Animate(float t, float scale)
        {
            // The card rises into place and fades in; the parts on it move with it.
            float enter = Math.Clamp(t / EnterSeconds, 0f, 1f), alpha = Math.Clamp(t / 0.25f, 0f, 1f);
            var rise = new Vector2(0f, 24f * scale * (1f - MainMenu.BackOut(enter)));
            foreach (MenuOverlay overlay in Overlays)
            {
                overlay.Shift = rise;
                overlay.Alpha = alpha;
            }

            // The lines slide in from the left one by one, each adding to the sum as it comes.
            float sum = 0f;
            for (int i = 0; i < lines.Length; i++)
            {
                float u = Math.Clamp((t - LineStart(i)) / LineSeconds, 0f, 1f), eased = CubicOut(u);
                lines[i].Visible = i < lineCount;
                lines[i].Alpha = u;
                lines[i].Shift = rise + new Vector2(-36f * scale * (1f - eased), 0f);
                if (i < lineCount)
                    sum += lineValue[i] * eased;
            }
            note.Visible = lineCount == 0;
            int whole = (int)MathF.Round(sum);
            if (whole != shownSum)
            {
                shownSum = whole;
                view.Sum("+" + whole + " XP");
            }

            // Under the round's bright share, what the level held before it, muted, while it is that level.
            Bar(t, out int level, out float share, out int into, out int span);
            gain.Reveal = share;
            old.Reveal = span == 0 ? 1f : level == startLevel ? Math.Min(share, (float)(before - Levels.Total(startLevel)) / span) : 0f;

            if (level != shownLevel)
            {
                // A new level after the first: flash, pop and stamp, and a note when it opens something.
                if (shownLevel > 0 && level > shownLevel)
                {
                    (flashAt, flashStrength, popAt, stampAt) = (t, 1f, t, t);
                    for (int l = shownLevel + 1; l <= level; l++)
                        if (Levels.Unlocks(l) is { } what)
                        {
                            view.Unlocked(what);
                            unlockAt = t + 0.3f;
                        }
                }
                shownLevel = level;
                view.Level("Level " + level);
            }
            if (into != shownInto || span != shownSpan)
            {
                (shownInto, shownSpan) = (into, span);
                view.Progress(span == 0 ? "Max level" : Number(into) + " / " + Number(span) + " XP");
            }
            // A last, softer flash where the bar stops.
            if (stretchCount > 0 && flashAt < barEnd && t >= barEnd && !stretches[stretchCount - 1].Fills)
                (flashAt, flashStrength) = (t, 0.55f);

            flash.Alpha = alpha * (flashAt < 0f ? 0f : flashStrength * Math.Max(0f, 1f - (t - flashAt) / FlashSeconds));
            badge.Zoom = popAt < 0f ? 1f : 1.35f - 0.35f * MainMenu.BackOut(Math.Clamp((t - popAt) / PopSeconds, 0f, 1f));

            // The spark rides the bar's tip while it fills, shimmering, and fades where it stops.
            bool moving = stretchCount > 0 && t >= stretches[0].Start && t < barEnd + 0.3f;
            spark.Alpha = !moving ? 0f : alpha * Math.Clamp((barEnd + 0.3f - t) / 0.3f, 0f, 1f);
            spark.Zoom = 1f + 0.12f * MathF.Sin(t * 22f);
            spark.Shift = rise + new Vector2(share * BarWidth * scale, 0f);

            levelUp.Visible = stampAt >= 0f;
            if (stampAt >= 0f)
            {
                float pop = Math.Clamp((t - stampAt) / PopSeconds, 0f, 1f);
                levelUp.Alpha = Math.Clamp((t - stampAt) / 0.12f, 0f, 1f);
                levelUp.Zoom = 1.6f - 0.6f * MainMenu.BackOut(pop);
            }
            unlock.Visible = unlockAt >= 0f && t >= unlockAt;
            if (unlock.Visible)
            {
                float u = Math.Clamp((t - unlockAt) / 0.35f, 0f, 1f);
                unlock.Alpha = u;
                unlock.Shift = rise + new Vector2(0f, 16f * scale * (1f - CubicOut(u)));
            }
        }

        /// <summary>Where the bar stands at t: its level, how full, and the XP into it of the level's span
        /// (0 at the top). On a stop after a filled level the bar stays full and the badge already says the next.</summary>
        void Bar(float t, out int level, out float share, out int into, out int span)
        {
            int xp = before;
            level = Levels.LevelOf(before);
            for (int i = 0; i < stretchCount; i++)
            {
                Stretch s = stretches[i];
                if (t < s.Start)
                    break;
                level = s.Level;
                float u = Math.Clamp((t - s.Start) / (s.End - s.Start), 0f, 1f);
                // A level that fills runs on into its flash; the last stretch slows down where it stops.
                float eased = s.Fills ? u * u * (3f - 2f * u) : CubicOut(u);
                xp = s.From + (int)MathF.Round((s.To - s.From) * eased);
                if (u >= 1f && s.Fills)
                {
                    level = s.Level + 1;
                    if (t >= s.End + HoldSeconds)
                        continue;
                    int spanAt = Levels.Total(s.Level + 1) - Levels.Total(s.Level);
                    (share, into, span) = (1f, spanAt, spanAt);
                    return;
                }
            }
            int floor = Levels.Total(level);
            span = level >= Levels.Max ? 0 : Levels.Total(level + 1) - floor;
            into = span == 0 ? 0 : xp - floor;
            share = span == 0 ? 1f : Math.Clamp((float)into / span, 0f, 1f);
        }

        static float CubicOut(float u)
        {
            float v = 1f - u;
            return 1f - v * v * v;
        }

        static string Clock(int ticks)
        {
            int s = ticks / HillMatch.TicksPerPoint;
            return string.Format(HudRenderer.Numbers, "{0}:{1:D2}", s / 60, s % 60);
        }

        static string Number(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
