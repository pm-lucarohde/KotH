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
        // Rows of 58 units fit three to the well, as on Find server.
        const int PerPage = 3;

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

        /// <summary>Map was chosen: time to list the maps.</summary>
        public event Action MapsOpened;

        readonly RowList rows = new RowList(PerPage);

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
            Maps.Click += (_, _) => OpenMaps();
            Back.Click += (_, _) => PageOpen = false;
            More.Click += (_, _) => rows.Next();
        }

        /// <summary>Whether a page shows instead of the buttons.</summary>
        public bool PageOpen { get; set; }

        /// <summary>The buttons for this game: Invite friends in a Steam round, Map for the host in the lobby.</summary>
        public void Offer(bool invite, bool maps)
        {
            Invite.IsVisible = invite;
            Maps.IsVisible = maps;
            Board.Height = BoardHeight + ButtonStep * ((invite ? 1 : 0) + (maps ? 1 : 0));
        }

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
            More.IsVisible = list && rows.Paged;
            if (!list)
                SettingsPage.Open();
        }

        /// <summary>The list page's rows, what it says without any, and the word underneath.</summary>
        public void ShowList(IReadOnlyList<RowList.Row> list, string empty, string status)
        {
            rows.Show(list, keepPage: true);
            NoRows.Text = empty;
            NoRows.IsVisible = list.Count == 0;
            More.IsVisible = rows.Paged;
            Status.Text = status;
        }

        public void Say(string status) => Status.Text = status;

        /// <summary>The two parts, out of this control, each to be the content of an overlay.</summary>
        public (Control Board, Control Page) TakeParts()
        {
            Parts.Children.Clear();
            return (BoardPart, PagePart);
        }

        /// <summary>Test aid (KOTH_SHOT_PAUSE): clicks button 0-3.</summary>
        public void Pretend(int button)
        {
            Button[] buttons = { Resume, Settings, ToMenu, Quit };
            if (button >= 0 && button < buttons.Length)
                buttons[button].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }
}
