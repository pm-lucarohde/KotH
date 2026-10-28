using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace KotH.Client.Menu
{
    /// <summary>The main menu's controls: buttons, the note on the one under the pointer, the pages
    /// behind three of them (looks only so far), title and small print. Four parts of a screen 1080
    /// high that MainMenu takes out (<see cref="TakeParts"/>), shows and slides in.</summary>
    public partial class MainMenuView : UserControl
    {
        /// <summary>Host server was chosen.</summary>
        public event Action HostRequested;

        /// <summary>Quit game was chosen.</summary>
        public event Action QuitRequested;

        /// <summary>The height the parts are laid out for, in their units.</summary>
        public const double DesignHeight = MenuOverlay.DesignHeight;

        Button open;

        public MainMenuView()
        {
            InitializeComponent();
            Describe(Host);
            foreach (Button button in new[] { Host, Find, Settings, Languages, Quit })
                button.PointerEntered += (_, _) => Describe(button);
            Host.Click += (_, _) => HostRequested?.Invoke();
            Quit.Click += (_, _) => QuitRequested?.Invoke();
            Find.Click += (_, _) => OpenPage(Find, "Find server", FindPage);
            Settings.Click += (_, _) => OpenPage(Settings, "Settings", SettingsPage);
            Languages.Click += (_, _) => OpenPage(Languages, "Languages", LanguagesPage);
            Back.Click += (_, _) => GoBack();
        }

        Button[] Lit => new[] { Host, Find, Settings, Languages, Quit, Back };

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
        public bool PageOpen => open != null;

        /// <summary>
        /// The four parts, out of this control, each to be the content of an
        /// overlay: the buttons, the page, the title and the small print.
        /// </summary>
        public (Control Column, Control Page, Control Logo, Control Corner) TakeParts()
        {
            Parts.Children.Clear();
            return (ColumnPart, PagePart, LogoPart, CornerPart);
        }

        /// <summary>The name on the green band and the small print in the corner.</summary>
        public void Label(string player, string version)
        {
            PlayerName.Text = player;
            Version.Text = version;
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

        /// <summary>Test aid (KOTH_SHOT_MENU): shows button 0-4 as under the pointer, or clicks it.</summary>
        public void Pretend(int button, bool click)
        {
            Button[] buttons = { Host, Find, Settings, Languages, Quit };
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
            PageTitle.Text = title;
            FindPage.IsVisible = content == FindPage;
            SettingsPage.IsVisible = content == SettingsPage;
            LanguagesPage.IsVisible = content == LanguagesPage;
        }

        void Describe(Button button)
        {
            Description.Text = button == Host ? "Start your own round on the mountain. Friends join via your address, bots fill the free slots."
                             : button == Find ? "Find other players' rounds on the network and join them."
                             : button == Settings ? "Graphics, sound and controls to your taste."
                             : button == Languages ? "Choose the game's language."
                             : "Back to the desktop. See you on the summit!";
        }
    }
}
