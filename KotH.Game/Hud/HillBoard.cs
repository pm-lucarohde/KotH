using System;
using System.Collections.Generic;
using KotH.Client.Menu;
using KotH.Client.Net;
using KotH.Client.Player;
using KotH.Shared.Combat;
using KotH.Shared.Match;
using KotH.Shared.Net;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Corner = KotH.Client.Menu.MenuOverlay.Corner;

namespace KotH.Client.Hud
{
    /// <summary>The round on screen (HudView): own points and time at the top, the zone with the
    /// warm-up bar below, the best four top right, own damage at the bottom, the winner in the
    /// middle; before a networked round it waits or counts down. Reads the match and the bodies,
    /// never writes.</summary>
    [DataContract("KotH.HillBoard")]
    public sealed class HillBoard : SyncScript
    {
        [Display("Free", "Colours")] public Color FreeColor = Color.White;
        [Display("Own zone", "Colours")] public Color OwnColor = new Color(30, 200, 230);
        [Display("Contested", "Colours")] public Color ContestedColor = new Color(255, 205, 60);
        [Display("Enemy", "Colours")] public Color EnemyColor = new Color(255, 85, 70);

        /// <userdoc>An enemy stands alone in the zone and is capturing it, so scores no points yet.</userdoc>
        [Display("Enemy capturing", "Colours")] public Color EnemyTakingColor = new Color(255, 140, 30);

        /// <userdoc>How many bodies the leaderboard top right shows (at most 4)</userdoc>
        [Display("Leaderboard", "Display")] public int Leaders = 4;

        [DataMemberIgnore] public HillMatch Match { get; set; }
        [DataMemberIgnore] public SimulationSystem Simulation { get; set; }

        /// <summary>The body this screen belongs to, named "You" everywhere.</summary>
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
        MenuOverlay top, points, status, track, fill, board, ranks, hurt, ammo, dashChip, banner, countdown, notice;
        MenuOverlay[] overlays;
        readonly List<SimulationBody> ranking = new List<SimulationBody>();

        // Rebuilt once per tick, not per frame: the values only change then.
        int builtTick = -1;
        string statusText = "", bannerText = "";
        float warmup, popped = -1f;

        // Texts made again only when their numbers or names change, so a tick allocates nothing.
        NumberText countText, clockText, winText;
        JoinedText statusJoin, bannerJoin;
        readonly NumberText[] rankText = new NumberText[HudView.Rows], scoreText = new NumberText[HudView.Rows];
        Comparison<SimulationBody> scoreOrder;

        // The own blaster's shots and progress as last shown.
        Blaster blaster;
        int shownShots = -1, shownNext = -1;
        uint shownSpores = uint.MaxValue;

        // The own dash and its progress as last shown.
        Dash dash;
        int shownDash = -1, shownDashNext = -1;

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
            status = new MenuOverlay(view.StatusPart, Corner.TopCenter, 0f, 72f, Size);
            track = new MenuOverlay(view.TrackPart, Corner.TopCenter, 0f, 124f, Size);
            fill = new MenuOverlay(view.FillPart, Corner.TopCenter, 0f, 152f, Size);
            board = new MenuOverlay(view.BoardPart, Corner.TopRight, 8f, 8f, Size);
            ranks = new MenuOverlay(view.RanksPart, Corner.TopRight, 8f, 8f, Size);
            hurt = new MenuOverlay(view.DamagePart, Corner.BottomCenter, 0f, 16f, Size);
            // The dash in the corner, the blaster's spores beside it while it is up.
            dashChip = new MenuOverlay(view.DashPart, Corner.BottomRight, 8f, 12f, Size);
            ammo = new MenuOverlay(view.AmmoPart, Corner.BottomRight, 160f, 12f, Size);
            // The middle ones stay at a third of the height however small: 322 / Size.
            banner = new MenuOverlay(view.BannerPart, Corner.TopCenter, 0f, 358f, Size);
            countdown = new MenuOverlay(view.CountdownPart, Corner.TopCenter, 0f, 470f, Size);
            notice = new MenuOverlay(view.NoticePart, Corner.TopCenter, 0f, 358f, Size);
            overlays = new[] { top, points, status, track, fill, board, ranks, hurt, ammo, dashChip, banner, countdown, notice };
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
            if (HillMatch.SameTeam(m.Holder, self))
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
            ammo.Visible = round && ShowShots();
            dashChip.Visible = round && ShowDash();

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
            string caption = "Points", count = null, time = null;

