using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using KotH.Client.Settings;
using KotH.Shared.Match;

namespace KotH.Client.Menu
{
    /// <summary>The host's next round, from the lobby's Esc menu (Luca, 25.11.2026): rows edit a local
    /// copy until Apply confirms them (RememberRound); Back drops unapplied edits.</summary>
    public partial class RoundPage : UserControl
    {
        static readonly MatchMode[] Modes = { MatchMode.Solo, MatchMode.Duo, MatchMode.Trio, MatchMode.Squad, MatchMode.Mighty, MatchMode.Free };

        /// <summary>A row was edited (not yet applied); the footer updates Apply and Start round.</summary>
        public event Action Changed;

        /// <summary>Who is in the lobby now: people and all players (bots too), for the choices that fit.</summary>
        public Func<(int People, int Players)> Count { get; set; } = () => (1, 1);

        readonly List<SettingRow> rows = new List<SettingRow>();
        readonly List<MatchMode> fitting = new List<MatchMode>();
        PlayerSettings applied = ClientSettings.Applied.Clone();
        PlayerSettings edited = ClientSettings.Applied.Clone();

        public RoundPage()
        {
            InitializeComponent();
            rows.Add(new ChoiceRow<MatchMode>("Mode", _ => Fitting(), s => s.HostMode, (s, v) =>
            {
                s.HostMode = v;
                s.HostStartPercent = -1f;   // the new mode's own start damage, changeable again
            }, HillMatch.DisplayName) { NoteOf = HillMatch.ModeNote });
            rows.Add(HostPage.MaxPlayersRow());
            rows.Add(new ToggleRow("Bots", s => s.HostBots, (s, v) => s.HostBots = v));
            rows.Add(new SliderRow("Bot count", 1, HillMatch.FreeMaxPlayers - 1, 1, s => s.BotCountOf,
                                   (s, v) => s.HostBotCount = (int)Math.Round(v), v => Text(v, "0"))
                     { Locked = s => s.HostBots ? null : "", MaxOf = s => Math.Max(1, s.MaxPlayersOf - 1) });
            rows.Add(new ToggleRow("Auto start", s => s.HostAutoStart, (s, v) => s.HostAutoStart = v, "when the lobby is full"));
            rows.Add(new SliderRow("Start damage", 0, PlayerSettings.MaxStartPercent, 5, s => s.StartPercentOf,
                                   (s, v) => s.HostStartPercent = (float)Math.Round(v), v => Text(v, "0") + " %"));
            rows.Add(new SliderRow("Hit", 0, PlayerSettings.MaxHitPercent, 0.1, s => s.HostHit,
                                   (s, v) => s.HostHit = Tenths(v), Hit, "percent each hit adds"));
            rows.Add(new SliderRow("Knockback", PlayerSettings.MinKnock, PlayerSettings.MaxKnock, 0.05, s => s.HostKnock,
                                   (s, v) => s.HostKnock = (float)Math.Round(v, 2), v => "×" + Text(v, "0.00")));
            foreach (SettingRow row in rows)
            {
                row.Edited = () => edited;
                row.Changed = () =>
                {
                    Refresh();
                    Changed?.Invoke();
                };
                Rows.Children.Add(row.View);
            }
        }

        /// <summary>Shown again: from the settings in force, with the mode the lobby has now; unapplied edits are dropped.</summary>
        public void Open(MatchMode mode)
        {
            applied = ClientSettings.Applied.Clone();
            applied.HostMode = mode;
            edited = applied.Clone();
            Scroll.Offset = default;
            Refresh();
        }

        /// <summary>Whether any row differs from the applied round settings.</summary>
        public bool HasChanges
            => edited.HostMode != applied.HostMode
            || edited.MaxPlayersOf != applied.MaxPlayersOf
            || edited.HostBots != applied.HostBots
            || edited.HostBots && edited.BotCountOf != applied.BotCountOf
            || edited.HostAutoStart != applied.HostAutoStart
            || MathF.Abs(edited.StartPercentOf - applied.StartPercentOf) > 1e-3f
            || MathF.Abs(edited.HostHit - applied.HostHit) > 1e-3f
            || MathF.Abs(edited.HostKnock - applied.HostKnock) > 1e-3f;

        /// <summary>Confirms the edited settings so the lobby and next round take them.</summary>
        public void Apply()
        {
            ClientSettings.RememberRound(edited);
            applied = edited.Clone();
            Refresh();
        }

        /// <summary>Why the round cannot start now, else null: fewer than the mode's fewest (5 in 1, 2, 3, Free, else 2),
        /// people and the applied bots (counted as applied: the lobby's bodies follow a few ticks later).</summary>
        public string Problem
            => TooFew ? $"{HillMatch.DisplayName(applied.HostMode)} needs {HillMatch.FewestOf(applied.HostMode)} players: add bots or wait for more people."
                      : null;

        /// <summary>Whether too few would be in the round: checked every frame, people come and go.</summary>
        public bool TooFew => Count().People + (applied.HostBots ? applied.BotCountOf : 0) < HillMatch.FewestOf(applied.HostMode);

        void Refresh()
        {
            foreach (SettingRow row in rows)
                row.Show(edited);
        }

        // The modes the lobby's players fit in (16 only in 1, 2, 3, Free).
        List<MatchMode> Fitting()
        {
            fitting.Clear();
            int players = Count().Players;
            foreach (MatchMode m in Modes)
                if (players <= HillMatch.MaxPlayers(m) || m == edited.HostMode)
                    fitting.Add(m);
            return fitting;
        }

        /// <summary>Test aid (KOTH_SHOT_ROUND=mode): that mode chosen and applied as if by hand.</summary>
        public void Pretend(MatchMode mode)
        {
            edited.HostMode = mode;
            edited.HostStartPercent = -1f;
            Apply();
            Changed?.Invoke();
        }

        /// <summary>Test aid (KOTH_SHOT_BOTS=n): bots switched on, n of them, and applied.</summary>
        public void PretendBots(int count)
        {
            (edited.HostBots, edited.HostBotCount) = (true, count);
            Apply();
            Changed?.Invoke();
        }

        static float Tenths(double v) => (float)Math.Round(v, 1);

        static string Hit(double v) => "+" + Text(v, "0.0") + " %";

        static string Text(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);
    }
}
