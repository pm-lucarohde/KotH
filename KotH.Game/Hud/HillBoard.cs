using System;
using System.Collections.Generic;
using KotH.Client.Menu;
using KotH.Shared.Combat;
using KotH.Shared.Match;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Corner = KotH.Client.Menu.MenuOverlay.Corner;

namespace KotH.Client.Hud
{
    /// <summary>
    /// The round on screen (HudView, Avalonia): own points and the time left
    /// at the top, what the zone is doing below with the warm-up as a bar, the
    /// best four at the top right, the own damage percent at the bottom and
    /// the winner in the middle. Before a networked round the top bar waits
    /// for players or counts down instead. Presentation only: it reads the
    /// match and the bodies and never writes to either.
    /// </summary>
    [DataContract("KotH.HillBoard")]
    public sealed class HillBoard : SyncScript
    {
        [Display("Frei", "Farben")] public Color FreeColor = Color.White;
        [Display("Eigene Zone", "Farben")] public Color OwnColor = new Color(30, 200, 230);
        [Display("Umkämpft", "Farben")] public Color ContestedColor = new Color(255, 205, 60);
        [Display("Gegner", "Farben")] public Color EnemyColor = new Color(255, 85, 70);

        /// <userdoc>Ein Gegner steht allein in der Zone und nimmt sie gerade ein, bekommt also noch keine Punkte.</userdoc>
        [Display("Gegner nimmt ein", "Farben")] public Color EnemyTakingColor = new Color(255, 140, 30);

        /// <userdoc>So viele Körper zeigt die Bestenliste oben rechts (höchstens 4)</userdoc>
        [Display("Bestenliste", "Anzeige")] public int Leaders = 4;

        [DataMemberIgnore] public HillMatch Match { get; set; }
        [DataMemberIgnore] public SimulationSystem Simulation { get; set; }

        /// <summary>The body this screen belongs to, named "Du" everywhere.</summary>
        [DataMemberIgnore] public SimulationBody Self { get; set; }

        /// <summary>Networked: the round as the host confirmed it, for the winner banner.</summary>
        [DataMemberIgnore] public Func<MatchState?> ConfirmedRound { get; set; }

        /// <summary>Networked: what to say while there is no game, such as connecting.</summary>
        [DataMemberIgnore] public Func<string> Notice { get; set; }

        // Test aid for shots: KOTH_SHOT_HUD=banner shows the winner banner with a sample.
        static readonly bool PretendBanner = Environment.GetEnvironmentVariable("KOTH_SHOT_HUD") == "banner";

        // The whole HUD this much smaller than laid out in HudView (Luca).
        const float Size = 0.9f;

        // The banner pops in this many seconds, and fades in in these.
        const float PopSeconds = 0.35f, FadeSeconds = 0.2f;

        HudView view;
        HudRenderer hud;
        MenuOverlay top, points, status, track, fill, board, ranks, hurt, banner, countdown, notice;
        MenuOverlay[] overlays;
        readonly List<SimulationBody> ranking = new List<SimulationBody>();

        // Rebuilt once per tick, not per frame: the values only change then.
        int builtTick = -1;
        string statusText = "", bannerText = "";
        float warmup, popped = -1f;

        // The own damage percent, its text rebuilt only when it changes.
        SimulationBody damageOf;
        DamagePercent damage;
        float shownDamage = float.NaN;

