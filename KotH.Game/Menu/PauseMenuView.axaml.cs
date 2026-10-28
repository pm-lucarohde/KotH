using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace KotH.Client.Menu
{
    /// <summary>
    /// The pause menu's buttons and its Einstellungen page, as two parts for
    /// overlays of their own (PauseMenu); one of them is shown at a time.
    /// </summary>
    public partial class PauseMenuView : UserControl
    {
        /// <summary>Weiterspielen was chosen.</summary>
        public event Action ResumeRequested;

        /// <summary>Hauptmenü was chosen.</summary>
        public event Action MenuRequested;

        /// <summary>Zum Desktop was chosen.</summary>
        public event Action QuitRequested;

        public PauseMenuView()
        {
            InitializeComponent();
            Resume.Click += (_, _) => ResumeRequested?.Invoke();
            ToMenu.Click += (_, _) => MenuRequested?.Invoke();
            Quit.Click += (_, _) => QuitRequested?.Invoke();
            Settings.Click += (_, _) => SettingsOpen = true;
            Back.Click += (_, _) => SettingsOpen = false;
        }

        /// <summary>Whether the Einstellungen page shows instead of the buttons.</summary>
        public bool SettingsOpen { get; set; }

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
