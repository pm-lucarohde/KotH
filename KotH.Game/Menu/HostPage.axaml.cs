using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Layout;
using KotH.Client.Localization;
using KotH.Shared.Match;
using KotH.Client.Settings;
using KotH.Client.Steam;

namespace KotH.Client.Menu
{
    /// <summary>The Host server page: what the lobby list will say about the round (name, public or
    /// private, up to 2 regions, up to 2 languages, modded) and the password a private one asks for. Host
    /// hands the flags and the password to the menu; the rest is remembered (ClientSettings.RememberHost).</summary>
    public partial class HostPage : UserControl
    {
        /// <summary>A password needs at least this many characters.</summary>
        public const int MinPassword = 4;

        /// <summary>Host was chosen: the flags, and the password for a private round (else null).</summary>
        public event Action<LobbyFlags, string> HostRequested;

        readonly List<SettingRow> rows = new List<SettingRow>();
        readonly MenuTextBox name = new MenuTextBox { MaxLength = 32, Width = 380 };
        readonly MenuTextBox password = new MenuTextBox { MaxLength = 64, Width = 380, Password = true };
        readonly ChipsRow regionChips, languageChips;
        PlayerSettings edited = ClientSettings.Applied.Clone();
        string fallbackName, lastPlayer;

        public HostPage()
        {
            InitializeComponent();
            name.TextChanged += value => edited.HostName = value.Trim();
            password.TextChanged += _ => Refresh();
            regionChips = new ChipsRow(LobbyFlags.AllRegions, LobbyFlags.MaxRegions, s => s.HostRegions, (s, v) => s.HostRegions = v, Loc.RegionName);
            languageChips = new ChipsRow(LobbyFlags.AllLanguages, LobbyFlags.MaxLanguages, s => s.HostLanguages, (s, v) => s.HostLanguages = v, Loc.LobbyLanguageName);
            rows.Add(new FieldRow("", name));
            rows.Add(new ChoiceRow<MatchMode>("", _ => new[] { MatchMode.Solo, MatchMode.Duo, MatchMode.Trio, MatchMode.Squad, MatchMode.Mighty, MatchMode.Free },
                                              s => s.HostMode, (s, v) => s.HostMode = v, Loc.ModeName)
                     { NoteOf = Loc.ModeNote });
            rows.Add(MaxPlayersRow());
            rows.Add(new ChoiceRow<bool>("", _ => new[] { false, true }, s => s.HostPrivate, (s, v) => s.HostPrivate = v,
                                         v => Loc.T(v ? "host.access.private" : "host.access.public")));
            rows.Add(new FieldRow("", password, "") { Locked = s => s.HostPrivate ? null : "" });
            rows.Add(new ToggleRow("", s => s.HostModded, (s, v) => s.HostModded = v));
            rows.Add(new HeadingRow("", ""));
            rows.Add(regionChips);
            rows.Add(new HeadingRow("", ""));
            rows.Add(languageChips);
            foreach (SettingRow row in rows)
            {
                row.Edited = () => edited;
                row.Changed = Refresh;
                Rows.Children.Add(row.View);
            }
            Start.Click += (_, _) => Host();
            ApplyLanguage();
        }

        /// <summary>Updates all labels, placeholders and chips for the active language.</summary>
        public void ApplyLanguage()
        {
            Start.Content = Loc.T("host.start");
            password.Placeholder = Loc.T("host.password.placeholder");
            fallbackName = lastPlayer != null ? Loc.F("menu.player_round", lastPlayer) : Loc.T("host.my_round");
            name.Placeholder = fallbackName;
            rows[0].Retext(Loc.T("host.name"));
            rows[1].Retext(Loc.T("host.mode"));
            rows[2].Retext(Loc.T("host.max_players"));
            rows[3].Retext(Loc.T("host.access"));
            rows[4].Retext(Loc.T("host.password"), Loc.T("host.password.note"));
            rows[5].Retext(Loc.T("host.modded"));
            rows[6].Retext(Loc.T("host.regions"), Loc.F("host.regions.note", LobbyFlags.MaxRegions));
            rows[8].Retext(Loc.T("host.languages"), Loc.F("host.languages.note", LobbyFlags.MaxLanguages));
            regionChips.RefreshLabels();
            languageChips.RefreshLabels();
            Refresh();
        }

        /// <summary>Shown again: the remembered choices, Steam's guesses where there are none, no password.</summary>
        public void Open(string playerName)
        {
            edited = ClientSettings.Applied.Clone();
            lastPlayer = playerName;
            fallbackName = Loc.F("menu.player_round", playerName);
            if (edited.HostRegions.Length == 0)
                edited.HostRegions = new[] { LobbyFlags.RegionOf(SteamClient.IpCountry, TimeZoneInfo.Local.BaseUtcOffset.TotalHours) };
            if (edited.HostLanguages.Length == 0)
                edited.HostLanguages = new[] { SteamClient.Language };
            else if (edited.HostLanguages.Length > LobbyFlags.MaxLanguages)
                edited.HostLanguages = edited.HostLanguages[..LobbyFlags.MaxLanguages];
            name.Text = edited.HostName;
            name.Placeholder = fallbackName;
            password.Text = "";
            Scroll.Offset = default;
            Refresh();
        }

