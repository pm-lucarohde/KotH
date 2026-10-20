using System;
using System.Collections.Generic;
using KotH.Shared.Match;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;

namespace KotH.Client.Hud
{
    /// <summary>
    /// The round on screen: own points and the time left at the top, what
    /// the zone is doing below, the warm-up as a bar, the best four at the
    /// top right and the winner in the middle. Before a networked round the
    /// top line waits for players or counts down instead. Presentation
    /// only: it reads the match and the bodies and never writes to either.
    /// </summary>
    [DataContract("KotH.HillBoard")]
    public sealed class HillBoard : StartupScript, IHudElement
    {
        [Display("Schrift", "Schrift")] public SpriteFont Font;
        [Display("Schrift für den Sieger", "Schrift")] public SpriteFont BannerFont;

        [Display("Frei", "Farben")] public Color FreeColor = Color.White;
        [Display("Eigene Zone", "Farben")] public Color OwnColor = new Color(30, 200, 230);
        [Display("Umkämpft", "Farben")] public Color ContestedColor = new Color(255, 205, 60);
        [Display("Gegner", "Farben")] public Color EnemyColor = new Color(255, 85, 70);

        [Display("Rand", "Maße (Pixel)")] public float Margin = 12f;
        [Display("Balkenbreite", "Maße (Pixel)")] public float BarWidth = 220f;
        [Display("Balkenhöhe", "Maße (Pixel)")] public float BarHeight = 6f;

        /// <userdoc>So viele Körper zeigt die Bestenliste oben rechts</userdoc>
        [Display("Bestenliste", "Anzeige")] public int Leaders = 4;

        [DataMemberIgnore] public HillMatch Match { get; set; }
        [DataMemberIgnore] public SimulationSystem Simulation { get; set; }

        /// <summary>The body this screen belongs to, named "Du" everywhere.</summary>
        [DataMemberIgnore] public SimulationBody Self { get; set; }

        static readonly Color4 Band = Color4.PremultiplyAlpha(new Color(0, 0, 0, 0xD0).ToColor4());
        const float Pad = 6f;

        HudRenderer hud;
        readonly List<SimulationBody> ranking = new List<SimulationBody>();
        readonly List<string> rankLines = new List<string>();

        // Rebuilt once per tick, not per frame: the values only change then.
        int builtTick = -1;
        string score = "", status = "", banner = "", countdown = "";
        Color statusColor;
        float warmup;

        public override void Start()
        {
            hud = HudRenderer.Of(this);
            hud.Add(this);
        }

        public override void Cancel() => hud?.Remove(this);

        /// <summary>What the zone means for this screen; the visual uses the same colours.</summary>
        public static ZoneLook Look(in MatchState m, SimulationBody self)
        {
            if (m.Over || m.Inside == 0)
                return ZoneLook.Free;
            if (m.Holder == null)
                return ZoneLook.Contested;
            return m.Holder == self ? ZoneLook.Own : ZoneLook.Enemy;
        }

        public Color ColorOf(ZoneLook look) => look switch
        {
            ZoneLook.Own => OwnColor,
            ZoneLook.Contested => ContestedColor,
            ZoneLook.Enemy => EnemyColor,
            _ => FreeColor,
        };

        public void Draw(SpriteBatch batch, Vector2 screen)
        {
            if (Match == null || Simulation == null || Font == null)
                return;

            Rebuild();

            float y = Margin;
            y = Centered(batch, Font, score, Font.Size, screen.X, y, Color.White) + Pad;
            y = Centered(batch, Font, status, Font.Size, screen.X, y, statusColor);

            if (warmup > 0f)
            {
                float x = (screen.X - BarWidth) * 0.5f;
                y += Pad;
                batch.Draw(hud.White, new RectangleF(x - 1f, y - 1f, BarWidth + 2f, BarHeight + 2f), Band);
                batch.Draw(hud.White, new RectangleF(x, y, BarWidth * warmup, BarHeight),
                           Color4.PremultiplyAlpha(OwnColor.ToColor4()));
            }

            DrawRanking(batch, screen);

            if (banner.Length > 0)
            {
                SpriteFont big = BannerFont ?? Font;
                float top = screen.Y * 0.32f;
                top = Centered(batch, big, banner, big.Size, screen.X, top, Color.White) + Pad;
                Centered(batch, Font, countdown, Font.Size, screen.X, top, Color.White);
            }
        }

