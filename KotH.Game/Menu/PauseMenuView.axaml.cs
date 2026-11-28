using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using KotH.Client.Localization;
using KotH.Shared.Match;

namespace KotH.Client.Menu
{
    /// <summary>
    /// The pause menu's buttons and its page (Settings, Invite friends or Map), as two
    /// parts for overlays of their own (PauseMenu); one of them is shown at a time.
    /// </summary>
    public partial class PauseMenuView : UserControl
    {
        // The board's height with four buttons (seven as the main menu's); each further one adds a button and its gap.
        const double BoardHeight = 380, ButtonStep = 66 + 12;

        enum SubPage { None, Settings, Invite, Players, Maps, Round }

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

        /// <summary>Round: a value changed (false) or Start round was pressed (true).</summary>
        public event Action<bool> RoundChanged;

        /// <summary>Round was chosen: the page needs the lobby's mode.</summary>
        public event Action RoundOpened;

        /// <summary>The question before leaving a running round: Leave (true) or Stay.</summary>
        public event Action<bool> Confirmed;

        /// <summary>Whether the lobby is currently counting down to start (toggles Start round to Cancel start).</summary>
        public Func<bool> Starting { get; set; }

        readonly RowList rows = new RowList();
        SubPage currentPage;
        string currentTitleKey;
        bool lastPenalty, lastStarting;

        public PauseMenuView()
        {
            InitializeComponent();
            ListInside.Children.Add(rows);
            Resume.Click += (_, _) => ResumeRequested?.Invoke();
            ToMenu.Click += (_, _) => MenuRequested?.Invoke();
            Quit.Click += (_, _) => QuitRequested?.Invoke();
            Settings.Click += (_, _) => ShowPage(SubPage.Settings, "pause.settings", list: false);
            SettingsPage.BackRequested += () => PageOpen = false;
            Invite.Click += (_, _) =>
            {
                ShowPage(SubPage.Invite, "pause.invite_friends", list: true);
                InviteOpened?.Invoke();
            };
            Players.Click += (_, _) =>
            {
                ShowPage(SubPage.Players, "pause.players", list: true);
                PlayersOpened?.Invoke();
            };
            Maps.Click += (_, _) => OpenMaps();
            Round.Click += (_, _) =>
            {
                ShowPage(SubPage.Round, "pause.game_mode", list: false, round: true);
                RoundOpened?.Invoke();
                ShowRound();
            };
            RoundPage.Changed += ShowRound;
            ApplyRound.Click += (_, _) =>
            {
                RoundPage.Apply();
                ShowRound();
                RoundChanged?.Invoke(false);
            };
            StartRound.Click += (_, _) => RoundChanged?.Invoke(true);
            Stay.Click += (_, _) => Confirmed?.Invoke(false);
            Leave.Click += (_, _) => Confirmed?.Invoke(true);
            Back.Click += (_, _) => PageOpen = false;
            ApplyLanguage();
        }

        public void ApplyLanguage()
        {
            Resume.Content = Loc.T("pause.resume");
            Maps.Content = Loc.T("pause.map");
            Round.Content = Loc.T("pause.game_mode");
            Invite.Content = Loc.T("pause.invite_friends");
            Players.Content = Loc.T("pause.players");
            Settings.Content = Loc.T("pause.settings");
            Quit.Content = Loc.T("pause.to_desktop");
            ToMenu.Content = Loc.T("pause.main_menu");
            LeaveTitle.Text = Loc.T("pause.leave_title");
            Stay.Content = Loc.T("pause.stay");
            Leave.Content = Loc.T("pause.leave");
            StartRound.Content = Loc.T(lastStarting ? "pause.cancel_start" : "pause.start_round");
            ApplyRound.Content = Loc.T("common.apply");
            Back.Content = Loc.T("common.back");
            if (currentTitleKey != null)
                PageTitle.Text = Loc.T(currentTitleKey);
            Ask(lastPenalty);
            SettingsPage.ApplyLanguage();
            RoundPage.ApplyLanguage();
            if (RoundShown)
                ShowRound();
        }

        /// <summary>Whether a page shows instead of the buttons.</summary>
        public bool PageOpen { get; set; }

