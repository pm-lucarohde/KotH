using System;
using KotH.Client.Audio;
using KotH.Client.Menu;
using KotH.Client.Net;
using KotH.Client.Player;
using KotH.Client.Settings;
using Stride.Core;
using Stride.Engine;
using Stride.Input;
using Stride.Core.Mathematics;

namespace KotH.Client.Hud
{
    /// <summary>The chat on screen (ChatView, top left): the chat key (T) opens it to type, Enter sends to everyone
    /// in the Steam round, Escape closes. The newest lines stay a while after the last one came, then
    /// fade. While it is open the own figure holds no key and Escape is the chat's (Busy).</summary>
    [DataContract("KotH.ChatBox")]
    public sealed class ChatBox : SyncScript
    {
        const int ClosedLines = 5, OpenLines = 5;   // at most five lines stacked (Luca)
        const double ShowMs = 10000.0, FadeMs = 1500.0, NoteMs = 3000.0;
        const float Size = 0.9f, Top = 20f;   // as the HUD; from the top, the voice's sign under the chat

        /// <summary>The chat has the keys this frame (open, or closed by its own Escape or Enter).</summary>
        public static bool Busy { get; private set; }

        [DataMemberIgnore] public SimulationDriver Driver { get; set; }
        [DataMemberIgnore] public PauseMenu Pause { get; set; }

        ChatView view;
        HudRenderer hud;
        MenuOverlay overlay, talkOverlay;
        bool open, sent, shotTalk;   // shotTalk: test aid KOTH_SHOT_TALK=1, the voice's sign on
        long openedFrame;
        Comms local;   // no Steam: the chat's own notes (and the shot's sample)

        public ChatBox()
        {
            Priority = -100;   // before the pause menu, which must not take the chat's Escape
        }

        public override void Start()
        {
            MenuOverlay.Start<MenuApp>();
            hud = HudRenderer.Of(this);
            view = new ChatView();
            view.Typing.Submitted += () => sent = true;
            (Avalonia.Controls.Control chat, Avalonia.Controls.Control talk) = view.TakeParts();
            overlay = new MenuOverlay(chat, MenuOverlay.Corner.TopLeft, 0f, Top, Size) { Visible = false };
            talkOverlay = new MenuOverlay(talk, MenuOverlay.Corner.TopLeft, 0f, Top, Size) { Visible = false };
            hud.Add(overlay);
            hud.Add(talkOverlay);
            if (Environment.GetEnvironmentVariable("KOTH_SHOT_CHAT") is { Length: > 0 } sample)
                Pretend(sample);
            shotTalk = Environment.GetEnvironmentVariable("KOTH_SHOT_TALK") == "1";
        }

        public override void Cancel()
        {
            if (overlay == null)
                return;
            hud.Remove(overlay);
            hud.Remove(talkOverlay);
            overlay.Dispose();
            talkOverlay.Dispose();
            overlay = null;
        }

        public override void Update()
        {
            NetClient net = Services.GetService<NetClient>();
            double now = net?.Now ?? Environment.TickCount64;
            Comms comms = net?.Comms ?? local;
            long frame = Game.UpdateTime.FrameCount;
            bool wasOpen = open;
            bool blocked = Driver == null || Driver.InMenu || Pause is { Open: true };

            if (open && (blocked || comms == null))
                Close();
            else if (!open && !blocked && KeyPresses.Pressed(Input, ClientSettings.Applied.Key(Binding.Chat)))
            {
                if (comms != null)
                {
                    open = true;
                    openedFrame = frame;
                    view.Typing.Text = "";
                    MenuTextBox.Focus(view.Typing);
                }
                else if (net != null)
                    (local ??= new Comms(null, _ => null)).Note("The chat needs a Steam round.", now);
                comms ??= local;
            }
            // The T that opened it is no text: typing starts the frame after.
            if (open && frame != openedFrame)
            {
                MenuTextBox.Capture(Input);
                if (sent)
                    comms.Say(view.Typing.Text, net?.OwnName ?? "You", now);
                if (sent || !view.Typing.HasFocus)
                    Close();
            }
            sent = false;
            Busy = open || wasOpen;
            if (Driver != null)
                Driver.Typing = open;
            Draw(comms, now, frame);
            DrawTalk(frame);
        }

        void DrawTalk(long frame)
        {
            VoiceChat.Mic mic = shotTalk ? VoiceChat.Mic.Talking : VoiceChat.State;
            talkOverlay.Visible = mic != VoiceChat.Mic.Off && !(Driver?.InMenu ?? true);
            if (!talkOverlay.Visible)
                return;
            view.ShowTalk(mic);
            var backBuffer = GraphicsDevice.Presenter.BackBuffer;
            float scale = backBuffer.Height / (float)MenuOverlay.DesignHeight * Size;
            talkOverlay.Shift = new Vector2(0f, (float)(overlay.Visible ? view.ChatBottom : view.ChatTop) * scale);
            talkOverlay.Place(new Vector2(backBuffer.Width, backBuffer.Height));
            MenuOverlay.Tick(frame);
            talkOverlay.Upload(Game.GraphicsContext.CommandList);
        }

        void Close()
        {
            open = false;
            if (view.Typing.HasFocus)
                MenuTextBox.Focus(null);
        }

        void Draw(Comms comms, double now, long frame)
        {
            double age = comms == null ? double.PositiveInfinity : now - comms.LastAt;
            double shown = comms != null && comms.Lines.Count > 0 && comms.Lines[^1].Name == null ? NoteMs : ShowMs;
            overlay.Visible = open || age < shown;
            if (!overlay.Visible)
                return;
            overlay.Alpha = open ? 1f : (float)Math.Clamp((shown - age) / FadeMs, 0.0, 1.0);
            view.Show(comms.Lines, comms.Version, open ? OpenLines : ClosedLines, open);
            var backBuffer = GraphicsDevice.Presenter.BackBuffer;
            overlay.Place(new Vector2(backBuffer.Width, backBuffer.Height));
            MenuOverlay.Tick(frame);
            overlay.Upload(Game.GraphicsContext.CommandList);
        }

        // Test aid (KOTH_SHOT_CHAT=text): a few lines (Bot 3 says the text) and the chat open with it typed;
        // KOTH_SHOT_CHAT_OPEN=0 leaves it closed, the lines as they show after a message.
        void Pretend(string sample)
        {
            local = new Comms(null, _ => null);
            local.Note("Lobby: Mount Destiny", 0);
            local.Say("anyone up for the summit?", "Luca", Environment.TickCount64);
            local.Pretend("Bot 2", "ja, gleich oben", Environment.TickCount64);
            local.Pretend("Bot 3", sample, Environment.TickCount64);
            if (Environment.GetEnvironmentVariable("KOTH_SHOT_CHAT_OPEN") == "0")
                return;
            open = true;
            view.Typing.Text = sample;
            MenuTextBox.Focus(view.Typing);
        }
    }
}