        void Rebuild()
        {
            int tick = Simulation.CurrentTick;
            if (tick == builtTick)
                return;
            builtTick = tick;

            MatchState m = Match.State;
            int own = Self != null ? HillMatch.Points(Self.ScoreTicks) : 0;
            int win = Match.WinTicks / HillMatch.TicksPerPoint;
            int left = (Math.Max(0, Match.LimitTicks - m.RoundTicks) + HillMatch.TicksPerPoint - 1) / HillMatch.TicksPerPoint;
            score = string.Format(HudRenderer.Numbers, "Punkte {0} / {1}     {2}:{3:D2}", own, win, left / 60, left % 60);

            ZoneLook look = Look(m, Self);
            statusColor = ColorOf(look);
            status = look switch
            {
                ZoneLook.Own => Self.AloneTicks > Match.WarmupTicks ? "Du hältst die Zone" : "Du nimmst die Zone ein",
                ZoneLook.Contested => "Umkämpft",
                ZoneLook.Enemy => NameOf(m.Holder) + " hält die Zone",
                _ => "Zone frei",
            };

            warmup = look == ZoneLook.Own && Self.AloneTicks <= Match.WarmupTicks && Match.WarmupTicks > 0
                ? (float)Self.AloneTicks / Match.WarmupTicks : 0f;

            if (m.Phase == MatchPhase.Waiting)
            {
                score = string.Format(HudRenderer.Numbers, "Warte auf Spieler ({0} / {1})",
                                      HillMatch.CountPlayers(Simulation.Bodies), m.MinPlayers);
                status = "";
            }
            else if (m.Phase == MatchPhase.Countdown)
            {
                int start = (m.PhaseTicks + HillMatch.TicksPerPoint - 1) / HillMatch.TicksPerPoint;
                score = string.Format(HudRenderer.Numbers, "Match beginnt in {0}:{1:D2}", start / 60, start % 60);
                status = "";
            }

            if (m.Over)
            {
                banner = m.Winner == null ? "Unentschieden"
                       : m.Winner == Self ? "Du gewinnst!"
                       : NameOf(m.Winner) + " gewinnt";
                int seconds = (m.PhaseTicks + HillMatch.TicksPerPoint - 1) / HillMatch.TicksPerPoint;
                countdown = string.Format(HudRenderer.Numbers, "Neue Runde in {0}", seconds);
            }
            else
                banner = countdown = "";

            ranking.Clear();
            foreach (SimulationBody body in Simulation.Bodies)
                ranking.Add(body);
            // Ties keep the body list's order, so the list does not flicker.
            ranking.Sort((a, b) =>
            {
                int byScore = b.ScoreTicks.CompareTo(a.ScoreTicks);
                return byScore != 0 ? byScore : IndexOf(a).CompareTo(IndexOf(b));
            });

            rankLines.Clear();
            for (int i = 0; i < Math.Min(Leaders, ranking.Count); i++)
                rankLines.Add(string.Format(HudRenderer.Numbers, "{0}. {1}  {2}",
                                            i + 1, NameOf(ranking[i]), HillMatch.Points(ranking[i].ScoreTicks)));
        }

        void DrawRanking(SpriteBatch batch, Vector2 screen)
        {
            float y = Margin;
            for (int i = 0; i < rankLines.Count; i++)
            {
                Vector2 size = Font.MeasureString(rankLines[i], Font.Size);
                var at = new Vector2(screen.X - Margin - size.X, y);
                batch.Draw(hud.White, new RectangleF(at.X - Pad, at.Y, size.X + 2f * Pad, size.Y), Band);
                bool own = ranking[i] == Self;
                batch.DrawString(Font, rankLines[i], Font.Size, at, own ? OwnColor : Color.White);
                y += size.Y;
            }
        }

        /// <summary>Draws one line centred on a band; returns the y below it.</summary>
        float Centered(SpriteBatch batch, SpriteFont font, string text, float size, float width, float y, Color color)
        {
            if (text.Length == 0)
                return y;
            Vector2 extent = font.MeasureString(text, size);
            var at = new Vector2(MathF.Round((width - extent.X) * 0.5f), y);
            batch.Draw(hud.White, new RectangleF(at.X - Pad, at.Y, extent.X + 2f * Pad, extent.Y), Band);
            batch.DrawString(font, text, size, at, color);
            return y + extent.Y;
        }

        string NameOf(SimulationBody body) => body == Self ? "Du" : body.Entity.Name;

        int IndexOf(SimulationBody body)
        {
            IReadOnlyList<SimulationBody> bodies = Simulation.Bodies;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i] == body)
                    return i;
            return -1;
        }
    }

    /// <summary>How the zone looks from one body's point of view.</summary>
    public enum ZoneLook { Free, Own, Contested, Enemy }
}
