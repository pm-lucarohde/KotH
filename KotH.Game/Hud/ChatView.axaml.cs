using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using KotH.Client.Menu;
using KotH.Client.Net;

namespace KotH.Client.Hud
{
    /// <summary>The chat's controls for ChatBox: the lines it is given, closed bare with a soft shadow,
    /// open on the glass with the typing line. Rebuilt only when what is shown changed.</summary>
    public partial class ChatView : UserControl
    {
        // Closed, the lines stand on the scene without glass (Luca): a soft shadow to read them, as the speaker.
        static readonly DropShadowEffect Shadow = new DropShadowEffect { OffsetX = 0, OffsetY = 1, BlurRadius = 6, Color = Color.Parse("#B0000000") };
        static readonly IBrush NameBrush = new SolidColorBrush(Color.Parse("#FFD27A"));
        static readonly IBrush OwnBrush = new SolidColorBrush(Color.Parse("#7FD8F0"));
        static readonly IBrush NoteBrush = new SolidColorBrush(Color.Parse("#B3FFFFFF"));

        public readonly MenuTextBox Typing = new MenuTextBox
        {
            MaxLength = Steam.SteamMesh.MaxChatChars, Placeholder = "Say something, Enter sends, Esc closes",
        };

        int shownVersion = -1, shownCount = -1;
        bool shownOpen;

        public ChatView()
        {
            InitializeComponent();
            Field.Children.Add(Typing);
        }

        Audio.VoiceChat.Mic shownMic;

        /// <summary>Where the chat ends in its part, as last laid out (the voice's sign goes right under it).</summary>
        public double ChatBottom => Back.Bounds.Bottom;

        /// <summary>Where the chat begins: the voice's sign stands there while no chat shows.</summary>
        public double ChatTop => Back.Bounds.Top;

        /// <summary>The parts, out of this control, to be the contents of overlays.</summary>
        public (Control Chat, Control Talk) TakeParts()
        {
            Parts.Children.Clear();
            return (ChatPart, TalkPart);
        }

        /// <summary>The voice's sign: going out (the speaker with waves alone), or crossed out with why the key sends nothing.</summary>
        public void ShowTalk(Audio.VoiceChat.Mic mic)
        {
            if (shownMic == mic)
                return;
            shownMic = mic;
            TalkWaves.IsVisible = mic == Audio.VoiceChat.Mic.Talking;
            TalkCross.IsVisible = TalkText.IsVisible = !TalkWaves.IsVisible;
            TalkText.Text = mic switch
            {
                Audio.VoiceChat.Mic.Talking => "",   // the speaker alone (Luca)
                Audio.VoiceChat.Mic.Missing => "No microphone",
                _ => "Voice needs a Steam round",
            };
        }

        /// <summary>The newest count lines of the chat, closed or open for typing.</summary>
        public void Show(IReadOnlyList<Comms.Line> lines, int version, int count, bool open)
        {
            if (version == shownVersion && count == shownCount && open == shownOpen)
                return;
            (shownVersion, shownCount, shownOpen) = (version, count, open);
            Back.Classes.Set("Glass", open);
            Field.IsVisible = open;
            Lines.Children.Clear();
            for (int i = System.Math.Max(0, lines.Count - count); i < lines.Count; i++)
                Lines.Children.Add(LineOf(lines[i], open));
        }

        static Control LineOf(Comms.Line line, bool open)
        {
            var text = new TextBlock { Classes = { "Hud" }, FontSize = 21, TextWrapping = TextWrapping.Wrap };
            if (line.Name == null)
                text.Inlines!.Add(new Run(line.Text) { Foreground = NoteBrush });
            else
            {
                // No-break spaces: name and text stay on one line, a long word breaks at the line's end (Luca).
                text.Inlines!.Add(new Run(line.Name.Replace(' ', '\u00A0') + ":\u00A0") { Foreground = line.Own ? OwnBrush : NameBrush });
                text.Inlines.Add(new Run(line.Text));
            }
            if (!open)
                text.Effect = Shadow;
            return new Border
            {
                Child = text, Padding = new(12, 5, 12, 6), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
        }
    }
}
