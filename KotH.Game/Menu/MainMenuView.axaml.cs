using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using KotH.Client.Localization;

namespace KotH.Client.Menu
{
    /// <summary>The main menu's controls: name, level bar, buttons, the note on the one under the pointer, the pages
    /// behind five of them (Host server, Find server, Social, Settings, Languages), Customize, title and small print.
    /// Five parts of a screen 1080 high that MainMenu takes out (<see cref="TakeParts"/>), shows and slides in.</summary>
    public partial class MainMenuView : UserControl
    {
        /// <summary>Host server's page was opened: time to fill it.</summary>
        public event Action HostOpened;

        /// <summary>Find server was opened: time to search.</summary>
        public event Action FindOpened;

        /// <summary>Quit game was chosen.</summary>
        public event Action QuitRequested;

        /// <summary>The height the parts are laid out for, in their units.</summary>
        public const double DesignHeight = MenuOverlay.DesignHeight;

        Button open, described;
        string openTitleKey;
        int progLevel = 1, progInto, progSpan = 200;
        bool progOffline;

        /// <summary>The Customize page, whose part MainMenu shows when <see cref="CustomizeOpen"/>.</summary>
        public CustomizePage Customizer => CustomizeView;

        /// <summary>The Settings page, which takes keys while one of its rows waits for one.</summary>
        public SettingsPage SettingsView => SettingsPage;

        /// <summary>The Find server page.</summary>
        public FindPage Finder => FindPage;

        /// <summary>The Host server page.</summary>
        public HostPage Hoster => HostPage;

        /// <summary>The Social page (Party and Leaderboard).</summary>
        public SocialPage SocialView => SocialPage;

        /// <summary>The Languages page.</summary>
        public LanguagesPage LanguagesView => LanguagesPage;

        bool locked;


        public MainMenuView()
        {
            InitializeComponent();
            described = Host;
            foreach (Button button in new[] { Host, Find, SocialButton, Customize, Settings, Languages, Quit })
                button.PointerEntered += (_, _) => Describe(button);
            // A second click on the open page's button closes it again, as Escape does.
            Host.Click += (_, _) =>
            {
                if (Closed(Host))
                    return;
                OpenPage(Host, "menu.host", HostPage);
                HostOpened?.Invoke();
            };
            Quit.Click += (_, _) => QuitRequested?.Invoke();
            Find.Click += (_, _) =>
            {
                if (Closed(Find))
                    return;
                OpenFind();
                FindOpened?.Invoke();
            };
            SocialButton.Click += (_, _) =>
            {
                if (!Closed(SocialButton))
                    OpenSocial(party: false);
            };
            Customize.Click += (_, _) =>
            {
                if (locked)
                    Describe(Customize);
                else if (!Closed(Customize))
                    OpenPage(Customize, null, null);
            };
            CustomizeView.BackRequested += () => GoBack();
            Settings.Click += (_, _) =>
            {
                if (!Closed(Settings))
                    OpenPage(Settings, "menu.settings", SettingsPage);
            };
            Languages.Click += (_, _) =>
            {
                if (!Closed(Languages))
                {
                    LanguagesPage.Refresh();
                    OpenPage(Languages, "menu.languages", LanguagesPage);
                }
            };
            SettingsPage.BackRequested += () => GoBack();
            Back.Click += (_, _) => GoBack();
            ApplyLanguage();
        }

        /// <summary>Refreshes every visible label across the main menu and its subpages for the active language.</summary>
        public void ApplyLanguage()
        {
            Host.Content = Loc.T("menu.host");
            Find.Content = Loc.T("menu.find");
            SocialButton.Content = Loc.T("menu.social");
            Customize.Content = Loc.T("menu.customize");
            Settings.Content = Loc.T("menu.settings");
            Languages.Content = Loc.T("menu.languages");
            Quit.Content = Loc.T("menu.quit");
            Back.Content = Loc.T("common.back");
            if (openTitleKey != null)
                PageTitle.Text = Loc.T(openTitleKey);
            Describe(described ?? Host);
            ShowProgress();
            LanguagesPage.Refresh();
            HostPage.ApplyLanguage();
            FindPage.ApplyLanguage();
            SocialPage.ApplyLanguage();
            SettingsPage.ApplyLanguage();
            CustomizeView.ApplyLanguage();
        }

        Button[] Lit => new[] { Host, Find, SocialButton, Customize, Settings, Languages, Quit, Back };

        /// <summary>Behind the load screen: every button shows all its pictures, so Skia
        /// decodes the lit and pressed ones there (hidden ones it skips, and their first
        /// sight stuttered) and the hover transitions run once; false puts them
        /// back.</summary>
        public void Light(bool on)
        {
            foreach (Button button in Lit)
                if (on)
                    button.Classes.Add("Warm");
                else
                    button.Classes.Remove("Warm");
        }

        /// <summary>Whether a page is open, so its part is shown.</summary>
        public bool PageOpen => open != null && open != Customize;

        /// <summary>Whether Social is open.</summary>
        public bool SocialOpen => open == SocialButton;

        /// <summary>Whether Find server is open.</summary>
        public bool FindOpen => open == Find;

