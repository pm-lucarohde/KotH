using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace KotH.Client.Menu
{
    /// <summary>The main menu's controls: name, level bar, buttons, the note on the one under the pointer, the pages
    /// behind five of them (Host server, Find server, Social, Settings; Languages only to look at), Customize, title and
    /// small print. Five parts of a screen 1080 high that MainMenu takes out (<see cref="TakeParts"/>), shows and slides in.</summary>
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

        Button open;

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

        bool locked;


        public MainMenuView()
        {
            InitializeComponent();
            Describe(Host);
            foreach (Button button in new[] { Host, Find, SocialButton, Customize, Settings, Languages, Quit })
                button.PointerEntered += (_, _) => Describe(button);
            // A second click on the open page's button closes it again, as Escape does.
            Host.Click += (_, _) =>
            {
                if (Closed(Host))
                    return;
                OpenPage(Host, "Host server", HostPage);
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
                    OpenPage(Settings, "Settings", SettingsPage);
            };
            Languages.Click += (_, _) =>
            {
                if (!Closed(Languages))
                    OpenPage(Languages, "Languages", LanguagesPage);
            };
            SettingsPage.BackRequested += () => GoBack();
            Back.Click += (_, _) => GoBack();
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
        public void OpenFind() => OpenPage(Find, "Find server", FindPage);

        /// <summary>Opens Social: on its Party tab for an accepted party invite, else on the tab chosen last.</summary>
        public void OpenSocial(bool party)
        {
            OpenPage(SocialButton, "Social", SocialPage);
            SocialPage.Open(party);
        }

        /// <summary>The level bar: level, XP into it of the XP to the next (span 0 at the top); offline
        /// when the values are the file's, not Steam's. Below level 10 Customize is locked.</summary>
        public void Progress(int level, int into, int span, bool offline, bool customizeLocked)
        {
            LevelText.Text = "Level " + level.ToString(System.Globalization.CultureInfo.InvariantCulture);
            XpText.Text = (span == 0 ? "Max level" : $"{Number(into)} / {Number(span)} XP") + (offline ? " (offline)" : "");
            XpFill.Width = 210.0 * (span == 0 ? 1.0 : Math.Clamp((double)into / span, 0.0, 1.0));
            locked = customizeLocked;
            Customize.Classes.Set("Locked", locked);
            if (locked && open == Customize)
                GoBack();
        }

        static string Number(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

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

        void OpenPage(Button button, string title, Control content)
        {
            open?.Classes.Remove("Active");
            open = button;
            button.Classes.Add("Active");
            if (content == null)
                return;
            PageTitle.Text = title;
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
            Description.Text = button == Host ? "Open a lobby on Mount Destiny, public or private with a password, and choose the round's map. Bots fill the free slots."
                             : button == Find ? "Find other players' rounds on Steam and join them."
                             : button == SocialButton ? "Invite a friend into your party, and see who has won the most rounds."
                             : button == Customize && locked ? "Reach level 10 to colour your mushroom. Online rounds give XP."
                             : button == Customize ? "Colour your mushroom: cap, skin, leaves, gloves and spores."
                             : button == Settings ? "Graphics, sound and controls to your taste."
                             : button == Languages ? "Choose the game's language."
                             : "Back to the desktop. See you on the summit!";
        }
    }
}
