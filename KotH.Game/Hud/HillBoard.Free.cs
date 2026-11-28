using System;
using System.Collections.Generic;
using KotH.Client.Localization;
using KotH.Client.Player;
using KotH.Shared.Match;
using KotH.Shared.Net;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Client.Hud
{
    /// <summary>The HUD of 1, 2, 3, Free: runners left and the time at the top, the own role bottom left,
    /// the count in the middle for everyone (1, 2, 3, Free!, the last one on to 6), who got free or was caught,
    /// the draw's wheel, whom a caught or free one watches, and per player "You win!" or "You lose".</summary>
    public sealed partial class HillBoard
    {
        static readonly string[] Digits = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15" };
        static readonly Color Green = new Color(110, 220, 120), Grey = new Color(165, 170, 176);

        // The wheel's slices, in turn round the palette.
        static readonly global::Avalonia.Media.Color[] SliceColors =
        {
            global::Avalonia.Media.Color.FromRgb(0xD9, 0x5A, 0x4E), global::Avalonia.Media.Color.FromRgb(0xE8, 0x9F, 0x45),
            global::Avalonia.Media.Color.FromRgb(0xE3, 0xC5, 0x55), global::Avalonia.Media.Color.FromRgb(0x72, 0xB8, 0x6E),
            global::Avalonia.Media.Color.FromRgb(0x3E, 0xA8, 0xA0), global::Avalonia.Media.Color.FromRgb(0x4C, 0x8B, 0xD0),
            global::Avalonia.Media.Color.FromRgb(0x87, 0x6A, 0xC6), global::Avalonia.Media.Color.FromRgb(0xC9, 0x68, 0xA0),
        };

        // A flash in the middle (someone free, the own catch or mark) lasts this long, in ticks.
        const int FlashTicks = 80;

        // The wheel turns this share of a spin and rests on the drawn name for the rest.
        const float TurnShare = 0.75f;

        readonly Dictionary<int, FreeRole> lastRoles = new Dictionary<int, FreeRole>();
        int lastRoundTicks = int.MaxValue, lastMarked;
        string roleText = "", callText = "", callName = "";
        Color callColor = Color.White;
        string flashText, flashName;
        Color flashColor;
        int flashUntil = -1;
        bool flashOwn;
        Comparison<SimulationBody> roleOrder;
        PlayerCamera camera;
        SimulationBody watchShown;

        // The wheel: per spin its names, shares and colours, whom it lands on and how far it turns.
        sealed class Spin
        {
            public readonly List<string> Names = new List<string>();
            public readonly List<double> Shares = new List<double>();
            public readonly List<global::Avalonia.Media.Color> Colors = new List<global::Avalonia.Media.Color>();
            public SimulationBody Chosen;
            public float Turn;
        }

        readonly Spin[] spins = { new Spin(), new Spin() };
        readonly List<SimulationBody> candidates = new List<SimulationBody>(NetWorld.MaxPlayers);
        readonly List<int> wheelCatchers = new List<int>();
        int spinCount, builtSpin = -1, wheelDrawnAt = -1, captionShown = -1;
        bool newRound, hiding;

        // A body's role as the HUD shows it: a catcher stays a runner while the wheel still turns.
        FreeRole Shown(SimulationBody b) => hiding && b.Role == FreeRole.Catcher ? FreeRole.Runner : b.Role;

        void RebuildFree(in MatchState m, ZoneLook look)
        {
            IReadOnlyList<SimulationBody> bodies = Simulation.Bodies;
            int tick = Simulation.CurrentTick;
            if (m.RoundTicks < lastRoundTicks)
            {
                lastRoles.Clear();
                newRound = true;
                flashUntil = -1;
            }
            lastRoundTicks = m.RoundTicks;
            // Until the wheel rests on its last catcher, the catchers show as runners (no spoiler).
            int spinsShown = Math.Max(1, m.DrawnAt == wheelDrawnAt && !newRound ? spinCount : m.Catchers);
            hiding = m.Phase == MatchPhase.Running && m.RoundTicks - m.DrawnAt >= 0
                && m.RoundTicks - m.DrawnAt < HillMatch.DrawTicks * (1f - (1f - TurnShare) / spinsShown);

            int left = HillMatch.RunnersLeft(bodies), all = left;
            for (int i = 0; i < bodies.Count; i++)
                all += bodies[i].Role is FreeRole.Caught or FreeRole.Free ? 1 : 0;
            int ticksLeft = Math.Max(0, Match.RoundLimitTicks - Math.Max(m.RoundTicks, HillMatch.DrawTicks));   // stands during the draw
            view.Bar(Loc.T("hud.free.runners"), countText.Of(CountFormat, left, all, ltr: true), Clock((ticksLeft + HillMatch.TicksPerPoint - 1) / HillMatch.TicksPerPoint));

            statusText = !Match.Playing || m.Over ? "" : look switch
            {
                ZoneLook.Enemy => Loc.T("hud.free.chaser_in_zone"),
                ZoneLook.Own => Loc.T(Self.Role == FreeRole.Rescuing ? "hud.free.freeing_all" : "hud.free.you_counting"),
                ZoneLook.EnemyTaking or ZoneLook.Contested => Loc.T("hud.free.runners_in_zone"),
                _ => Loc.T("hud.zone_free"),
            };
            view.Zone(statusText, ToAvalonia(ColorOf(look)));
            warmup = Self == null || !Match.Playing || m.Over ? 0f : Self.Role switch
            {
                FreeRole.Runner when Self.RoleTicks > 0 => (float)Self.RoleTicks / HillMatch.FreeTicks,
                FreeRole.Rescuing => (float)Self.RoleTicks / HillMatch.RescueTicks,
                FreeRole.Catcher when Self.RoleTicks > 0 && Marked(Self, bodies) => (float)Self.RoleTicks / HillMatch.MarkTicks,
                _ => 0f,
            };

            if (m.Phase == MatchPhase.Running)
                Events(bodies, tick);
            ShowOwnRole(bodies);
            Call(m, bodies, tick);

            // The end, as the host confirmed it: per player, by Luca's rule (HillMatch.Won).
            MatchState final = ConfirmedRound != null ? ConfirmedRound() ?? default : m;
            if (final.Over && Self != null)
            {
                bool won = HillMatch.Won(Self, bodies);
                bannerText = Loc.T(Self.Role is FreeRole.Waiting or FreeRole.None ? "hud.round_over" : won ? "hud.you_win" : "hud.you_lose");
                int seconds = (final.PhaseTicks + HillMatch.TicksPerPoint - 1) / HillMatch.TicksPerPoint;
                view.Win(bannerText, won, winText.Of(Loc.T(Match.Stage == MatchStage.Round ? "hud.lobby_in" : "hud.new_round_in"), seconds));
            }
            else
                bannerText = "";

            // Catchers first, then who still runs (furthest count first), the free, the caught; the own row stays.
            ranking.Clear();
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].NetId >= NetWorld.FirstPlayerId)
                    ranking.Add(bodies[i]);
            ranking.Sort(roleOrder ??= ByRole);
            int ranked = Math.Min(Math.Min(Leaders, HudView.Rows), ranking.Count);
            int own = Self != null ? ranking.IndexOf(Self) : -1;
            if (own >= ranked && ranked > 0)
                (ranking[ranked - 1], ranking[own]) = (ranking[own], ranking[ranked - 1]);
            view.Ranked(ranked);
            for (int i = 0; i < ranked; i++)
                view.Rank(i, rankText[i].Of("{0}", i + 1, ltr: true), NameOf(ranking[i]), StatusOf(ranking[i]), ranking[i] == Self);
        }

        string StatusOf(SimulationBody b) => Shown(b) switch
        {
            FreeRole.Catcher => Loc.T("hud.free.role.chaser"),
            FreeRole.Runner => Loc.T(b.MarkedBy != 0 ? "hud.free.role.marked" : "hud.free.role.runner"),
            FreeRole.Rescuing or FreeRole.Free => Loc.T("hud.free.role.free"),
            FreeRole.Caught => Loc.T("hud.free.role.caught"),
            FreeRole.Waiting => Loc.T("hud.free.role.spectator"),
            _ => "",
        };

        static int Rank(FreeRole role) => role switch
        {
            FreeRole.Catcher => 0,
            FreeRole.Rescuing => 1,
            FreeRole.Runner => 2,
            FreeRole.Free => 3,
            FreeRole.Caught => 4,
            _ => 5,
        };

        int ByRole(SimulationBody a, SimulationBody b)
        {
            int c = Rank(Shown(a)).CompareTo(Rank(Shown(b)));
            if (c == 0 && a.Role == FreeRole.Runner)
                c = b.RoleTicks.CompareTo(a.RoleTicks);
            return c != 0 ? c : a.NetId.CompareTo(b.NetId);
        }

        static bool Marked(SimulationBody catcher, IReadOnlyList<SimulationBody> bodies)
        {
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].Role == FreeRole.Runner && bodies[i].MarkedBy == catcher.NetId)
                    return true;
            return false;
        }

        // Who changed role since the last tick: free, everyone freed, the own catch or mark flash in the middle.
        void Events(IReadOnlyList<SimulationBody> bodies, int tick)
        {
            bool rescued = false;
            for (int i = 0; i < bodies.Count; i++)
            {
                SimulationBody b = bodies[i];
                FreeRole now = b.Role, was = lastRoles.TryGetValue(b.NetId, out FreeRole r) ? r : now;
                lastRoles[b.NetId] = now;
                if (was == now)
                    continue;
                if (was == FreeRole.Caught && now == FreeRole.Free)
                    rescued = true;
                else if (was == FreeRole.Runner && now is FreeRole.Free or FreeRole.Rescuing)
                    Flash(Loc.T("hud.free.flash.free"), b == Self ? Loc.T(now == FreeRole.Rescuing ? "hud.free.flash.you_free_stay" : "hud.free.flash.you_free")
                                                                  : Loc.F("hud.free.flash.other_free", NameOf(b)), Green, b == Self, tick);
                else if (b == Self && now == FreeRole.Caught)
                    Flash(Loc.T("hud.free.flash.caught"), Loc.T("hud.free.flash.you_caught"), EnemyColor, true, tick);
            }
            if (rescued)
                Flash(Loc.T("hud.free.flash.free"), Loc.T("hud.free.flash.all_free"), Green, true, tick);
            int marked = Self?.Role == FreeRole.Runner ? Self.MarkedBy : 0;
            if (marked != 0 && marked != lastMarked)
                Flash(Loc.T("hud.free.flash.marked"), Loc.T("hud.free.flash.marked_hint"), EnemyTakingColor, true, tick);
            lastMarked = marked;
        }

        void Flash(string text, string name, Color color, bool own, int tick)
        {
            if (!own && flashOwn && tick < flashUntil)
                return;
            (flashText, flashName, flashColor, flashOwn, flashUntil) = (text, name, color, own, tick + FlashTicks);
        }

        void ShowOwnRole(IReadOnlyList<SimulationBody> bodies)
        {
            FreeRole r = Self != null ? Shown(Self) : FreeRole.None;
            (string text, Color color) = r switch
            {
                FreeRole.Catcher => (Loc.T("hud.free.role.chaser"), EnemyColor),
                FreeRole.Runner => Self.MarkedBy != 0 ? (Loc.T("hud.free.role.runner_marked"), EnemyTakingColor) : (Loc.T("hud.free.role.runner"), OwnColor),
                FreeRole.Rescuing => (Loc.T("hud.free.role.stay_free_all"), Green),
                FreeRole.Free => (Loc.T("hud.free.role.free"), Green),
                FreeRole.Caught => (Loc.T("hud.free.role.caught"), Grey),
                FreeRole.Waiting => (Loc.T("hud.free.role.spectator"), Grey),
                _ => ("", Color.White),
            };
            roleText = text;
            view.ShowRole(text, ToAvalonia(color));
        }

        // The middle: a flash, else the head start's seconds, else the furthest count (a catcher's on its marks).
        void Call(in MatchState m, IReadOnlyList<SimulationBody> bodies, int tick)
        {
            string text = "", name = "";
            Color color = Color.White;
            if (m.Over)
            {
            }
            else if (tick < flashUntil && flashText != null)
                (text, name, color) = (flashText, flashName, flashColor);
            else if (Match.HeadStart)
            {
                int seconds = (Match.HeadStartLeft + HillMatch.TicksPerPoint - 1) / HillMatch.TicksPerPoint;
                text = Digits[Math.Min(seconds, Digits.Length - 1)];
                name = Loc.T(Self?.Role == FreeRole.Catcher ? "hud.free.head_start_go" : "hud.free.head_start_wait");
            }
            else if (Match.Playing)
            {
                SimulationBody best = null;
                for (int i = 0; i < bodies.Count; i++)
                {
                    SimulationBody b = bodies[i];
                    bool counts = b.Role is FreeRole.Runner or FreeRole.Rescuing || b.Role == FreeRole.Catcher && Marked(b, bodies);
                    if (counts && b.RoleTicks > 0 && (best == null || b.RoleTicks > best.RoleTicks))
                        best = b;
                }
                if (best != null)
                {
                    text = Digits[Math.Min(best.RoleTicks / HillMatch.TicksPerPoint + 1, 6)];
                    name = best.Role == FreeRole.Catcher ? (best == Self ? Loc.T("hud.free.you_cash_in") : callFmt.Of("hud.free.other_cash_in", NameOf(best)))
                         : best.Role == FreeRole.Rescuing ? (best == Self ? Loc.T("hud.free.you_free_all") : callFmt.Of("hud.free.other_free_all", NameOf(best)))
                         : NameOf(best);
                    color = best.Role == FreeRole.Catcher ? EnemyColor : best == Self ? OwnColor : Color.White;
                }
            }
            (callText, callName, callColor) = (text, name, color);
            view.ShowCall(text, name, ToAvalonia(color));
        }

        /// <summary>Per frame: which of the mode's parts show, the wheel's turn, whom the view follows.</summary>
        void ShowFree(bool visible)
        {
            bool free = visible && Match != null && Simulation != null && Match.IsFree;
            MatchState m = free ? Match.State : default;
            bool playing = free && m.Phase is MatchPhase.Running or MatchPhase.Over;
            bool spinning = playing && m.Phase == MatchPhase.Running && Wheel(m);
            // Any other mode: a late joiner watches, its badge says so (Luca).
            bool watching = !free && visible && Match != null && Self?.Role == FreeRole.Waiting && Match.State.Phase == MatchPhase.Running;
            string spectator = Loc.T("hud.free.role.spectator");
            if (watching && roleText != spectator)
                view.ShowRole(roleText = spectator, ToAvalonia(Grey));
            role.Visible = playing && roleText.Length > 0 || watching;
            wheel.Visible = pointer.Visible = wheelCaption.Visible = spinning;
            call.Visible = playing && !spinning && callText.Length > 0 && bannerText.Length == 0;

            camera ??= FindCamera();
            SimulationBody watched = visible ? camera?.Watched : null;
            watch.Visible = watched != null && bannerText.Length == 0;
            if (watched != null && watched != watchShown)
                view.ShowWatch(Loc.F("hud.free.watching", NameOf(watched)));
            watchShown = watched;
        }

        PlayerCamera FindCamera()
        {
            if (SceneSystem.SceneInstance?.RootScene is not { } scene)
                return null;
            foreach (Entity e in scene.Entities)
                if (e.Get<PlayerCamera>() is { } found)
                    return found;
            return null;
        }

        // The wheel while a draw shows (DrawTicks from it): planned once per draw, a spin per catcher drawn,
        // each turning to its catcher and resting there. False while none shows.
        bool Wheel(in MatchState m)
        {
            int since = m.RoundTicks - m.DrawnAt;
            if (since < 0 || since >= HillMatch.DrawTicks)
                return false;
            if (m.DrawnAt != wheelDrawnAt || newRound)
            {
                if (!PlanWheel(m.DrawnAt == 0))
                    return false;
                (wheelDrawnAt, newRound, builtSpin, captionShown) = (m.DrawnAt, false, -1, -1);
            }
            if (spinCount == 0)
                return false;
            float perSpin = (float)HillMatch.DrawTicks / spinCount;
            float t = since + TickInterpolation.Alpha;
            int k = Math.Min(spinCount - 1, (int)(t / perSpin));
            Spin spin = spins[k];
            if (k != builtSpin)
            {
                builtSpin = k;
                view.BuildWheel(spin.Names, spin.Shares, spin.Colors);
            }
            float x = Math.Clamp((t - k * perSpin) / (perSpin * TurnShare), 0f, 1f);
            float eased = 1f - (1f - x) * (1f - x) * (1f - x);
            wheel.Angle = spin.Turn * eased % MathUtil.TwoPi;
            // The caption only anew when it changes: turning, then whom it landed on.
            int caption = 2 * k + (x < 1f ? 0 : 1);
            if (caption != captionShown)
            {
                captionShown = caption;
                string line = x < 1f
                    ? Loc.T(wheelDrawnAt > 0 ? "hud.free.draw_anew" : spinCount > 1 ? "hud.free.drawing_many" : "hud.free.drawing_one")
                    : spin.Chosen == Self
                        ? Loc.T(spinCount > 1 ? "hud.free.you_are_a_chaser" : "hud.free.you_are_the_chaser")
                        : Loc.F(spinCount > 1 ? "hud.free.other_is_a_chaser" : "hud.free.other_is_the_chaser", NameOf(spin.Chosen));
                view.ShowWheelCaption(line);
            }
            return true;
        }

        // The draw as the simulation made it, shown as spins: at the start every player by the weights (the
        // certain catcher first), later the runners alike; each spin without the catchers drawn before.
        bool PlanWheel(bool start)
        {
            IReadOnlyList<SimulationBody> bodies = Simulation.Bodies;
            candidates.Clear();
            spinCount = 0;
            for (int i = 0; i < bodies.Count; i++)
            {
                SimulationBody b = bodies[i];
                if (b.NetId >= NetWorld.FirstPlayerId && (start ? b.Role != FreeRole.Waiting : b.Role == FreeRole.Runner
                                                                   || b.Role == FreeRole.Catcher && !wheelCatchers.Contains(b.NetId)))
                    candidates.Add(b);
            }
            candidates.Sort((a, b) => a.NetId.CompareTo(b.NetId));
            wheelCatchers.Clear();
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].Role == FreeRole.Catcher)
                    wheelCatchers.Add(bodies[i].NetId);
            while (spinCount < spins.Length)
            {
                SimulationBody chosen = null;
                foreach (SimulationBody b in candidates)
                    if (b.Role == FreeRole.Catcher && (chosen == null || Before(b, chosen)))
                        chosen = b;
                if (chosen == null)
                    break;
                Lay(spins[spinCount++], chosen, start);
                candidates.Remove(chosen);
            }
            return spinCount > 0;

            static bool Before(SimulationBody a, SimulationBody b)
                => (a.Odds == HillMatch.Certain) != (b.Odds == HillMatch.Certain) ? a.Odds == HillMatch.Certain : a.NetId < b.NetId;
        }

        // One spin's slices over the candidates left, and the turn that brings the chosen one's slice under
        // the pointer (top), a little off its middle by its NetId, after five whole turns.
        void Lay(Spin spin, SimulationBody chosen, bool weighted)
        {
            spin.Names.Clear();
            spin.Shares.Clear();
            spin.Colors.Clear();
            spin.Chosen = chosen;
            bool certain = false;
            foreach (SimulationBody b in candidates)
                certain |= weighted && b.Odds == HillMatch.Certain;
            int total = 0;
            foreach (SimulationBody b in candidates)
                total += HillMatch.Weight(b, weighted, certain);
            double from = 0, mid = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                SimulationBody b = candidates[i];
                double share = total == 0 ? 1.0 / candidates.Count : (double)HillMatch.Weight(b, weighted, certain) / total;
                spin.Names.Add(NameOf(b));
                spin.Shares.Add(share);
                spin.Colors.Add(SliceColors[i % SliceColors.Length]);
                if (b == chosen)
                    mid = from + share * Math.PI * (1.0 + 0.6 * ((b.NetId * 7919 % 100) / 100.0 - 0.5));
                from += share * 2 * Math.PI;
            }
            spin.Turn = (float)(5 * 2 * Math.PI - mid);
        }
    }
}