        public override void Start()
        {
            MenuOverlay.Start<MenuApp>();
            hud = HudRenderer.Of(this);
            view = new HudView();
            view.Own(ToAvalonia(OwnColor));
            view.TakeParts();

            // In units of a screen 1080 high; see HudView for the pieces' 24 of shadow room.
            // The own damage sits at the bottom middle (Luca).
            top = new MenuOverlay(view.TopPart, Corner.TopCenter, 0f, 8f, Size);
            points = new MenuOverlay(view.PointsPart, Corner.TopCenter, 0f, 8f, Size);
            status = new MenuOverlay(view.StatusPart, Corner.TopCenter, 0f, 78f, Size);
            track = new MenuOverlay(view.TrackPart, Corner.TopCenter, 0f, 130f, Size);
            fill = new MenuOverlay(view.FillPart, Corner.TopCenter, 0f, 158f, Size);
            board = new MenuOverlay(view.BoardPart, Corner.TopRight, 8f, 8f, Size);
            ranks = new MenuOverlay(view.RanksPart, Corner.TopRight, 8f, 8f, Size);
            hurt = new MenuOverlay(view.DamagePart, Corner.BottomCenter, 0f, 16f, Size);
            // The middle ones stay at a third of the height however small: 322 / Size.
            banner = new MenuOverlay(view.BannerPart, Corner.TopCenter, 0f, 358f, Size);
            countdown = new MenuOverlay(view.CountdownPart, Corner.TopCenter, 0f, 470f, Size);
            notice = new MenuOverlay(view.NoticePart, Corner.TopCenter, 0f, 358f, Size);
            overlays = new[] { top, points, status, track, fill, board, ranks, hurt, banner, countdown, notice };
            foreach (MenuOverlay overlay in overlays)
            {
                overlay.Visible = false;
                hud.Add(overlay);
            }
        }

        public override void Cancel()
        {
            if (overlays == null)
                return;
            foreach (MenuOverlay overlay in overlays)
            {
                hud.Remove(overlay);
                overlay.Dispose();
            }
            overlays = null;
        }

        /// <summary>What the zone means for this screen; the visual uses the same colours.</summary>
        public static ZoneLook Look(in MatchState m, SimulationBody self, int warmupTicks)
        {
            if (m.Over || m.Inside == 0)
                return ZoneLook.Free;
            if (m.Holder == null)
                return ZoneLook.Contested;
            if (m.Holder == self)
                return ZoneLook.Own;
            return m.Holder.AloneTicks > warmupTicks ? ZoneLook.Enemy : ZoneLook.EnemyTaking;
        }

        public Color ColorOf(ZoneLook look) => look switch
        {
            ZoneLook.Own => OwnColor,
            ZoneLook.Contested => ContestedColor,
            ZoneLook.Enemy => EnemyColor,
            ZoneLook.EnemyTaking => EnemyTakingColor,
            _ => FreeColor,
        };

        public override void Update()
        {
            if (overlays == null)
                return;
            var backBuffer = GraphicsDevice.Presenter.BackBuffer;
            var screen = new Vector2(backBuffer.Width, backBuffer.Height);
            foreach (MenuOverlay overlay in overlays)
                overlay.Place(screen);

            // Behind the load screen, which hides the rest of the HUD, every part
            // renders and uploads its sample texts once: Skia learns the type
            // and each texture exists before the game is seen.
            if (Services.GetService<LoadScreen>() is { Covers: true })
                foreach (MenuOverlay overlay in overlays)
                    overlay.Visible = true;
            else
                Show((float)Game.UpdateTime.Elapsed.TotalSeconds);

            MenuOverlay.Tick(Game.UpdateTime.FrameCount);
            foreach (MenuOverlay overlay in overlays)
                overlay.Upload(Game.GraphicsContext.CommandList);
        }

