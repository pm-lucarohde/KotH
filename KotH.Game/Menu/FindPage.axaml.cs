using System;
using System.Collections.Generic;
using Avalonia.Controls;
using KotH.Shared.Match;
using KotH.Client.Steam;
using KotH.Shared.Net;

namespace KotH.Client.Menu
{
    /// <summary>The Find server page: the filters, what MainMenu found on Steam with them (nearest first),
    /// a Join per round, and what is going on (searching, joining, why not). The menu does the searching
    /// and joining; this only shows and keeps the filter.</summary>
    public partial class FindPage : UserControl
    {
        /// <summary>Refresh was chosen.</summary>
        public event Action RefreshRequested;

        /// <summary>Join was chosen on a round, with the password a private one was given (else null).</summary>
        public event Action<LobbyListing, string> JoinRequested;

        /// <summary>Join was chosen on a direct-IP invite code (checked: it parses).</summary>
        public event Action<string> InviteRequested;

        readonly MenuTextBox invite = new MenuTextBox { MaxLength = 160, Placeholder = "koth://join?… from the host, Ctrl+V pastes" };

        readonly RowList list = new RowList();
        readonly MenuDropdown access, region, language, modded, mode;
        readonly MenuTextBox password = new MenuTextBox { Password = true, MaxLength = 64, Width = 330, Placeholder = "Type it, Enter joins" };
        LobbyListing? asking;
        bool busy, saysWhy;

        /// <summary>What to ask Steam for.</summary>
        public LobbyFilter Filter { get; private set; } = LobbyFilter.Any;

        public FindPage()
        {
            InitializeComponent();
            Inside.Children.Add(list);
            Ask.Children.Add(password);
            password.Submitted += JoinAsked;
            JoinPrivate.Click += (_, _) => JoinAsked();
            bool?[] either = { null, false, true };
            MatchMode?[] modes = { null, MatchMode.Solo, MatchMode.Duo, MatchMode.Trio, MatchMode.Squad };
            access = Drop(either, () => Filter.Private, v => Filter = Filter with { Private = v },
                          v => v switch { null => "Any access", true => "Private", false => "Public" });
            region = Drop(Regions(), () => Filter.Region, v => Filter = Filter with { Region = v },
                          v => v == null ? "Any region" : LobbyFlags.RegionName(v) + (v == OwnRegion ? " (you)" : ""));
            language = Drop(Languages(), () => Filter.Language, v => Filter = Filter with { Language = v },
                            v => v == null ? "Any language" : LobbyFlags.LanguageName(v));
            modded = Drop(either, () => Filter.Modded, v => Filter = Filter with { Modded = v },
                          v => v switch { null => "Modded or not", true => "Modded", false => "Not modded" });
            mode = Drop(modes, () => Filter.Mode, v => Filter = Filter with { Mode = v }, v => v?.ToString() ?? "Any mode");
            ShowFilter();
            // A press anywhere but on a drop-down closes its list (before the press does its own thing).
            AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (e.Source is Avalonia.Visual v && !Owner(v))
                    Drops.Children.Clear();
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            Refresh.Click += (_, _) => RefreshRequested?.Invoke();
            InviteField.Children.Add(invite);
            invite.Submitted += JoinInvited;
            JoinInvite.Click += (_, _) => JoinInvited();
            Found(new List<LobbyListing>());
        }

        /// <summary>No Steam: nothing to find; direct IP stays on the command line.</summary>
        public void Unavailable(string why)
        {
            busy = true;
            list.Show(Array.Empty<RowList.Row>());
            EmptyTitle.Text = why;
            EmptyHint.Text = "Start Steam and KotH again, or join with a host's invite code below.";
            Empty.IsVisible = true;
            Status.Text = "";
            Refresh.IsEnabled = false;
        }

        public void Searching()
        {
            saysWhy = false;
            Status.Text = "Searching …";
            Refresh.IsEnabled = false;
        }

        /// <summary>The rounds found, fullest first; again (a search on its own) keeps the scroll and a word on joining.</summary>
        public void Found(List<LobbyListing> rounds, bool again = false)
        {
            if (busy && again)
                return;
            busy = false;
            var rows = new List<RowList.Row>();
            foreach (LobbyListing round in rounds)
            {
                LobbyListing chosen = round;
                string where = round.Regions.Length == 0 ? "" : "  " + string.Join(", ", Array.ConvertAll(round.Regions, LobbyFlags.RegionName));
                string ping = round.PingMs < 0 ? "? ms" : $"{round.PingMs} ms";
                string marks = (round.Mode != MatchMode.Solo ? "  " + HillMatch.ModeName(round.Mode) : "") + (round.Private ? "  private" : "") + (round.Modded ? "  modded" : "");
                rows.Add(new RowList.Row(round.Host, $"{round.Map}  {round.Players}/{NetWorld.MaxPlayers}{where}{marks}  {ping}", "Join",
                                         () => { if (!busy) Choose(chosen); }));
            }
            list.Show(rows, keepPlace: again);
            Empty.IsVisible = rows.Count == 0;
            EmptyTitle.Text = "No servers found";
            EmptyHint.Text = "Host one yourself, or ask a friend for an invite.";
            if (!again || !saysWhy)
                Status.Text = rows.Count == 0 ? "" : rows.Count == 1 ? "1 round" : $"{rows.Count} rounds";
            Refresh.IsEnabled = true;
        }

