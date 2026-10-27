using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace KotH.Client.Menu
{
    /// <summary>
    /// The main menu's controls: the buttons, the note that says what the one
    /// under the pointer does, the pages behind three of them (only to look at
    /// so far), the title and the small print. Laid out in four parts of a
    /// screen 1080 high, which MainMenu takes out (<see cref="TakeParts"/>) and
    /// shows as overlays of their own. MainMenu also slides them in, so
    /// nothing here moves but the buttons.
    /// </summary>
    public partial class MainMenuView : UserControl
    {
        /// <summary>Server erstellen was chosen.</summary>
        public event Action HostRequested;

        /// <summary>Spiel verlassen was chosen.</summary>
        public event Action QuitRequested;

        /// <summary>The height the parts are laid out for, in their units.</summary>
        public const double DesignHeight = 1080;

        Button open;

        public MainMenuView()
        {
            InitializeComponent();
            Describe(Host);
            foreach (Button button in new[] { Host, Find, Settings, Languages, Quit })
                button.PointerEntered += (_, _) => Describe(button);
            Host.Click += (_, _) => HostRequested?.Invoke();
            Quit.Click += (_, _) => QuitRequested?.Invoke();
            Find.Click += (_, _) => OpenPage(Find, "Server suchen", FindPage);
            Settings.Click += (_, _) => OpenPage(Settings, "Einstellungen", SettingsPage);
            Languages.Click += (_, _) => OpenPage(Languages, "Sprachen", LanguagesPage);
            Back.Click += (_, _) => GoBack();
        }

        Button[] Lit => new[] { Host, Find, Settings, Languages, Quit, Back };

        /// <summary>
        /// Behind the load screen: every button shows all its pictures, so Skia
        /// decodes the lit and pressed ones there (hidden ones it skips, and their
        /// first sight stuttered) and the hover transitions run once; false puts them back.
        /// </summary>
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

        /// <summary>Test aid (KOTH_SHOT_MENU): shows button 0-4 as under the pointer, or clicks one of the first four.</summary>
        public void Pretend(int button, bool click)
        {
            Button[] buttons = { Host, Find, Settings, Languages, Quit };
            if (button < 0 || button >= buttons.Length)
                return;
            Describe(buttons[button]);
            if (click && button <= 3)
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
            Description.Text = button == Host ? "Starte eine eigene Runde auf dem Berg. Freunde treten über deine Adresse bei, Bots füllen die freien Plätze."
                             : button == Find ? "Finde Runden anderer Spieler im Netz und tritt ihnen bei."
                             : button == Settings ? "Grafik, Ton und Steuerung nach deinem Geschmack."
                             : button == Languages ? "Wähle die Sprache des Spiels."
                             : "Zurück auf den Desktop. Bis bald auf dem Gipfel!";
        }
    }
}