        /// <summary>Whether Customize is open, so its part is shown.</summary>
        public bool CustomizeOpen => open == Customize;

        /// <summary>
        /// The five parts, out of this control, each to be the content of an
        /// overlay: the buttons, the page, Customize, the title and the small print.
        /// </summary>
        public (Control Column, Control Page, Control Customize, Control Logo, Control Corner) TakeParts()
        {
            Parts.Children.Clear();
            return (ColumnPart, PagePart, CustomizePart, LogoPart, CornerPart);
        }

        /// <summary>The name on the green band and the small print in the corner.</summary>
        public void Label(string player, string version)
        {
            PlayerName.Text = player;
            Version.Text = version;
        }

        /// <summary>Opens Find server (also for an accepted invite, to show how joining goes).</summary>
        public void OpenFind() => OpenPage(Find, "menu.find", FindPage);

        /// <summary>Opens Social: on its Party tab for an accepted party invite, else on the tab chosen last.</summary>
        public void OpenSocial(bool party)
        {
            OpenPage(SocialButton, "menu.social", SocialPage);
            SocialPage.Open(party);
        }

        /// <summary>The level bar: level, XP into it of the XP to the next (span 0 at the top); offline
        /// when the values are the file's, not Steam's. Below level 10 Customize is locked.</summary>
        public void Progress(int level, int into, int span, bool offline, bool customizeLocked)
        {
            (progLevel, progInto, progSpan, progOffline, locked) = (level, into, span, offline, customizeLocked);
            ShowProgress();
            Customize.Classes.Set("Locked", locked);
            if (locked && open == Customize)
                GoBack();
        }

        void ShowProgress()
        {
            LevelText.Text = Loc.F("common.level_n", Loc.Number(progLevel));
            string core = progSpan == 0 ? Loc.T("common.max_level")
                : Loc.Ltr(Loc.F("common.xp_progress", "\u200E" + progInto.ToString("N0", Loc.Numbers) + "\u200E", "\u200E" + progSpan.ToString("N0", Loc.Numbers) + "\u200E"));
            XpText.Text = progOffline ? Loc.F("common.xp_offline", core) : core;
            XpFill.Width = 170.0 * (progSpan == 0 ? 1.0 : Math.Clamp((double)progInto / progSpan, 0.0, 1.0));
        }

        /// <summary>Gamepad LB/RB: switches tabs in the open submenu (Settings, Social or Customize).</summary>
        public void StepTab(int delta)
        {
            if (open == Settings)
                SettingsPage.StepTab(delta);
            else if (open == SocialButton)
                SocialPage.StepTab(delta);
            else if (open == Customize)
                CustomizeView.StepTab(delta);
        }

        /// <summary>Updates the note on the bottom left when gamepad focus moves in the main column.</summary>
        public void DescribeControl(Control control)
        {
            if (control is Button b && (b == Host || b == Find || b == SocialButton || b == Customize || b == Settings || b == Languages || b == Quit))
                Describe(b);
        }

        /// <summary>Escape: closes the open page; false when none was open.</summary>
        public bool GoBack()
        {
            if (open == null)
                return false;
            open.Classes.Remove("Active");
            open = null;
            return true;
        }

        // The button's page is open: closed now (true).
        bool Closed(Button button) => open == button && GoBack();

        /// <summary>Test aid (KOTH_SHOT_MENU): shows button 0-6 as under the pointer, or clicks it.</summary>
        public void Pretend(int button, bool click)
        {
            Button[] buttons = { Host, Find, SocialButton, Customize, Settings, Languages, Quit };
            if (button < 0 || button >= buttons.Length)
                return;
            Describe(buttons[button]);
            if (click)
                buttons[button].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else
                buttons[button].Classes.Add("Active");
        }

        void OpenPage(Button button, string titleKey, Control content)
        {
            open?.Classes.Remove("Active");
            open = button;
            openTitleKey = titleKey;
            button.Classes.Add("Active");
            if (content == null)
                return;
            PageTitle.Text = titleKey != null ? Loc.T(titleKey) : "";
            FindPage.IsVisible = content == FindPage;
            HostPage.IsVisible = content == HostPage;
            SocialPage.IsVisible = content == SocialPage;
            SettingsPage.IsVisible = content == SettingsPage;
            LanguagesPage.IsVisible = content == LanguagesPage;
            PageTitle.IsVisible = content != SettingsPage && content != SocialPage;
            Back.IsVisible = content != SettingsPage;
            if (content == SettingsPage)
                SettingsPage.Open();
        }

        void Describe(Button button)
        {
            described = button;
            Description.Text = button == Host ? Loc.T("menu.desc.host")
                             : button == Find ? Loc.T("menu.desc.find")
                             : button == SocialButton ? Loc.T("menu.desc.social")
                             : button == Customize && locked ? Loc.T("menu.desc.customize_locked")
                             : button == Customize ? Loc.T("menu.desc.customize")
                             : button == Settings ? Loc.T("menu.desc.settings")
                             : button == Languages ? Loc.T("menu.desc.languages")
                             : Loc.T("menu.desc.quit");
        }
    }
}
