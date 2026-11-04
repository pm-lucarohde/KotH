using System;
using System.Collections.Generic;
using KotH.Shared.Net;
using static KotH.Client.Steam.SteamApi;

namespace KotH.Client.Steam
{
    /// <summary>The sessions over Steam's peer-to-peer messages (ISteamNetworkingMessages), through Valve's
    /// relays: no port to open, addresses stay hidden. A host takes sessions from members of its lobby only
    /// and numbers them from 1; a peer sees the lobby's owner as 0. Reliable messages go in order,
    /// datagrams unreliable. Game thread only; ends a session when it fails or its member leaves.</summary>
    public sealed unsafe class SteamTransport : INetTransport, IDisposable
    {
        const int Channel = 0;

        // k_nSteamNetworkingSend_ReliableNoNagle and _UnreliableNoNagle: out at once, as LiteNetLib sends.
        const int Reliable = 9, Unreliable = 1;

        // Callback ids: SteamNetworkingMessagesSessionRequest_t and _SessionFailed_t.
        const int RequestId = 1251, FailedId = 1252;

        readonly SteamLobby lobby;
        readonly bool hosting;
        readonly Dictionary<ulong, int> peerOf = new Dictionary<ulong, int>();
        readonly Dictionary<int, ulong> steamOf = new Dictionary<int, ulong>();
        readonly HashSet<ulong> waiting = new HashSet<ulong>();
        readonly Queue<TransportEvent> events = new Queue<TransportEvent>();
        readonly byte[] identity = new byte[IdentitySize];
        readonly nint[] inbox = new nint[64];
        int next = 1;

        SteamTransport(SteamLobby lobby, bool hosting)
        {
            this.lobby = lobby;
            this.hosting = hosting;
            SteamClient.Callback += OnCallback;
            lobby.MemberEntered += Entered;
            lobby.MemberLeft += Left;
        }

        /// <summary>Hosts the lobby's round: its members may connect.</summary>
        public static SteamTransport Host(SteamLobby lobby) => new SteamTransport(lobby, hosting: true);

        /// <summary>Connects to the lobby's owner; sending may start at once, Steam opens the session.</summary>
        public static SteamTransport Join(SteamLobby lobby)
        {
            var t = new SteamTransport(lobby, hosting: false);
            t.Map(lobby.Owner, 0);
            return t;
        }

        void Map(ulong steamId, int peer)
        {
            peerOf[steamId] = peer;
            steamOf[peer] = steamId;
        }

        void OnCallback(int id, nint p)
        {
            // Both structs begin with the remote's SteamNetworkingIdentity.
            if (id != RequestId && id != FailedId)
                return;
            ulong remote = SteamAPI_SteamNetworkingIdentity_GetSteamID64((byte*)p);
            if (id == RequestId)
            {
                if (peerOf.ContainsKey(remote) || hosting && lobby.Has(remote))
                    Accept(remote);
                else if (hosting)
                    waiting.Add(remote);   // its lobby entry may still be on the way
                return;
            }
            // SteamNetConnectionInfo_t: identity (136), user data, socket, address (18), pad, POPs, state, end reason.
            Console.WriteLine($"[steam] session with {remote} failed: {*(int*)(p + 180)} {Text((byte*)(p + 184))}");
            Lost(remote);
        }

        void Accept(ulong remote)
        {
            waiting.Remove(remote);
            fixed (byte* who = Identity(remote))
                SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(SteamClient.Messages, who);
            if (!peerOf.ContainsKey(remote))
            {
                Map(remote, next++);
                events.Enqueue(new TransportEvent { Kind = TransportEventKind.Connected, Peer = peerOf[remote] });
            }
        }

        void Entered(ulong member)
        {
            if (hosting && waiting.Contains(member))
                Accept(member);
        }

        void Left(ulong member)
        {
            waiting.Remove(member);
            if (peerOf.ContainsKey(member))
            {
                Close(member);
                Lost(member);
            }
        }

        void Lost(ulong remote)
        {
            if (!peerOf.Remove(remote, out int peer))
                return;
            steamOf.Remove(peer);
            events.Enqueue(new TransportEvent { Kind = TransportEventKind.Disconnected, Peer = peer });
        }

        byte[] Identity(ulong steamId)
        {
            Array.Clear(identity);
            fixed (byte* p = identity)
                SteamAPI_SteamNetworkingIdentity_SetSteamID64(p, steamId);
            return identity;
        }

        /// <summary>The Steam account behind a peer; 0 when unknown.</summary>
        public ulong SteamIdOf(int peer) => steamOf.TryGetValue(peer, out ulong id) ? id : 0;

        /// <summary>The host: whether the peer is a friend it invited (a private round lets them in).</summary>
        public bool Invited(int peer) => steamOf.TryGetValue(peer, out ulong id) && lobby.WasInvited(id);

        public void Send(int peer, ReadOnlySpan<byte> data, bool reliable)
        {
            if (!steamOf.TryGetValue(peer, out ulong remote))
                return;
            fixed (byte* who = Identity(remote), bytes = data)
                SteamAPI_ISteamNetworkingMessages_SendMessageToUser(SteamClient.Messages, who, bytes, (uint)data.Length,
                                                                    reliable ? Reliable : Unreliable, Channel);
        }

        public bool Poll(out TransportEvent e)
        {
            if (events.Count == 0)
                Receive();
            return events.TryDequeue(out e);
        }

        // SteamNetworkingMessage_t: data, size, connection, then the sender's identity at 16.
        void Receive()
        {
            fixed (nint* box = inbox)
            {
                int count = SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(SteamClient.Messages, Channel, box, inbox.Length);
                for (int i = 0; i < count; i++)
                {
                    nint m = box[i];
                    ulong from = SteamAPI_SteamNetworkingIdentity_GetSteamID64((byte*)(m + 16));
                    if (peerOf.TryGetValue(from, out int peer))
                    {
                        var data = new ReadOnlySpan<byte>(*(byte**)m, *(int*)(m + 8));
                        events.Enqueue(new TransportEvent { Kind = TransportEventKind.Data, Peer = peer, Data = data.ToArray() });
                    }
                    SteamAPI_SteamNetworkingMessage_t_Release(m);
                }
            }
        }

        void Close(ulong remote)
        {
            if (!SteamClient.Ready)
                return;
            fixed (byte* who = Identity(remote))
                SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(SteamClient.Messages, who);
        }

        public void Disconnect(int peer)
        {
            if (steamOf.TryGetValue(peer, out ulong remote))
            {
                Close(remote);
                Lost(remote);
            }
        }

        public void Dispose()
        {
            SteamClient.Callback -= OnCallback;
            lobby.MemberEntered -= Entered;
            lobby.MemberLeft -= Left;
            foreach (ulong remote in peerOf.Keys)
                Close(remote);
            peerOf.Clear();
            steamOf.Clear();
        }
    }
}
