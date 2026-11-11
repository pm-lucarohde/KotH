using Avalonia.Controls;

namespace KotH.Client.Menu
{
    /// <summary>The Social page: Party (friends to invite, the party) and Leaderboard (most wins) as tabs.</summary>
    public partial class SocialPage : UserControl
    {
        public SocialPage()
        {
            InitializeComponent();
            PartyTab.PointerPressed += (_, _) => Choose(false);
            BoardTab.PointerPressed += (_, _) => Choose(true);
            Choose(false);
        }

        /// <summary>The Party tab's page.</summary>
        public PartyPage Partier => Party;

        /// <summary>The Leaderboard tab's page.</summary>
        public LeaderboardPage Leaderboard => Board;

        /// <summary>Whether the Leaderboard tab is chosen.</summary>
        public bool BoardShown => Board.IsVisible;

        /// <summary>Opened: the Party tab (an invite wants it) or the one chosen last, filled anew.</summary>
        public void Open(bool party)
        {
            Choose(!party && BoardShown);
        }

        /// <summary>MainMenu, while open: the party's state, or the board when its values or names changed.</summary>
        public void Refresh()
        {
            if (BoardShown)
                Board.Refresh();
            else
                Party.Refresh();
        }

        /// <summary>The Leaderboard tab (true) or Party, filled anew.</summary>
        public void Choose(bool board)
        {
            PartyTab.Classes.Set("Chosen", !board);
            BoardTab.Classes.Set("Chosen", board);
            Party.IsVisible = !board;
            Board.IsVisible = board;
            if (board)
                Board.Open();
            else
                Party.Open();
        }
    }
}
