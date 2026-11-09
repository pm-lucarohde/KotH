using System;
using System.Collections.Generic;
using System.Text;
using KotH.Shared.Net;
using static KotH.Client.Steam.SteamApi;

namespace KotH.Client.Steam
{
    /// <summary>Messages straight to the other lobby members, besides the game's own (channel 0, host and
    /// peers): chat reliably on channel 1, voice as datagrams on channel 2, beside the game's sessions with
    /// each member (SteamTransport accepts them). What arrives is limited per sender, the rest dropped.
    /// Game thread only.</summary>
    public sealed unsafe class SteamMesh
    {
        public const int ChatChannel = 1, VoiceChannel = 2;
        public const int MaxChatChars = 160;
        const int MaxChatBytes = 4 * MaxChatChars, MaxVoiceBytes = 600;
        const int Reliable = 9, Unreliable = 1;   // both NoNagle: out at once
        const int ChatsPer10s = 5, VoicePerSecond = 60;

        readonly SteamLobby lobby;
        readonly byte[] identity = new byte[IdentitySize];
        readonly nint[] inbox = new nint[64];
        readonly Dictionary<ulong, Budget> budgets = new Dictionary<ulong, Budget>();
        readonly byte[] chatOut = new byte[MaxChatBytes];

        struct Budget
        {
            public double ChatFrom, VoiceFrom;
            public int Chats, Voices;
        }

        public SteamMesh(SteamLobby lobby) => this.lobby = lobby;

        /// <summary>Sends a chat line to every other member; false when there is nothing to send.</summary>
        public bool SendChat(string text)
        {
            text = Clean(text);
            if (text.Length == 0 || lobby.Id == 0)
                return false;
            int length = Encoding.UTF8.GetBytes(text, chatOut);
            foreach (ulong member in lobby.Members())
                if (member != SteamClient.SteamId)
                    Send(member, chatOut.AsSpan(0, length), Reliable, ChatChannel);
            return true;
        }

        /// <summary>A voice packet to one member, lost rather than late.</summary>
        public void SendVoice(ulong member, ReadOnlySpan<byte> packet)
        {
            if (packet.Length <= MaxVoiceBytes && member != SteamClient.SteamId)
                Send(member, packet, Unreliable, VoiceChannel);
        }

        /// <summary>What arrived since the last call: chat lines (sender, text) and voice packets (sender,
        /// bytes; valid during the call only), from members of the lobby within their budget.</summary>
        public void Poll(double now, Action<ulong, string> chat, VoiceHandler voice)
        {
            Receive(ChatChannel, now, chat, null);
            Receive(VoiceChannel, now, null, voice);
        }

        public delegate void VoiceHandler(ulong sender, ReadOnlySpan<byte> packet);

        // SteamNetworkingMessage_t: data, size, connection, then the sender's identity at 16.
        void Receive(int channel, double now, Action<ulong, string> chat, VoiceHandler voice)
        {
            fixed (nint* box = inbox)
            {
                int count = SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(SteamClient.Messages, channel, box, inbox.Length);
                for (int i = 0; i < count; i++)
                {
                    nint m = box[i];
                    ulong from = SteamAPI_SteamNetworkingIdentity_GetSteamID64((byte*)(m + 16));
                    var data = new ReadOnlySpan<byte>(*(byte**)m, *(int*)(m + 8));
                    if (lobby.Has(from) && Allowed(from, now, channel == ChatChannel))
                    {
                        if (chat != null && data.Length <= MaxChatBytes)
                        {
                            string text = Clean(Encoding.UTF8.GetString(data));
                            if (text.Length > 0)
                                chat(from, text);
                        }
                        else if (voice != null && data.Length <= MaxVoiceBytes)
                            voice(from, data);
                    }
                    SteamAPI_SteamNetworkingMessage_t_Release(m);
                }
            }
        }

        bool Allowed(ulong from, double now, bool isChat)
        {
            budgets.TryGetValue(from, out Budget b);
            bool ok;
            if (isChat)
            {
                if (now - b.ChatFrom > 10000.0)
                    (b.ChatFrom, b.Chats) = (now, 0);
                ok = ++b.Chats <= ChatsPer10s;
            }
            else
            {
                if (now - b.VoiceFrom > 1000.0)
                    (b.VoiceFrom, b.Voices) = (now, 0);
                ok = ++b.Voices <= VoicePerSecond;
            }
            budgets[from] = b;
            return ok;
        }

        /// <summary>A line as the chat shows it: no control characters, trimmed, at most MaxChatChars.</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            var s = new StringBuilder(Math.Min(text.Length, MaxChatChars));
            foreach (char c in text)
                if (!char.IsControl(c) && s.Length < MaxChatChars)
                    s.Append(c);
            return s.ToString().Trim();
        }

        /// <summary>A member's Steam name, for a sender the world has no player of.</summary>
        public static string PersonaOf(ulong member)
            => Text(SteamAPI_ISteamFriends_GetFriendPersonaName(SteamClient.Friends, member)) ?? "?";

        void Send(ulong member, ReadOnlySpan<byte> data, int flags, int channel)
        {
            Array.Clear(identity);
            fixed (byte* who = identity, bytes = data)
            {
                SteamAPI_SteamNetworkingIdentity_SetSteamID64(who, member);
                SteamAPI_ISteamNetworkingMessages_SendMessageToUser(SteamClient.Messages, who, bytes, (uint)data.Length, flags, channel);
            }
        }
    }
}
