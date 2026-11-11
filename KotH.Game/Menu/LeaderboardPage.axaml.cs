using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using KotH.Client.Steam;

namespace KotH.Client.Menu
{
    /// <summary>The Leaderboard tab: the wins board's top ten (SteamStats), globally or among friends, and
    /// the own row. Names Steam does not know yet come later: Refresh redraws when SteamStats' counters move.</summary>
    public partial class LeaderboardPage : UserControl
    {
        public const int Shown = 10;

        bool friends, sample;
        int asked, built = -1;
        List<SteamStats.Entry> rows;
        SteamStats.Entry? own;

        public LeaderboardPage()
        {
            InitializeComponent();
            GlobalTab.PointerPressed += (_, _) => Choose(false);
            FriendsTab.PointerPressed += (_, _) => Choose(true);
            Choose(false, load: false);
        }

        /// <summary>Test aid (KOTH_SHOT_SOCIAL=sample): ten made-up rows and the own one at rank 132, no Steam.</summary>
        public void Sample()
        {
            sample = true;
            rows = new List<SteamStats.Entry>();
            for (int i = 0; i < Shown; i++)
                rows.Add(new SteamStats.Entry(i + 1, (ulong)(i + 1), 412 - 37 * i));
            own = new SteamStats.Entry(132, 0, 4);
            built = -1;
            Refresh();
        }

        /// <summary>Opened: the board anew from Steam.</summary>
        public void Open()
        {
            if (sample)
                return;
            Rows.Children.Clear();
            rows = null;
            own = null;
            Status.Text = "";
            if (!SteamClient.Ready)
            {
                Empty.Text = "The leaderboard needs Steam";
                Empty.IsVisible = true;
                return;
            }
            Empty.Text = "Loading…";
            Empty.IsVisible = true;
            int call = ++asked;
            SteamStats.Top(friends, Shown, (top, mine) =>
            {
                if (call != asked)
                    return;
                if (top == null)
                {
                    Empty.Text = "Steam did not answer";
                    return;
                }
                rows = top;
                own = mine;
                built = -1;
                Refresh();
            });
        }

        /// <summary>Redraws when names or the own values came in since.</summary>
        public void Refresh()
        {
            if (rows == null || built == SteamStats.Version + SteamStats.NamesLearned && !sample)
                return;
            built = SteamStats.Version + SteamStats.NamesLearned;
            Rows.Children.Clear();
            Empty.IsVisible = rows.Count == 0;
            Empty.Text = friends ? "None of your friends has won yet" : "No wins yet: be the first!";
            if (rows.Count > 0)
                Rows.Children.Add(Line("#", "Player", "Wins", false, heading: true));
            bool listed = false;
            for (int i = 0; i < rows.Count; i++)
            {
                SteamStats.Entry e = rows[i];
                bool self = e.Id == SteamClient.SteamId;
                listed |= self;
                Rows.Children.Add(Line(Number(friends ? i + 1 : e.Rank), NameOf(e.Id), Number(e.Score), self));
            }
            if (!listed && own is { } o && rows.Count > 0 && !friends)
            {
                Border mine = Line(Number(o.Rank), NameOf(o.Id), Number(o.Score), true);
                mine.Margin = new Avalonia.Thickness(0, 10, 0, 0);
                Rows.Children.Add(mine);
            }
            Status.Text = own is { Score: > 0 } me
                ? string.Format(CultureInfo.InvariantCulture, "You: rank {0} worldwide with {1} {2}", me.Rank, me.Score, me.Score == 1 ? "win" : "wins")
                : "Win an online round to get on the board.";
        }

        void Choose(bool toFriends, bool load = true)
        {
            friends = toFriends;
            GlobalTab.Classes.Set("Chosen", !friends);
            FriendsTab.Classes.Set("Chosen", friends);
            if (load)
                Open();
        }

        string NameOf(ulong id) => !sample ? SteamClient.NameOf(id) : id == 0 ? "You" : "Player " + id;

        static string Number(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

        static Border Line(string rank, string name, string wins, bool self, bool heading = false)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*,140") };
            var a = new TextBlock { Text = rank };
            var b = new TextBlock { Text = name, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            var c = new TextBlock { Text = wins, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(b, 1);
            Grid.SetColumn(c, 2);
            grid.Children.Add(a);
            grid.Children.Add(b);
            grid.Children.Add(c);
            var line = new Border { Classes = { "Line" }, Child = grid };
            if (self)
                line.Classes.Add("Own");
            if (heading)
                line.Classes.Add("Head");
            return line;
        }
    }
}
