using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace KotH.Client.Menu
{
    /// <summary>
    /// The pause menu's buttons and its page (Settings, Invite friends or Map), as two
    /// parts for overlays of their own (PauseMenu); one of them is shown at a time.
    /// </summary>
    public partial class PauseMenuView : UserControl
    {
        // The board's height with four buttons; each further one adds a button and its gap.
        const double BoardHeight = 370, ButtonStep = 66 + 18;

        /// <summary>Resume was chosen.</summary>
        public event Action ResumeRequested;

        /// <summary>Main menu was chosen.</summary>
        public event Action MenuRequested;

        /// <summary>To desktop was chosen.</summary>
        public event Action QuitRequested;

        /// <summary>Invite friends was chosen: time to list them.</summary>
        public event Action InviteOpened;

        /// <summary>Players was chosen: time to list the others to mute.</summary>
        public event Action PlayersOpened;

        /// <summary>Map was chosen: time to list the maps.</summary>
        public event Action MapsOpened;

        /// <summary>The question before closing the window in a running round: Leave (true) or Stay.</summary>
        public event Action<bool> Confirmed;

        readonly RowList rows = new RowList();

        public PauseMenuView()
        {
            InitializeComponent();
            ListInside.Children.Add(rows);
            Resume.Click += (_, _) => ResumeRequested?.Invoke();
            ToMenu.Click += (_, _) => MenuRequested?.Invoke();
            Quit.Click += (_, _) => QuitRequested?.Invoke();
            Settings.Click += (_, _) => ShowPage("Settings", list: false);
            SettingsPage.BackRequested += () => PageOpen = false;
            Invite.Click += (_, _) =>
            {
                ShowPage("Invite friends", list: true);
                InviteOpened?.Invoke();
            };
            Players.Click += (_, _) =>
            {
                ShowPage("Players", list: true);
                PlayersOpened?.Invoke();
            };
            Maps.Click += (_, _) => OpenMaps();
            Stay.Click += (_, _) => Confirmed?.Invoke(false);
            Leave.Click += (_, _) => Confirmed?.Invoke(true);
            Back.Click += (_, _) => PageOpen = false;
        }

        /// <summary>Whether a page shows instead of the buttons.</summary>
        public bool PageOpen { get; set; }

        /// <summary>The buttons for this game: Invite friends (and Players) in a Steam round, Map for the host in
        /// the lobby; in a running online round leaving is locked (Main menu, To desktop greyed, a word why).</summary>
        public void Offer(bool invite, bool players, bool maps, bool locked)
        {
            Invite.IsVisible = invite;
            Players.IsVisible = players;
            Maps.IsVisible = maps;
            Quit.IsEnabled = ToMenu.IsEnabled = !locked;
            Locked.IsVisible = locked;
            int extra = (invite ? 1 : 0) + (players ? 1 : 0) + (maps ? 1 : 0);
            Board.Height = BoardHeight + ButtonStep * extra + (locked ? LockedHeight : 0);
        }

        /// <summary>Whether the Players page shows.</summary>
        public bool PlayersShown => PageOpen && ListPage.IsVisible && PageTitle.Text == "Players";

        const double LockedHeight = 60;

        /// <summary>The Settings page, which takes keys while one of its rows waits for one.</summary>
        public SettingsPage SettingsView => SettingsPage;

        /// <summary>Whether the Map page shows.</summary>
        public bool MapsShown => PageOpen && ListPage.IsVisible && PageTitle.Text == "Map";

        /// <summary>The Map page, as if chosen (also when the host lands in the lobby).</summary>
        public void OpenMaps()
        {
            ShowPage("Map", list: true);
            MapsOpened?.Invoke();
        }

        void ShowPage(string title, bool list)
        {
            PageOpen = true;
            PageTitle.Text = title;
            SettingsPage.IsVisible = !list;
            PageTitle.IsVisible = Back.IsVisible = list;
            ListPage.IsVisible = Status.IsVisible = list;
            if (!list)
                SettingsPage.Open();
        }

        /// <summary>The list page's rows, what it says without any, and the word underneath.</summary>
        public void ShowList(IReadOnlyList<RowList.Row> list, string empty, string status)
        {
            rows.Show(list, keepPlace: true);
            NoRows.Text = empty;
            NoRows.IsVisible = list.Count == 0;
            Status.Text = status;
        }

        public void Say(string status) => Status.Text = status;

        /// <summary>The three parts, out of this control, each to be the content of an overlay.</summary>
        public (Control Board, Control Page, Control Confirm) TakeParts()
        {
            Parts.Children.Clear();
            return (BoardPart, PagePart, ConfirmPart);
        }

        /// <summary>Test aid (KOTH_SHOT_PAUSE): clicks button 0-4 (4 Players, also where it is hidden).</summary>
        public void Pretend(int button)
        {
            Button[] buttons = { Resume, Settings, ToMenu, Quit, Players };
            if (button >= 0 && button < buttons.Length)
                buttons[button].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }
}
