using System;
using System.Collections.Generic;
using Avalonia.Controls;
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
        readonly List<string> countriesSeen = new List<string>();
        readonly Border access, country, language, modded, mode;
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
            access = Pill(step => Filter = Filter with { Private = Next(new bool?[] { null, false, true }, Filter.Private, step) });
            country = Pill(step => Filter = Filter with { Country = Next(Countries(), Filter.Country, step) });
            language = Pill(step => Filter = Filter with { Language = Next(Languages(), Filter.Language, step) });
            modded = Pill(step => Filter = Filter with { Modded = Next(new bool?[] { null, false, true }, Filter.Modded, step) });
            mode = Pill(step => Filter = Filter with { Duo = Next(new bool?[] { null, false, true }, Filter.Duo, step) });
            ShowFilter();
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
                if (round.Country.Length == 2 && !countriesSeen.Contains(round.Country))
                    countriesSeen.Add(round.Country);
                string ping = round.PingMs < 0 ? "? ms" : $"{round.PingMs} ms";
                string marks = (round.Duo ? "  duo" : "") + (round.Private ? "  private" : "") + (round.Modded ? "  modded" : "");
                rows.Add(new RowList.Row(round.Host, $"{round.Map}  {round.Players}/{NetWorld.MaxPlayers}  {round.Country}{marks}  {ping}", "Join",
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

        /// <summary>Escape: closes the password prompt; false when none was open.</summary>
        public bool Cancel()
        {
            if (asking == null)
                return false;
            AskFor(null);
            return true;
        }

        // A filter as a pill: a click steps it on, a right click back; then the list searches again.
        Border Pill(Action<int> step)
        {
            var pill = new Border
            {
                Classes = { "Tab" }, Child = new TextBlock(), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            pill.PointerPressed += (_, e) =>
            {
                step(e.GetCurrentPoint(pill).Properties.IsRightButtonPressed ? -1 : 1);
                ShowFilter();
                RefreshRequested?.Invoke();
            };
            Filters.Children.Add(pill);
            return pill;
        }

        void ShowFilter()
        {
            Say(access, Filter.Private switch { null => "Any access", true => "Private", false => "Public" });
            Say(country, Filter.Country == null ? "Any country" : LobbyFlags.CountryName(Filter.Country));
            Say(language, Filter.Language == null ? "Any language" : LobbyFlags.LanguageName(Filter.Language));
            Say(modded, Filter.Modded switch { null => "Modded or not", true => "Modded", false => "Not modded" });
            access.Classes.Set("Chosen", Filter.Private != null);
            country.Classes.Set("Chosen", Filter.Country != null);
            language.Classes.Set("Chosen", Filter.Language != null);
            modded.Classes.Set("Chosen", Filter.Modded != null);
            Say(mode, Filter.Duo switch { null => "Solo or duo", true => "Duo", false => "Solo" });
            mode.Classes.Set("Chosen", Filter.Duo != null);
        }

        static void Say(Border pill, string text) => ((TextBlock)pill.Child).Text = text;

        // Any, the own country, then those the lists showed.
        IReadOnlyList<string> Countries()
        {
            var list = new List<string> { null };
            if (SteamClient.IpCountry.Length == 2)
                list.Add(SteamClient.IpCountry);
            foreach (string c in countriesSeen)
                if (!list.Contains(c))
                    list.Add(c);
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

        static T Next<T>(IReadOnlyList<T> values, T now, int step)
        {
            int at = 0;
            for (int i = 0; i < values.Count; i++)
                if (EqualityComparer<T>.Default.Equals(values[i], now))
                    at = i;
            return values[(at + step + values.Count) % values.Count];
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
