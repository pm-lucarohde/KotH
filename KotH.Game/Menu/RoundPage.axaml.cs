using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using KotH.Client.Settings;
using KotH.Shared.Match;

namespace KotH.Client.Menu
{
    /// <summary>The host's next round, from the lobby's Esc menu (Luca, 25.11.2026): every change is remembered
    /// and goes to the lobby at once (Changed); the mode and the start show there, the values count from the round.</summary>
    public partial class RoundPage : UserControl
    {
        static readonly MatchMode[] Modes = { MatchMode.Solo, MatchMode.Duo, MatchMode.Trio, MatchMode.Squad, MatchMode.Mighty, MatchMode.Free };

        /// <summary>A change was made (remembered already); the lobby takes it.</summary>
        public event Action Changed;

        /// <summary>Who is in the lobby now: people and all players (bots too), for the choices that fit.</summary>
        public Func<(int People, int Players)> Count { get; set; } = () => (1, 1);

        readonly List<SettingRow> rows = new List<SettingRow>();
        readonly List<MatchMode> fitting = new List<MatchMode>();
        PlayerSettings edited = ClientSettings.Applied.Clone();

        public RoundPage()
        {
            InitializeComponent();
            rows.Add(new ChoiceRow<MatchMode>("Mode", _ => Fitting(), s => s.HostMode, (s, v) =>
            {
                s.HostMode = v;
                s.HostStartPercent = -1f;   // the new mode's own start damage, changeable again
            }, HillMatch.DisplayName) { NoteOf = HillMatch.ModeNote });
            rows.Add(new SliderRow("Start damage", 0, PlayerSettings.MaxStartPercent, 5, s => s.StartPercentOf,
                                   (s, v) => s.HostStartPercent = (float)Math.Round(v), v => Text(v, "0") + " %", "everyone, at the start"));
            rows.Add(new SliderRow("Cane hit", 0, PlayerSettings.MaxHitPercent, 0.1, s => s.HostMelee,
                                   (s, v) => s.HostMelee = Tenths(v), Hit, "percent it adds"));
            rows.Add(new SliderRow("Spore hit", 0, PlayerSettings.MaxHitPercent, 0.1, s => s.HostSpore,
                                   (s, v) => s.HostSpore = Tenths(v), Hit));
            rows.Add(new SliderRow("Dash hit", 0, PlayerSettings.MaxHitPercent, 0.1, s => s.HostDash,
                                   (s, v) => s.HostDash = Tenths(v), Hit));
            rows.Add(new SliderRow("Knockback", PlayerSettings.MinKnock, PlayerSettings.MaxKnock, 0.05, s => s.HostKnock,
                                   (s, v) => s.HostKnock = (float)Math.Round(v, 2), v => "×" + Text(v, "0.00"), "how far hits throw"));
            rows.Add(new ToggleRow("Bots", s => s.HostBots, (s, v) => s.HostBots = v, "they join the lobby now"));
            rows.Add(new SliderRow("Bot count", 1, HillMatch.FreeMaxPlayers - 1, 1, s => s.HostBotCount,
                                   (s, v) => s.HostBotCount = (int)Math.Round(v), v => Text(v, "0")) { Locked = s => s.HostBots ? null : "" });
            rows.Add(new ToggleRow("Auto start", s => s.HostAutoStart, (s, v) => s.HostAutoStart = v, "bots do not count"));
            rows.Add(new SliderRow("Start at", 1, HillMatch.FreeMaxPlayers, 1, s => s.HostMinPlayers,
                                   (s, v) => s.HostMinPlayers = (int)Math.Round(v), v => Text(v, "0") + " people")
                     { Locked = s => s.HostAutoStart ? null : "" });
            foreach (SettingRow row in rows)
            {
                row.Edited = () => edited;
                row.Changed = () =>
                {
                    ClientSettings.RememberRound(edited);
                    Refresh();
                    Changed?.Invoke();
                };
                Rows.Children.Add(row.View);
            }
        }

        /// <summary>Shown again: the remembered values, with the mode the lobby has now.</summary>
        public void Open(MatchMode mode)
        {
            edited = ClientSettings.Applied.Clone();
            edited.HostMode = mode;
            Scroll.Offset = default;
            Refresh();
        }

        /// <summary>Why the round cannot start now, else null: fewer than the mode's fewest (5 in 1, 2, 3, Free, else 2),
        /// people and the bots chosen (counted as chosen: the lobby's bodies follow a few ticks later).</summary>
        public string Problem
            => Count().People + (edited.HostBots ? edited.HostBotCount : 0) is var n && n < HillMatch.FewestOf(edited.HostMode)
               ? $"{HillMatch.DisplayName(edited.HostMode)} needs {HillMatch.FewestOf(edited.HostMode)} players: add bots or wait for more people."
               : null;

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

        /// <summary>Test aid (KOTH_SHOT_ROUND=mode): that mode chosen as if by the arrows.</summary>
        public void Pretend(MatchMode mode)
        {
            edited.HostMode = mode;
            edited.HostStartPercent = -1f;
            ClientSettings.RememberRound(edited);
            Refresh();
            Changed?.Invoke();
        }

        static float Tenths(double v) => (float)Math.Round(v, 1);

        static string Hit(double v) => "+" + Text(v, "0.0") + " %";

        static string Text(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);
    }
}
