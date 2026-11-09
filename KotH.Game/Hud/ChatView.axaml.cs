using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using KotH.Client.Menu;
using KotH.Client.Net;

namespace KotH.Client.Hud
{
    /// <summary>The chat's controls for ChatBox: the lines it is given, closed on chips of their own,
    /// open on the glass with the typing line. Rebuilt only when what is shown changed.</summary>
    public partial class ChatView : UserControl
    {
        static readonly IBrush Chip = new SolidColorBrush(Color.Parse("#B3121820"));
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

        static readonly IBrush TalkBrush = new SolidColorBrush(Color.Parse("#7FE08A"));
        static readonly IBrush MissingBrush = new SolidColorBrush(Color.Parse("#F07F7F"));
        Audio.VoiceChat.Mic shownMic;

        /// <summary>The parts, out of this control, to be the contents of overlays.</summary>
        public (Control Chat, Control Talk) TakeParts()
        {
            Parts.Children.Clear();
            return (ChatPart, TalkPart);
        }

        /// <summary>The voice's chip: going out, or why the held key sends nothing.</summary>
        public void ShowTalk(Audio.VoiceChat.Mic mic)
        {
            if (shownMic == mic)
                return;
            shownMic = mic;
            TalkDot.Fill = mic == Audio.VoiceChat.Mic.Talking ? TalkBrush : MissingBrush;
            TalkText.Text = mic switch
            {
                Audio.VoiceChat.Mic.Talking => "Talking",
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
                text.Inlines!.Add(new Run(line.Name + ": ") { Foreground = line.Own ? OwnBrush : NameBrush });
                text.Inlines.Add(new Run(line.Text));
            }
            return new Border
            {
                Child = text, CornerRadius = new(9), Padding = new(12, 5, 12, 6),
                Background = open ? Brushes.Transparent : Chip, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
        }
    }
}
