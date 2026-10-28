using System;
using System.Collections.Generic;
using LiteNetLib;

namespace KotH.Shared.Net
{
    /// <summary>The sessions over UDP with LiteNetLib. A host listens and numbers its peers from
    /// 1; a peer connects to one host (peer 0). Reliable messages go ordered, datagrams as plain
    /// UDP, one bigger than the path's MTU goes reliable. Send and Poll from the game thread only.</summary>
    public sealed class LiteNetTransport : INetTransport, IDisposable
    {
        public const int DefaultPort = 7777;
        const string Key = "KotH";

        readonly EventBasedLiteNetListener listener = new EventBasedLiteNetListener();
        readonly LiteNetManager manager;
        readonly Queue<TransportEvent> events = new Queue<TransportEvent>();
        readonly Dictionary<int, LiteNetPeer> peers = new Dictionary<int, LiteNetPeer>();
        readonly bool hosting;

        // A peer's reliable messages until the host has accepted it.
        readonly List<byte[]> early = new List<byte[]>();
        LiteNetPeer host;
        bool connected;

        LiteNetTransport(bool hosting)
        {
            this.hosting = hosting;
            manager = new LiteNetManager(listener)
            {
                AutoRecycle = true,
                DisconnectTimeout = 5000,
                UpdateTime = 5,
            };

            listener.ConnectionRequestEvent += request =>
            {
                if (this.hosting)
                    request.AcceptIfKey(Key);
                else
                    request.Reject();
            };
            listener.PeerConnectedEvent += OnConnected;
            listener.PeerDisconnectedEvent += (peer, info) =>
            {
                int id = IdOf(peer);
                // A peer also hears of a connection that never came about.
                if (!peers.Remove(id) && hosting)
                    return;
                DisconnectReason = info.Reason.ToString();
                events.Enqueue(new TransportEvent { Kind = TransportEventKind.Disconnected, Peer = id });
            };
            listener.NetworkReceiveEvent += (peer, reader, method) =>
            {
                events.Enqueue(new TransportEvent
                {
                    Kind = TransportEventKind.Data, Peer = IdOf(peer), Data = reader.GetRemainingBytes(),
                });
            };
        }

        /// <summary>Listens for peers on the port, all interfaces, IPv4 and IPv6.</summary>
        public static LiteNetTransport Host(int port)
        {
            var t = new LiteNetTransport(hosting: true);
            if (!t.manager.Start(port))
                throw new InvalidOperationException($"Port {port} is in use.");
            return t;
        }

        /// <summary>Connects to a host; sending may start at once, it waits for the line.</summary>
        public static LiteNetTransport Join(string address, int port)
        {
            var t = new LiteNetTransport(hosting: false);
            t.manager.Start();
            t.host = t.manager.Connect(address, port, Key);
            if (t.host == null)
                throw new InvalidOperationException($"Cannot connect to {address}:{port}.");
            return t;
        }

        /// <summary>Why the last connection ended, LiteNetLib's words.</summary>
        public string DisconnectReason { get; private set; }

        /// <summary>Round trip to a peer as LiteNetLib measures it, -1 if unknown.</summary>
        public int RoundTripMs(int peer) => peers.TryGetValue(peer, out LiteNetPeer p) ? p.RoundTripTime : -1;

        int IdOf(LiteNetPeer peer) => hosting ? peer.Id + 1 : 0;

        void OnConnected(LiteNetPeer peer)
        {
            int id = IdOf(peer);
            peers[id] = peer;
            if (hosting)
            {
                events.Enqueue(new TransportEvent { Kind = TransportEventKind.Connected, Peer = id });
                return;
            }

            connected = true;
            foreach (byte[] data in early)
                peer.Send(data, DeliveryMethod.ReliableOrdered);
            early.Clear();
        }

        public void Send(int peer, ReadOnlySpan<byte> data, bool reliable)
        {
            if (!hosting && !connected)
            {
                if (reliable)
                    early.Add(data.ToArray());
                return;
            }
            if (!peers.TryGetValue(peer, out LiteNetPeer p))
                return;

            DeliveryMethod method = reliable || data.Length > p.GetMaxSinglePacketSize(DeliveryMethod.Unreliable)
                ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;
            p.Send(data, method);
        }

        public bool Poll(out TransportEvent e)
        {
            if (events.Count == 0)
            {
                manager.TriggerUpdate();
                manager.PollEvents();
            }
            return events.TryDequeue(out e);
        }

        public void Disconnect(int peer)
        {
            if (peers.TryGetValue(peer, out LiteNetPeer p))
                p.Disconnect();
        }

        public void Dispose() => manager.Stop();
    }
}
