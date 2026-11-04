using System;
using System.Collections.Generic;
using KotH.Shared.Net;
using static KotH.Client.Steam.SteamApi;

namespace KotH.Client.Steam
{
    /// <summary>Steam's peer-to-peer messages (ISteamNetworkingMessages) through Valve's relays: no port, hidden
    /// addresses. A host takes sessions from lobby members only, numbered from 1; a peer calls its host 0.
    /// Reliable in order, datagrams unreliable; game thread only. A host change makes a peer's transport
    /// the host's (HostNow) or points it at another host (JoinNow).</summary>
    public sealed unsafe class SteamTransport : INetTransport, IDisposable
    {
        const int Channel = 0;

        // k_nSteamNetworkingSend_ReliableNoNagle and _UnreliableNoNagle: out at once, as LiteNetLib sends.
        const int Reliable = 9, Unreliable = 1;

        // Callback ids: SteamNetworkingMessagesSessionRequest_t and _SessionFailed_t.
        const int RequestId = 1251, FailedId = 1252;

        readonly SteamLobby lobby;
        bool hosting;
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
                else
                    waiting.Add(remote);   // its lobby entry may still be on the way, or it is this peer's turn to host
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

        /// <summary>A host change made this peer the host: the old host's session ends, members who
        /// already asked for one (they saw the change first) are taken in.</summary>
        public void HostNow()
        {
            CloseAll();
            hosting = true;
            next = 1;
            foreach (ulong member in new List<ulong>(waiting))
                if (lobby.Has(member))
                    Accept(member);
        }

        /// <summary>A host change: from now on to this host, as peer 0.</summary>
        public void JoinNow(ulong host)
        {
            CloseAll();
            hosting = false;
            waiting.Remove(host);
            Map(host, 0);
        }

        /// <summary>The host this peer talks to; 0 while hosting.</summary>
        public ulong HostId => !hosting && steamOf.TryGetValue(0, out ulong id) ? id : 0;

        // Every session ends; what is still queued from them is dropped.
        void CloseAll()
        {
            foreach (ulong remote in peerOf.Keys)
                Close(remote);
            peerOf.Clear();
            steamOf.Clear();
            events.Clear();
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
            CloseAll();
        }
    }
}