        void Refresh()
        {
            foreach (SettingRow row in rows)
                row.Show(edited);
            string why = Problem();
            Start.IsEnabled = why == null;
            Status.Text = why ?? (edited.HostPrivate ? Loc.T("host.private_hint") : "");
        }

        string Problem()
            => edited.HostPrivate && password.Text.Length < MinPassword ? Loc.F("host.err.password", MinPassword)
             : edited.HostRegions.Length == 0 ? Loc.T("host.err.region")
             : edited.HostLanguages.Length == 0 ? Loc.T("host.err.language")
             : null;

        void Host()
        {
            if (Problem() != null)
                return;
            MenuTextBox.Focus(null);
            ClientSettings.RememberHost(edited);
            string shown = edited.HostName.Length > 0 ? edited.HostName : fallbackName;
            var flags = new LobbyFlags(shown, edited.HostPrivate, (string[])edited.HostRegions.Clone(), (string[])edited.HostLanguages.Clone(), edited.HostModded,
                                       edited.HostMode, edited.MaxPlayersOf);
            HostRequested?.Invoke(flags, edited.HostPrivate ? password.Text : null);
        }

        /// <summary>Test aid (KOTH_SHOT_HOST_PRIVATE): a private round with this password.</summary>
        public void Pretend(string pass)
        {
            edited.HostPrivate = true;
            password.Text = pass;
            Refresh();
            // KOTH_SHOT_HOST_SCROLL: the rows further down (languages).
            if (Environment.GetEnvironmentVariable("KOTH_SHOT_HOST_SCROLL") == "1")
                Scroll.Offset = new Avalonia.Vector(0, 400);
        }

        /// <summary>The most players on the server: up to 16 in 1, 2, 3, Free, else 12 (Luca); at least the mode's fewest.</summary>
        public static SliderRow MaxPlayersRow()
            => new SliderRow(Loc.T("host.max_players"), 2, HillMatch.FreeMaxPlayers, 1, s => s.MaxPlayersOf,
                             (s, v) => s.ChooseMaxPlayers(Math.Max((int)Math.Round(v), HillMatch.FewestOf(s.HostMode))),
                             v => Loc.Number((int)Math.Round(v)))
               { MaxOf = s => HillMatch.MaxPlayers(s.HostMode) };

        /// <summary>A text field as a settings row.</summary>
        sealed class FieldRow : SettingRow
        {
            public FieldRow(string title, MenuTextBox box, string note = null) : base(title, note) => Place(box);

            protected override void Fill(PlayerSettings s, string reason) { }
        }

        /// <summary>Regions or languages as chips under their heading, up to max of them chosen (the rest greyed then).</summary>
        sealed class ChipsRow : SettingRow
        {
            readonly WrapPanel chips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Avalonia.Thickness(0, 4, 0, 12) };
            readonly Dictionary<string, (Border Chip, TextBlock Label)> byCode = new Dictionary<string, (Border, TextBlock)>();
            readonly Func<PlayerSettings, string[]> get;
            readonly Func<string, string> labelOf;
            readonly int max;

            public ChipsRow((string Code, string Name)[] values, int max, Func<PlayerSettings, string[]> get,
                            Action<PlayerSettings, string[]> set, Func<string, string> labelOf)
                : base("", null)
            {
                (this.get, this.max, this.labelOf) = (get, max, labelOf);
                foreach ((string code, _) in values)
                {
                    Action toggle = () => Change(s =>
                    {
                        var chosen = new List<string>(get(s));
                        if (!chosen.Remove(code) && chosen.Count < max)
                            chosen.Add(code);
                        set(s, chosen.ToArray());
                    });
                    var text = new TextBlock { Text = labelOf(code) };
                    var chip = new Border
                    {
                        Classes = { "Tab" }, Child = text, Margin = new Avalonia.Thickness(0, 0, 10, 12),
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), Tag = toggle,
                    };
                    chip.PointerPressed += (_, _) => toggle();
                    byCode[code] = (chip, text);
                    chips.Children.Add(chip);
                }
                Line.Height = double.NaN;
                Line.Children.Clear();
                Line.Children.Add(chips);
                Grid.SetColumnSpan(chips, 2);
            }

            public void RefreshLabels()
            {
                foreach ((string code, (_, TextBlock label)) in byCode)
                    label.Text = labelOf(code);
            }

            protected override void Fill(PlayerSettings s, string reason)
            {
                string[] chosen = get(s);
                foreach ((string code, (Border chip, _)) in byCode)
                {
                    bool on = Array.IndexOf(chosen, code) >= 0;
                    chip.Classes.Set("Chosen", on);
                    chip.Classes.Set("Off", !on && chosen.Length >= max);
                }
            }
        }
    }
}
