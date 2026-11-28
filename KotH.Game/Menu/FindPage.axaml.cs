using System;
using System.Collections.Generic;
using Avalonia.Controls;
using KotH.Client.Localization;
using KotH.Shared.Match;
using KotH.Client.Steam;
using KotH.Shared.Net;

namespace KotH.Client.Menu
{
    /// <summary>The Find server page: the filters, what MainMenu found on Steam with them (weighted by
    /// ping, player count and lobby state, full rounds hidden), a Join per round, and what is going on
    /// (searching, joining, why not). The menu does the searching and joining; this only shows and keeps the filter.</summary>
    public partial class FindPage : UserControl
    {
        /// <summary>Refresh was chosen.</summary>
        public event Action RefreshRequested;

        /// <summary>Join was chosen on a round, with the password a private one was given (else null).</summary>
        public event Action<LobbyListing, string> JoinRequested;

        /// <summary>Join was chosen on a direct-IP invite code (checked: it parses).</summary>
        public event Action<string> InviteRequested;

        readonly MenuTextBox search = new MenuTextBox { MaxLength = 40 };
        readonly MenuTextBox invite = new MenuTextBox { MaxLength = 160 };

        readonly RowList list = new RowList();
        readonly MenuDropdown access, region, language, modded, mode;
        readonly MenuTextBox password = new MenuTextBox { Password = true, MaxLength = 64, Width = 330 };
        List<LobbyListing> allRounds = new List<LobbyListing>();
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
            MatchMode?[] modes = { null, MatchMode.Solo, MatchMode.Duo, MatchMode.Trio, MatchMode.Squad, MatchMode.Mighty, MatchMode.Free };
            access = Drop(either, () => Filter.Private, v => Filter = Filter with { Private = v },
                          v => v switch { null => Loc.T("find.filter.any_access"), true => Loc.T("find.filter.private"), false => Loc.T("find.filter.public") });
            region = Drop(Regions(), () => Filter.Region, v => Filter = Filter with { Region = v },
                          v => v == null ? Loc.T("find.filter.any_region") : v == OwnRegion ? Loc.F("find.filter.region_you", Loc.RegionName(v)) : Loc.RegionName(v));
            language = Drop(Languages(), () => Filter.Language, v => Filter = Filter with { Language = v },
                            v => v == null ? Loc.T("find.filter.any_language") : Loc.LobbyLanguageName(v));
            modded = Drop(either, () => Filter.Modded, v => Filter = Filter with { Modded = v },
                          v => v switch { null => Loc.T("find.filter.any_modded"), true => Loc.T("find.filter.modded"), false => Loc.T("find.filter.not_modded") });
            mode = Drop(modes, () => Filter.Mode, v => Filter = Filter with { Mode = v }, v => v is MatchMode m ? Loc.ModeName(m) : Loc.T("find.filter.any_mode"));
            // A press anywhere but on a drop-down closes its list (before the press does its own thing).
            AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (e.Source is Avalonia.Visual v && !Owner(v))
                    Drops.Children.Clear();
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            Refresh.Click += (_, _) => RefreshRequested?.Invoke();
            SearchField.Children.Add(search);
            search.TextChanged += _ => ApplySearch(keepPlace: false);
            ClearSearch.Click += (_, _) =>
            {
                search.Text = "";
                ApplySearch(keepPlace: false);
            };
            InviteField.Children.Add(invite);
            invite.Submitted += JoinInvited;
            JoinInvite.Click += (_, _) => JoinInvited();
            ApplyLanguage();
            Found(new List<LobbyListing>());
        }

        /// <summary>Updates all static labels, placeholders, filter heads and rows for the active language.</summary>
        public void ApplyLanguage()
        {
            SearchLabel.Text = Loc.T("find.search");
            PasswordLabel.Text = Loc.T("find.password");
            InviteLabel.Text = Loc.T("find.invite");
            Refresh.Content = Loc.T("find.refresh");
            JoinPrivate.Content = JoinInvite.Content = Loc.T("find.join");
            search.Placeholder = Loc.T("find.search.placeholder");
            invite.Placeholder = Loc.T("find.invite.placeholder");
            password.Placeholder = Loc.T("find.password.placeholder");
            Drops.Children.Clear();
            ShowFilter();
            if (!busy)
                ApplySearch(keepPlace: true);
        }

        /// <summary>No Steam: nothing to find; direct IP stays on the command line.</summary>
        public void Unavailable(string why)
        {
            busy = true;
            allRounds.Clear();
            list.Show(Array.Empty<RowList.Row>());
            EmptyTitle.Text = why;
            EmptyHint.Text = Loc.T("find.unavailable_hint");
            Empty.IsVisible = true;
            Status.Text = "";
            Refresh.IsEnabled = false;
        }

        public void Searching()
        {
            saysWhy = false;
            Status.Text = Loc.T("find.searching");
            Refresh.IsEnabled = false;
        }

        /// <summary>Clears the server-name search when Find server is opened afresh.</summary>
        public void ResetSearch()
        {
            search.Text = "";
            ClearSearch.IsVisible = false;
        }

        /// <summary>The rounds found (ranked by ping, players and lobby state); again keeps the scroll.</summary>
        public void Found(List<LobbyListing> rounds, bool again = false)
        {
            if (busy && again)
                return;
            busy = false;
            allRounds = rounds;
            ApplySearch(keepPlace: again);
            Refresh.IsEnabled = true;
        }