            ZoneLook look = Look(m, Self, Match.WarmupTicks);
            // In teams the team is meant: "you" for the own, all names for another team.
            bool team = Match.Teamed && Mate(Self) != null;
            statusText = look switch
            {
                ZoneLook.Own => Self.AloneTicks > Match.WarmupTicks ? team ? "Your team holds the zone" : "You hold the zone"
                                                                    : team ? "Your team is capturing" : "You are capturing",
                ZoneLook.Contested => "Contested",
                ZoneLook.Enemy => statusJoin.Of(TeamName(m.Holder), Mate(m.Holder) != null ? " hold the zone" : " holds the zone"),
                ZoneLook.EnemyTaking => statusJoin.Of(TeamName(m.Holder), Mate(m.Holder) != null ? " are capturing" : " is capturing"),
                _ => "Zone free",
            };

            warmup = look == ZoneLook.Own && Self.AloneTicks <= Match.WarmupTicks && Match.WarmupTicks > 0
                ? (float)Self.AloneTicks / Match.WarmupTicks : 0f;

            if (m.Phase == MatchPhase.Waiting)
            {
                caption = Match.Mode switch
                {
                    MatchMode.Duo => "Duo: waiting for players", MatchMode.Trio => "Trio: waiting for players",
                    MatchMode.Squad => "Squad: waiting for players", _ => "Waiting for players",
                };
                count = countText.Of(CountFormat, HillMatch.CountPlayers(Simulation.Bodies), m.MinPlayers);
                time = statusText = "";
            }
            else if (m.Phase == MatchPhase.Countdown)
            {
                caption = Match.Mode switch
                {
                    MatchMode.Duo => "Duo match starts in", MatchMode.Trio => "Trio match starts in",
                    MatchMode.Squad => "Squad match starts in", _ => "Match starts in",
                };
                count = "";
                time = Clock((m.PhaseTicks + HillMatch.TicksPerPoint - 1) / HillMatch.TicksPerPoint);
                statusText = "";
            }
            count ??= countText.Of(CountFormat, own, win);
            time ??= Clock(left);
            view.Bar(caption, count, time);
            view.Zone(statusText, ToAvalonia(ColorOf(look)));

            // Networked, a win shows only once no rollback can take it back.
            MatchState final = ConfirmedRound != null ? ConfirmedRound() ?? default : m;
            if (final.Over || PretendBanner)
            {
                bool ownWin = HillMatch.SameTeam(final.Winner, Self) && Self != null;
                bannerText = PretendBanner ? "You win!"
                           : final.Winner == null ? "Draw"
                           : ownWin ? "You win!"
                           : bannerJoin.Of(TeamName(final.Winner), Mate(final.Winner) != null ? " win" : " wins");
                int seconds = PretendBanner ? 10 : (final.PhaseTicks + HillMatch.TicksPerPoint - 1) / HillMatch.TicksPerPoint;
                view.Win(bannerText, PretendBanner || ownWin,
                         winText.Of(Match.Stage == MatchStage.Round ? "Lobby in {0}" : "New round in {0}", seconds));
            }
            else
                bannerText = "";

            // One row per team: its lead, named for all.
            ranking.Clear();
            foreach (SimulationBody body in Simulation.Bodies)
                if (HillMatch.LeadOf(body, Simulation.Bodies) == body)
                    ranking.Add(body);
            ranking.Sort(scoreOrder ??= ByScore);

            int ranked = Math.Min(Math.Min(Leaders, HudView.Rows), ranking.Count);
            view.Ranked(ranked);
            for (int i = 0; i < ranked; i++)
                view.Rank(i, rankText[i].Of("{0}", i + 1), TeamName(ranking[i]),
                          scoreText[i].Of("{0}", HillMatch.Points(ranking[i].ScoreTicks)), HillMatch.SameTeam(ranking[i], Self));
        }

