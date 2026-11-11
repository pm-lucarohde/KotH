using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Layout;
using KotH.Shared.Match;
using KotH.Client.Settings;
using KotH.Client.Steam;

namespace KotH.Client.Menu
{
    /// <summary>The Host server page: what the lobby list will say about the round (name, public or
    /// private, up to 2 regions, up to 4 languages, modded) and the password a private one asks for. Host
    /// hands the flags and the password to the menu; the rest is remembered (ClientSettings.RememberHost).</summary>
    public partial class HostPage : UserControl
    {
        /// <summary>A password needs at least this many characters.</summary>
        public const int MinPassword = 4;

        /// <summary>Host was chosen: the flags, and the password for a private round (else null).</summary>
        public event Action<LobbyFlags, string> HostRequested;

        readonly List<SettingRow> rows = new List<SettingRow>();
        readonly MenuTextBox name = new MenuTextBox { MaxLength = 32, Width = 380 };
        readonly MenuTextBox password = new MenuTextBox { MaxLength = 64, Width = 380, Password = true, Placeholder = "At least 4 characters" };
        PlayerSettings edited = ClientSettings.Applied.Clone();
        string fallbackName = "My round";

        public HostPage()
        {
            InitializeComponent();
            name.TextChanged += value => edited.HostName = value.Trim();
            password.TextChanged += _ => Refresh();
            rows.Add(new FieldRow("Name", name));
            rows.Add(new ChoiceRow<MatchMode>("Mode", _ => new[] { MatchMode.Solo, MatchMode.Duo, MatchMode.Trio, MatchMode.Squad },
                                              s => s.HostMode, (s, v) => s.HostMode = v, v => v.ToString(),
                                              "teams of 2, 3 or 4, chained in a row"));
            rows.Add(new ChoiceRow<bool>("Access", _ => new[] { false, true }, s => s.HostPrivate, (s, v) => s.HostPrivate = v,
                                         v => v ? "Private" : "Public"));
            rows.Add(new FieldRow("Password", password, "private only") { Locked = s => s.HostPrivate ? null : "" });
            rows.Add(new ToggleRow("Modded", s => s.HostModded, (s, v) => s.HostModded = v, "a label for now"));
            rows.Add(new HeadingRow("Regions", $"where the players are, up to {LobbyFlags.MaxRegions}"));
            rows.Add(new ChipsRow(LobbyFlags.AllRegions, LobbyFlags.MaxRegions, s => s.HostRegions, (s, v) => s.HostRegions = v));
            rows.Add(new HeadingRow("Languages", $"spoken in the round, up to {LobbyFlags.MaxLanguages}"));
            rows.Add(new ChipsRow(LobbyFlags.AllLanguages, LobbyFlags.MaxLanguages, s => s.HostLanguages, (s, v) => s.HostLanguages = v));
            foreach (SettingRow row in rows)
            {
                row.Edited = () => edited;
                row.Changed = Refresh;
                Rows.Children.Add(row.View);
            }
            Start.Click += (_, _) => Host();
        }

        /// <summary>Shown again: the remembered choices, Steam's guesses where there are none, no password.</summary>
        public void Open(string playerName)
        {
            edited = ClientSettings.Applied.Clone();
            fallbackName = $"{playerName}'s round";
            if (edited.HostRegions.Length == 0)
                edited.HostRegions = new[] { LobbyFlags.RegionOf(SteamClient.IpCountry, TimeZoneInfo.Local.BaseUtcOffset.TotalHours) };
            if (edited.HostLanguages.Length == 0)
                edited.HostLanguages = new[] { SteamClient.Language };
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
            Status.Text = why ?? (edited.HostPrivate ? "Players need the password; friends you invite do not." : "");
        }

        string Problem()
            => edited.HostPrivate && password.Text.Length < MinPassword ? $"A private round needs a password of {MinPassword} characters or more."
             : edited.HostRegions.Length == 0 ? "Choose at least one region."
             : edited.HostLanguages.Length == 0 ? "Choose at least one language."
             : null;

        void Host()
        {
            if (Problem() != null)
                return;
            MenuTextBox.Focus(null);
            ClientSettings.RememberHost(edited);
            string shown = edited.HostName.Length > 0 ? edited.HostName : fallbackName;
            var flags = new LobbyFlags(shown, edited.HostPrivate, (string[])edited.HostRegions.Clone(), (string[])edited.HostLanguages.Clone(), edited.HostModded, edited.HostMode);
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
            readonly Dictionary<string, Border> byCode = new Dictionary<string, Border>();
            readonly Func<PlayerSettings, string[]> get;
            readonly int max;

            public ChipsRow((string Code, string Name)[] values, int max, Func<PlayerSettings, string[]> get, Action<PlayerSettings, string[]> set)
                : base("", null)
            {
                (this.get, this.max) = (get, max);
                foreach ((string code, string title) in values)
                {
                    var chip = new Border
                    {
                        Classes = { "Tab" }, Child = new TextBlock { Text = title }, Margin = new Avalonia.Thickness(0, 0, 10, 12),
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    };
                    chip.PointerPressed += (_, _) => Change(s =>
                    {
                        var chosen = new List<string>(get(s));
                        if (!chosen.Remove(code) && chosen.Count < max)
                            chosen.Add(code);
                        set(s, chosen.ToArray());
                    });
                    byCode[code] = chip;
                    chips.Children.Add(chip);
                }
                var grid = (Grid)View;
                grid.Height = double.NaN;
                grid.Children.Clear();
                grid.Children.Add(chips);
                Grid.SetColumnSpan(chips, 2);
            }

            protected override void Fill(PlayerSettings s, string reason)
            {
                string[] chosen = get(s);
                foreach ((string code, Border chip) in byCode)
                {
                    bool on = Array.IndexOf(chosen, code) >= 0;
                    chip.Classes.Set("Chosen", on);
                    chip.Classes.Set("Off", !on && chosen.Length >= max);
                }
            }
        }
    }
}
