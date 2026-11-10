using System.Collections.Generic;
using Avalonia.Controls;
using KotH.Client.Steam;

namespace KotH.Client.Menu
{
    /// <summary>The Party page (SteamParty): online friends to invite, who is in the party, Leave party.
    /// MainMenu refreshes it once a second while open, as a friend may come or go.</summary>
    public partial class PartyPage : UserControl
    {
        readonly RowList list = new RowList();
        readonly List<RowList.Row> rows = new List<RowList.Row>();
        string said;

        public PartyPage()
        {
            InitializeComponent();
            Inside.Children.Add(list);
            Leave.Click += (_, _) =>
            {
                SteamParty.Leave();
                said = null;
                Refresh();
            };
        }

        /// <summary>Opened: the friends anew.</summary>
        public void Open()
        {
            said = null;
            rows.Clear();
            if (SteamClient.Ready)
                foreach ((ulong id, string name) in SteamClient.OnlineFriends())
                {
                    string friend = name;
                    rows.Add(new RowList.Row(name, "", "Invite", () =>
                    {
                        said = SteamParty.Invite(id) ? $"Invited {friend}: they get it in their Steam chat." : $"Could not invite {friend}.";
                        Refresh();
                    }));
                }
            list.Show(rows);
            Empty.Text = SteamClient.Ready ? "No friends online" : SteamClient.Failure ?? "Steam is not running";
            Empty.IsVisible = rows.Count == 0;
            Refresh();
        }

        /// <summary>The party's state under the list.</summary>
        public void Refresh()
        {
            Status.Text = said != null && SteamParty.Mate == 0 ? said : SteamParty.Status();
            Leave.IsEnabled = SteamParty.Active;
        }
    }
}