        /// <summary>Hands this frame's values to the view and picks the parts that show.</summary>
        void Show(float seconds)
        {
            string says = Simulation != null ? Notice?.Invoke() : null;
            notice.Visible = says != null;
            if (says != null)
                view.Say(says);
            bool round = Simulation != null && Match != null;
            if (round)
                Rebuild();

            top.Visible = points.Visible = board.Visible = ranks.Visible = round;
            status.Visible = round && statusText.Length > 0;
            track.Visible = fill.Visible = round && warmup > 0f;
            fill.Reveal = warmup;
            hurt.Visible = round && ShowDamage();

            bool won = round && bannerText.Length > 0 && says == null;
            banner.Visible = countdown.Visible = won;
            popped = won ? popped < 0f ? 0f : popped + seconds : -1f;
            if (won)
            {
                // The first frame stays clear: the new title may not be rendered yet.
                banner.Zoom = 0.85f + 0.15f * MainMenu.BackOut(Math.Min(1f, popped / PopSeconds));
                banner.Alpha = countdown.Alpha = Math.Min(1f, popped / FadeSeconds);
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
            string caption = "Punkte", count = string.Format(HudRenderer.Numbers, "{0} / {1}", own, win);
            string time = Clock(left);

            ZoneLook look = Look(m, Self, Match.WarmupTicks);
            statusText = look switch
            {
                ZoneLook.Own => Self.AloneTicks > Match.WarmupTicks ? "Du hältst die Zone" : "Du nimmst die Zone ein",
                ZoneLook.Contested => "Umkämpft",
                ZoneLook.Enemy => NameOf(m.Holder) + " hält die Zone",
                ZoneLook.EnemyTaking => NameOf(m.Holder) + " nimmt die Zone ein",
                _ => "Zone frei",
            };

            warmup = look == ZoneLook.Own && Self.AloneTicks <= Match.WarmupTicks && Match.WarmupTicks > 0
                ? (float)Self.AloneTicks / Match.WarmupTicks : 0f;

            if (m.Phase == MatchPhase.Waiting)
            {
                caption = "Warte auf Spieler";
                count = string.Format(HudRenderer.Numbers, "{0} / {1}", HillMatch.CountPlayers(Simulation.Bodies), m.MinPlayers);
                time = statusText = "";
            }
            else if (m.Phase == MatchPhase.Countdown)
            {
                caption = "Match beginnt in";
                count = "";
                time = Clock((m.PhaseTicks + HillMatch.TicksPerPoint - 1) / HillMatch.TicksPerPoint);
                statusText = "";
            }
            view.Bar(caption, count, time);
            view.Zone(statusText, ToAvalonia(ColorOf(look)));

            // Networked, a win shows only once no rollback can take it back.
            MatchState final = ConfirmedRound != null ? ConfirmedRound() ?? default : m;
            if (final.Over || PretendBanner)
            {
                bannerText = PretendBanner ? "Du gewinnst!"
                           : final.Winner == null ? "Unentschieden"
                           : final.Winner == Self ? "Du gewinnst!"
                           : NameOf(final.Winner) + " gewinnt";
                int seconds = PretendBanner ? 10 : (final.PhaseTicks + HillMatch.TicksPerPoint - 1) / HillMatch.TicksPerPoint;
                view.Win(bannerText, PretendBanner || final.Winner == Self && Self != null,
                         string.Format(HudRenderer.Numbers, "Neue Runde in {0}", seconds));
            }
            else
                bannerText = "";

            ranking.Clear();
            foreach (SimulationBody body in Simulation.Bodies)
                ranking.Add(body);
            // Ties keep the body list's order, so the list does not flicker.
            ranking.Sort((a, b) =>
            {
                int byScore = b.ScoreTicks.CompareTo(a.ScoreTicks);
                return byScore != 0 ? byScore : IndexOf(a).CompareTo(IndexOf(b));
            });

            int ranked = Math.Min(Math.Min(Leaders, HudView.Rows), ranking.Count);
            view.Ranked(ranked);
            for (int i = 0; i < ranked; i++)
                view.Rank(i, (i + 1).ToString(HudRenderer.Numbers), NameOf(ranking[i]),
                          HillMatch.Points(ranking[i].ScoreTicks).ToString(HudRenderer.Numbers), ranking[i] == Self);
        }

        /// <summary>The own damage percent to the view when it changed; false while there is none.</summary>
        bool ShowDamage()
        {
            if (Self != damageOf)
            {
                damageOf = Self;
                damage = Self?.Entity.Get<DamagePercent>();
                shownDamage = float.NaN;
            }
            if (damage == null)
                return false;
            if (damage.Percent != shownDamage)
            {
                shownDamage = damage.Percent;
                view.Hurt(shownDamage.ToString("F1", HudRenderer.Numbers) + " %");
            }
            return true;
        }

        static string Clock(int seconds) => string.Format(HudRenderer.Numbers, "{0}:{1:D2}", seconds / 60, seconds % 60);

        static global::Avalonia.Media.Color ToAvalonia(Color c) => global::Avalonia.Media.Color.FromArgb(c.A, c.R, c.G, c.B);

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
    public enum ZoneLook { Free, Own, Contested, Enemy, EnemyTaking }
}