        const float SearchMatchWeight = 0.90f, SearchLobbyWeight = 0.10f;

        void ApplySearch(bool keepPlace)
        {
            string query = search.Text.Trim();
            ClearSearch.IsVisible = asking == null && search.Text.Length > 0;
            List<LobbyListing> shown;
            if (query.Length == 0)
                shown = allRounds;
            else
            {
                var hits = new List<(LobbyListing Round, float Match, float Total)>(allRounds.Count);
                foreach (LobbyListing round in allRounds)
                {
                    float match = NameMatch.Score(query, round.Host);
                    if (match > 0f)
                        hits.Add((round, match, match * SearchMatchWeight + SteamLobby.RankScore(round) * SearchLobbyWeight));
                }
                hits.Sort((a, b) =>
                {
                    int byTotal = b.Total.CompareTo(a.Total);
                    if (byTotal != 0)
                        return byTotal;
                    int byMatch = b.Match.CompareTo(a.Match);
                    return byMatch != 0 ? byMatch : SteamLobby.RankScore(b.Round).CompareTo(SteamLobby.RankScore(a.Round));
                });
                shown = new List<LobbyListing>(hits.Count);
                foreach ((LobbyListing round, _, _) in hits)
                    shown.Add(round);
            }

            var rows = new List<RowList.Row>(shown.Count);
            string joinLabel = Loc.T("find.join");
            foreach (LobbyListing round in shown)
            {
                LobbyListing chosen = round;
                string where = round.Regions.Length == 0 ? "" : "  " + string.Join(Loc.T("list.sep"), Array.ConvertAll(round.Regions, Loc.RegionName));
                string ping = round.PingMs < 0 ? Loc.T("common.unknown_ms") : Loc.F("common.ping_ms", Loc.Number(round.PingMs));
                string marks = (round.Mode != MatchMode.Solo ? "  " + Loc.ModeName(round.Mode) : "")
                             + (round.Private ? "  " + Loc.T("find.mark.private") : "")
                             + (round.Modded ? "  " + Loc.T("find.mark.modded") : "");
                string ratio = Loc.Ltr($"{round.Players}/{round.Capacity}");
                rows.Add(new RowList.Row(round.Host, $"{Loc.MapTitle(round.Map, round.Map)}  {ratio}{where}{marks}  {ping}", joinLabel,
                                         () => { if (!busy) Choose(chosen); }));
            }
            list.Show(rows, keepPlace: keepPlace);
            Empty.IsVisible = rows.Count == 0;
            bool filteredOut = rows.Count == 0 && query.Length > 0 && allRounds.Count > 0;
            EmptyTitle.Text = Loc.T(filteredOut ? "find.no_matching" : "find.no_servers");
            EmptyHint.Text = Loc.T(filteredOut ? "find.try_another" : "find.host_yourself");
            if (!keepPlace || !saysWhy)
                Status.Text = query.Length > 0 && allRounds.Count > 0 ? Loc.F("find.count_ratio", Loc.Number(rows.Count), Loc.Number(allRounds.Count))
                    : rows.Count == 0 ? "" : Loc.Plural("find.rounds", rows.Count);
        }

        void JoinInvited()
        {
            if (busy && invite.Text.Length == 0)
                return;
            if (!Invites.TryParse(invite.Text, out _, out _, out _))
            {
                Status.Text = Loc.T("find.bad_invite");
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

        // Shows the password prompt in place of the search, status and Refresh, or takes it away.
        void AskFor(LobbyListing? round)
        {
            asking = round;
            bool on = round != null;
            Ask.IsVisible = JoinPrivate.IsVisible = on;
            SearchLabel.IsVisible = SearchField.IsVisible = Status.IsVisible = Refresh.IsVisible = !on;
            ClearSearch.IsVisible = !on && search.Text.Length > 0;
            if (on && search.HasFocus)
                MenuTextBox.Focus(null);
            if (!on && password.HasFocus)
                MenuTextBox.Focus(null);
        }

        /// <summary>The open drop-down's sheet if one is up, else null (for gamepad focus).</summary>
        public Control OpenDrop => Drops.Children.Count > 0 ? Drops.Children[0] : null;

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

        /// <summary>Test aid (KOTH_SHOT_FIND_SEARCH): fills the search box and optionally sample lobbies.</summary>
        public void PretendSearch(string query, bool sample)
        {
            if (sample)
            {
                busy = false;
                Refresh.IsEnabled = true;
                allRounds = new List<LobbyListing>
                {
                    new(1, "Berlin Rush EU", "Mount Destiny", 10, 24, false, new[] { "eu" }, Array.Empty<string>(), false, MatchMode.Solo, 12, false),
                    new(2, "Luca's Café Server", "Atrium", 8, 19, false, new[] { "eu" }, Array.Empty<string>(), false, MatchMode.Duo, 12, false),
                    new(3, "King of the Hill #1", "Volcano", 14, 48, false, new[] { "eu" }, Array.Empty<string>(), false, MatchMode.Free, 16, true),
                    new(4, "München Fun Lobby", "Pyramid", 5, 31, true, new[] { "eu" }, Array.Empty<string>(), false, MatchMode.Solo, 12, false),
                };
            }
            search.Text = query ?? "";
            ApplySearch(keepPlace: false);
        }

        /// <summary>A round is being entered; the list waits.</summary>
        public void Joining(string host)
        {
            busy = true;
            Status.Text = Loc.F("find.joining", host);
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