        /// <summary>The buttons for this game: Invite friends (and Players) in a Steam round, Map and Round for the host in the lobby.</summary>
        public void Offer(bool invite, bool players, bool maps)
        {
            Invite.IsVisible = invite;
            Players.IsVisible = players;
            Maps.IsVisible = Round.IsVisible = maps;
            int extra = (invite ? 1 : 0) + (players ? 1 : 0) + (maps ? 2 : 0);
            Board.Height = BoardHeight + ButtonStep * extra;
        }

        /// <summary>The question's words: with a penalty, what leaving costs.</summary>
        public void Ask(bool penalty)
        {
            lastPenalty = penalty;
            string text = penalty ? Loc.F("pause.leave_penalty", Loc.Number(XpCounter.LeaveXp))
                                  : Loc.T("pause.leave_why");
            if (Why.Text != text)
                Why.Text = text;
        }

        /// <summary>Whether the Players page shows.</summary>
        public bool PlayersShown => PageOpen && currentPage == SubPage.Players;

        /// <summary>Whether the Invite friends page shows.</summary>
        public bool InviteShown => PageOpen && currentPage == SubPage.Invite;

        /// <summary>The Settings page, which takes keys while one of its rows waits for one.</summary>
        public SettingsPage SettingsView => SettingsPage;

        /// <summary>Gamepad LB/RB: switches tabs when Settings is open.</summary>
        public void StepTab(int delta)
        {
            if (PageOpen && SettingsPage.IsVisible)
                SettingsPage.StepTab(delta);
        }

        /// <summary>The Round page.</summary>
        public RoundPage RoundView => RoundPage;

        /// <summary>Whether the Round page shows.</summary>
        public bool RoundShown => PageOpen && currentPage == SubPage.Round;

        /// <summary>Each frame while Round shows: the word and Start round / Cancel start follow the lobby.</summary>
        public void FollowRound()
        {
            if (!RoundShown)
                return;
            bool starting = Starting?.Invoke() ?? false;
            bool canPress = starting || !RoundPage.HasChanges && !RoundPage.TooFew;
            if (starting != lastStarting || canPress != StartRound.IsEnabled)
                ShowRound();
        }

        // The Round page's word underneath and whether Apply and Start round / Cancel start may be pressed.
        void ShowRound()
        {
            bool dirty = RoundPage.HasChanges;
            bool starting = Starting?.Invoke() ?? false;
            lastStarting = starting;
            string why = RoundPage.Problem;
            ApplyRound.IsEnabled = dirty;
            StartRound.Content = Loc.T(starting ? "pause.cancel_start" : "pause.start_round");
            StartRound.IsEnabled = starting || !dirty && why == null;
            Status.Text = dirty ? Loc.T("pause.apply_hint") : starting ? "" : why ?? "";
        }

        /// <summary>Whether the Map page shows.</summary>
        public bool MapsShown => PageOpen && currentPage == SubPage.Maps;

        /// <summary>The Map page, as if chosen (also when the host lands in the lobby).</summary>
        public void OpenMaps()
        {
            ShowPage(SubPage.Maps, "pause.map", list: true);
            MapsOpened?.Invoke();
        }

        void ShowPage(SubPage kind, string titleKey, bool list, bool round = false)
        {
            currentPage = kind;
            currentTitleKey = titleKey;
            PageOpen = true;
            PageTitle.Text = Loc.T(titleKey);
            SettingsPage.IsVisible = !list && !round;
            RoundPage.IsVisible = StartRound.IsVisible = ApplyRound.IsVisible = round;
            Back.Width = round ? 150 : double.NaN;
            PageTitle.IsVisible = Back.IsVisible = list || round;
            ListPage.IsVisible = list;
            Status.IsVisible = list || round;
            if (!list && !round)
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

        /// <summary>Test aid (KOTH_SHOT_PAUSE): clicks button 0-6 (4 Players, 5 Map, 6 Round, also where hidden).</summary>
        public void Pretend(int button)
        {
            Button[] buttons = { Resume, Settings, ToMenu, Quit, Players, Maps, Round };
            if (button >= 0 && button < buttons.Length)
                buttons[button].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }
}