        // Ties keep the body list's order, so the list does not flicker.
        int ByScore(SimulationBody a, SimulationBody b)
        {
            int points = b.ScoreTicks.CompareTo(a.ScoreTicks);
            return points != 0 ? points : IndexOf(a).CompareTo(IndexOf(b));
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

        /// <summary>The own blaster's shots to the view when they changed; false while it is not up.</summary>
        bool ShowShots()
        {
            if (Self != damageOf || blaster?.Entity != Self?.Entity)
                blaster = Self?.Entity.Get<Blaster>();
            if (blaster == null || !blaster.State.Selected)
                return false;
            // In the own spore colour (Luca), set again when it changed.
            uint spores = LaunchOptions.Current.Look[FigureLook.Spores];
            if (spores != shownSpores)
            {
                shownSpores = spores;
                var painted = new Color3(HudView.SporePainted.R / 255f, HudView.SporePainted.G / 255f, HudView.SporePainted.B / 255f);
                Color3 c = FigureColors.Spores(LaunchOptions.Current.Look, painted);
                view.Spores(ToAvalonia(new Color(c.R, c.G, c.B, 1f)));
            }
            BlasterState s = blaster.State;
            int per = blaster.PointsPerShot * HillMatch.TicksPerPoint;
            // In whole pixels of the bar, so a tick only redraws when it shows.
            int next = s.Shots >= blaster.MaxShots || per <= 0 ? 0 : s.Progress * 170 / per;
            if (s.Shots != shownShots || next != shownNext)
            {
                shownShots = s.Shots;
                shownNext = next;
                view.Shots(s.Shots, next / 170.0);
            }
            return true;
        }

        /// <summary>The own dash to the view when it changed; false for a body without one.</summary>
        bool ShowDash()
        {
            if (dash?.Entity != Self?.Entity)
                dash = Self?.Entity.Get<Dash>();
            if (dash == null)
                return false;
            DashState s = dash.State;
            int per = dash.PointsPerDash * HillMatch.TicksPerPoint;
            int next = s.Charges >= dash.MaxDashes || per <= 0 ? 0 : s.Progress * 100 / per;
            if (s.Charges != shownDash || next != shownDashNext)
            {
                shownDash = s.Charges;
                shownDashNext = next;
                view.Dash(s.Charges > 0, next / 100.0);
            }
            return true;
        }

        const string CountFormat = "{0} / {1}";

        string Clock(int seconds) => clockText.Of("{0}:{1:D2}", seconds / 60, seconds % 60);

        // A text from a format and up to two numbers, formatted again only when one of them changes.
        struct NumberText
        {
            string format, text;
            int a, b;

            public string Of(string format, int a, int b = 0)
            {
                if (text == null || a != this.a || b != this.b || format != this.format)
                    (this.format, this.a, this.b, text) = (format, a, b, string.Format(HudRenderer.Numbers, format, a, b));
                return text;
            }
        }

        // A text of two parts ("Bot 3" + " holds the zone"), joined again only when one changes.
        struct JoinedText
        {
            string first, second, text;

            public string Of(string first, string second)
            {
                if (text == null || first != this.first || second != this.second)
                    (this.first, this.second, text) = (first, second, first + second);
                return text;
            }
        }

        static global::Avalonia.Media.Color ToAvalonia(Color c) => global::Avalonia.Media.Color.FromArgb(c.A, c.R, c.G, c.B);

        string NameOf(SimulationBody body) => body == Self ? "You" : body.Entity.Name;

        // Any other of the body's team, null alone.
        SimulationBody Mate(SimulationBody body)
            => body == null ? null : HillMatch.AheadOf(body, Simulation.Bodies) ?? HillMatch.BehindOf(body, Simulation.Bodies);

        // A team's names, the own one first ("You, Bot 2 & Bot 3"); kept per lead while the team stays.
        readonly Dictionary<SimulationBody, (int Members, bool Own, string Name)> teamNames =
            new Dictionary<SimulationBody, (int, bool, string)>();
        readonly List<string> names = new List<string>();

        string TeamName(SimulationBody body)
        {
            IReadOnlyList<SimulationBody> bodies = Simulation.Bodies;
            SimulationBody lead = HillMatch.LeadOf(body, bodies);
            if (Mate(lead) == null)
                return NameOf(body);
            int members = 17;
            bool own = false;
            for (SimulationBody m = lead; m != null; m = HillMatch.BehindOf(m, bodies))
            {
                members = members * 31 + m.NetId;
                own |= m == Self;
            }
            if (teamNames.TryGetValue(body, out var known) && known.Members == members && known.Own == own)
                return known.Name;
            names.Clear();
            if (own)
                names.Add(NameOf(Self));
            for (SimulationBody m = lead; m != null; m = HillMatch.BehindOf(m, bodies))
                if (m != Self)
                    names.Add(NameOf(m));
            string name = names.Count == 1 ? names[0]
                : string.Join(", ", names.GetRange(0, names.Count - 1)) + " & " + names[^1];
            teamNames[body] = (members, own, name);
            return name;
        }

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