        void JoinInvited()
        {
            if (busy && invite.Text.Length == 0)
                return;
            if (!Invites.TryParse(invite.Text, out _, out _, out _))
            {
                Status.Text = "That is no invite code: koth://join?ip=…&port=…&key=…";
                saysWhy = true;
                return;
            }
            InviteRequested?.Invoke(invite.Text.Trim());
        }

        // A private round asks for its password first; an open one joins at once.
        void Choose(LobbyListing round)
        {
            if (!round.Private)
            {
                AskFor(null);
                JoinRequested?.Invoke(round, null);
                return;
            }
            AskFor(round);
            password.Text = "";
            MenuTextBox.Focus(password);
        }

        void JoinAsked()
        {
            if (asking is not LobbyListing round || password.Text.Length == 0 || busy)
                return;
            string typed = password.Text;
            AskFor(null);
            JoinRequested?.Invoke(round, typed);
        }

        // Shows the password prompt in place of the status and Refresh, or takes it away.
        void AskFor(LobbyListing? round)
        {
            asking = round;
            bool on = round != null;
            Ask.IsVisible = JoinPrivate.IsVisible = on;
            Status.IsVisible = Refresh.IsVisible = !on;
            if (!on && password.HasFocus)
                MenuTextBox.Focus(null);
        }

        /// <summary>Escape: closes an open drop-down or the password prompt; false when neither was open.</summary>
        public bool Cancel()
        {
            if (Drops.Children.Count > 0)
            {
                Drops.Children.Clear();
                return true;
            }
            if (asking == null)
                return false;
            AskFor(null);
            return true;
        }

        // A filter as a drop-down over the values; a choice searches again.
        MenuDropdown Drop<T>(IReadOnlyList<T> values, Func<T> get, Action<T> set, Func<T, string> text)
        {
            MenuDropdown drop = null;
            drop = new MenuDropdown(Drops, () => Array.ConvertAll(Values(values), v => text(v)), () => IndexOf(values, get()), i =>
            {
                set(values[i]);
                ShowFilter();
                RefreshRequested?.Invoke();
            });
            drops.Add((drop, () => (text(get()), get() != null)));
            Filters.Children.Add(drop.Head);
            return drop;
        }

        readonly List<(MenuDropdown Drop, Func<(string, bool)> Shown)> drops = new List<(MenuDropdown, Func<(string, bool)>)>();

        bool Owner(Avalonia.Visual v)
        {
            foreach ((MenuDropdown drop, _) in drops)
                if (drop.Owns(v))
                    return true;
            return false;
        }

        void ShowFilter()
        {
            foreach ((MenuDropdown drop, Func<(string, bool)> shown) in drops)
            {
                (string text, bool set) = shown();
                drop.Show(text, set);
            }
        }

        static T[] Values<T>(IReadOnlyList<T> values)
        {
            var copy = new T[values.Count];
            for (int i = 0; i < copy.Length; i++)
                copy[i] = values[i];
            return copy;
        }

        static int IndexOf<T>(IReadOnlyList<T> values, T value)
        {
            for (int i = 0; i < values.Count; i++)
                if (EqualityComparer<T>.Default.Equals(values[i], value))
                    return i;
            return -1;
        }

        // The own region by Steam's country and the PC's time zone.
        static string OwnRegion => LobbyFlags.RegionOf(SteamClient.IpCountry, TimeZoneInfo.Local.BaseUtcOffset.TotalHours);

        // Any, the own region, then the others.
        static IReadOnlyList<string> Regions()
        {
            var list = new List<string> { null, OwnRegion };
            foreach ((string code, _) in LobbyFlags.AllRegions)
                if (!list.Contains(code))
                    list.Add(code);
            return list;
        }

        // Any, the own language, then every other.
        static IReadOnlyList<string> Languages()
        {
            var list = new List<string> { null, SteamClient.Language };
            foreach ((string code, _) in LobbyFlags.AllLanguages)
                if (!list.Contains(code))
                    list.Add(code);
            return list;
        }

        /// <summary>Test aid (KOTH_SHOT_FIND_DROP): opens filter n's list once the page is laid out.</summary>
        public void Pretend(int n)
        {
            if (n < 0 || n >= drops.Count)
                return;
            void Laid(object sender, EventArgs e)
            {
                if (drops[n].Drop.Head.Bounds.Width <= 0)
                    return;
                LayoutUpdated -= Laid;
                drops[n].Drop.Open();
            }
            LayoutUpdated += Laid;
        }

        /// <summary>A round is being entered; the list waits.</summary>
        public void Joining(string host)
        {
            busy = true;
            Status.Text = $"Joining {host} …";
            Refresh.IsEnabled = false;
        }

        /// <summary>Entering failed: why, and the list is usable again.</summary>
        public void Failed(string why)
        {
            busy = false;
            saysWhy = true;
            Status.Text = why;
            Refresh.IsEnabled = true;
        }
    }
}
