using System;
using System.Collections.Generic;
using Avalonia.Controls;
using KotH.Client.Steam;
using KotH.Shared.Net;

namespace KotH.Client.Menu
{
    /// <summary>The Find server page: what MainMenu found on Steam, a Join per round, and what is going
    /// on (searching, joining, why not). The menu does the searching and joining; this only shows.</summary>
    public partial class FindPage : UserControl
    {
        // Rows of 58 units fit three to the well, under the title and over Back.
        const int PerPage = 3;

        /// <summary>Refresh was chosen.</summary>
        public event Action RefreshRequested;

        /// <summary>Join was chosen on a round.</summary>
        public event Action<LobbyListing> JoinRequested;

        readonly RowList list = new RowList(PerPage);
        bool busy, saysWhy;

        public FindPage()
        {
            InitializeComponent();
            Inside.Children.Add(list);
            Refresh.Click += (_, _) => RefreshRequested?.Invoke();
            More.Click += (_, _) => list.Next();
            Found(new List<LobbyListing>());
        }

        /// <summary>No Steam: nothing to find; direct IP stays on the command line.</summary>
        public void Unavailable(string why)
        {
            busy = true;
            list.Show(Array.Empty<RowList.Row>());
            EmptyTitle.Text = why;
            EmptyHint.Text = "Start Steam and KotH again, or join by address: KotH --join ADDRESS";
            Empty.IsVisible = true;
            Status.Text = "";
            Refresh.IsEnabled = false;
            More.IsVisible = false;
        }

        public void Searching()
        {
            saysWhy = false;
            Status.Text = "Searching …";
            Refresh.IsEnabled = false;
        }

        /// <summary>The rounds found, fullest first; again (a search on its own) keeps the page and a word on joining.</summary>
        public void Found(List<LobbyListing> rounds, bool again = false)
        {
            if (busy && again)
                return;
            busy = false;
            var rows = new List<RowList.Row>();
            foreach (LobbyListing round in rounds)
            {
                LobbyListing chosen = round;
                rows.Add(new RowList.Row(round.Host, $"{round.Map}   {round.Players}/{NetWorld.MaxPlayers}", "Join",
                                         () => { if (!busy) JoinRequested?.Invoke(chosen); }));
            }
            list.Show(rows, keepPage: again);
            Empty.IsVisible = rows.Count == 0;
            EmptyTitle.Text = "No servers found";
            EmptyHint.Text = "Host one yourself, or ask a friend for an invite.";
            if (!again || !saysWhy)
                Status.Text = rows.Count == 0 ? "" : rows.Count == 1 ? "1 round" : $"{rows.Count} rounds";
            Refresh.IsEnabled = true;
            More.IsVisible = list.Paged;
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
