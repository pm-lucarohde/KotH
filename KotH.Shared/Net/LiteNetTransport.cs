using System;
using System.Collections.Generic;
using LiteNetLib;

namespace KotH.Shared.Net
{
    /// <summary>The sessions over UDP with LiteNetLib. A host listens and numbers its peers from 1; a peer
    /// connects to one host (peer 0). Everything is encrypted (SecureLink): the host signs the handshake with
    /// its lasting key, a peer with an invite's fingerprint ends the connection on another key. Reliable
    /// messages go ordered, datagrams as plain UDP; game thread only.</summary>
    public sealed class LiteNetTransport : INetTransport, IDisposable
    {
        public const int DefaultPort = 7777;
        const string Key = "KotH";

        readonly EventBasedLiteNetListener listener = new EventBasedLiteNetListener();
        readonly LiteNetManager manager;
        readonly Queue<TransportEvent> events = new Queue<TransportEvent>();
        readonly Dictionary<int, LiteNetPeer> peers = new Dictionary<int, LiteNetPeer>();
        readonly Dictionary<int, SecureLink> links = new Dictionary<int, SecureLink>();
        readonly bool hosting;
        readonly HostKey hostKey;
        readonly string pinned;

        // A peer's reliable messages until the handshake is done.
        readonly List<byte[]> early = new List<byte[]>();
        LiteNetPeer host;
        byte[] sealedBuffer = new byte[2048];

        LiteNetTransport(bool hosting, HostKey hostKey, string pinned)
        {
            this.hosting = hosting;
            this.hostKey = hostKey;
            this.pinned = pinned;
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
                bool known = peers.Remove(id);
                if (links.Remove(id, out SecureLink link))
                    link.Dispose();
                if (!known && hosting)
                    return;
                DisconnectReason ??= info.Reason.ToString();
                if (hosting || !closed)
                    events.Enqueue(new TransportEvent { Kind = TransportEventKind.Disconnected, Peer = id });
                closed = true;
            };
            listener.NetworkReceiveEvent += (peer, reader, method) => Received(IdOf(peer), peer, reader.GetRemainingBytes());
        }

        /// <summary>Listens for peers on the port, all interfaces, IPv4 and IPv6; key signs every handshake.</summary>
        public static LiteNetTransport Host(int port, HostKey key)
        {
            var t = new LiteNetTransport(hosting: true, key, null);
            if (!t.manager.Start(port))
                throw new InvalidOperationException($"Port {port} is in use.");
            return t;
        }

        /// <summary>Connects to a host; sending may start at once, it waits for the handshake. With a
        /// fingerprint (an invite's) the host must have that key, else the connection ends.</summary>
        public static LiteNetTransport Join(string address, int port, string fingerprint = null)
        {
            var t = new LiteNetTransport(hosting: false, null, string.IsNullOrEmpty(fingerprint) ? null : fingerprint);
            t.manager.Start();
            t.host = t.manager.Connect(address, port, Key);
            if (t.host == null)
                throw new InvalidOperationException($"Cannot connect to {address}:{port}.");
            if (t.pinned == null)
                Console.WriteLine("[net] no key to check the host against: encrypted, but unverified host key");
            return t;
        }

        /// <summary>Why the last connection ended: a failed key check, else LiteNetLib's words.</summary>
        public string DisconnectReason { get; private set; }

        /// <summary>The host's fingerprint once the handshake checked out (a peer), else null.</summary>
        public string HostFingerprint => !hosting && links.TryGetValue(0, out SecureLink l) ? l.HostFingerprint : null;

        /// <summary>Packets dropped as forged, damaged or replayed.</summary>
        public int Dropped { get; private set; }

        /// <summary>Round trip to a peer as LiteNetLib measures it, -1 if unknown.</summary>
        public int RoundTripMs(int peer) => peers.TryGetValue(peer, out LiteNetPeer p) ? p.RoundTripTime : -1;

        int IdOf(LiteNetPeer peer) => hosting ? peer.Id + 1 : 0;

        bool closed;

        void OnConnected(LiteNetPeer peer)
        {
            int id = IdOf(peer);
            peers[id] = peer;
            if (hosting)
                return;   // Connected once its Hello is answered
            links[0] = SecureLink.Peer(out byte[] hello);
            peer.Send(hello, DeliveryMethod.ReliableOrdered);
        }

        void Received(int id, LiteNetPeer peer, byte[] data)
        {
            if (data.Length == 0)
                return;
            links.TryGetValue(id, out SecureLink link);
            if (link is { Ready: true })
            {
                byte[] plain = link.Open(data);
                if (plain == null)
                    Dropped++;
                else
                    events.Enqueue(new TransportEvent { Kind = TransportEventKind.Data, Peer = id, Data = plain });
                return;
            }
            if (hosting)
            {
                // The peer's Hello: answer with the signed key, from now on sealed both ways.
                SecureLink made = SecureLink.Host(hostKey, data, out byte[] reply);
                if (made == null)
                {
                    peer.Disconnect();
                    return;
                }
                links[id] = made;
                peer.Send(reply, DeliveryMethod.ReliableOrdered);
                events.Enqueue(new TransportEvent { Kind = TransportEventKind.Connected, Peer = id });
                return;
            }
            if (link == null)
                return;
            if (!link.TakeReply(data, pinned, out string why))
            {
                Console.WriteLine($"[net] {why}: connection ended");
                DisconnectReason = why;
                host?.Disconnect();
                if (!closed)
                    events.Enqueue(new TransportEvent { Kind = TransportEventKind.Disconnected, Peer = 0 });
                closed = true;
                return;
            }
            Console.WriteLine($"[net] encrypted, host key {link.HostFingerprint}{(pinned != null ? " as the invite says" : " (unverified)")}");
            foreach (byte[] message in early)
                SendSealed(peer, link, message, DeliveryMethod.ReliableOrdered);
            early.Clear();
        }

        public void Send(int peer, ReadOnlySpan<byte> data, bool reliable)
        {
            if (!peers.TryGetValue(peer, out LiteNetPeer p) || !links.TryGetValue(peer, out SecureLink link) || !link.Ready)
            {
                if (!hosting && reliable && !closed)
                    early.Add(data.ToArray());
                return;
            }
            DeliveryMethod method = reliable || data.Length + SecureLink.Overhead > p.GetMaxSinglePacketSize(DeliveryMethod.Unreliable)
                ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;
            SendSealed(p, link, data, method);
        }

        void SendSealed(LiteNetPeer p, SecureLink link, ReadOnlySpan<byte> data, DeliveryMethod method)
        {
            if (sealedBuffer.Length < data.Length + SecureLink.Overhead)
                sealedBuffer = new byte[Math.Max(data.Length + SecureLink.Overhead, sealedBuffer.Length * 2)];
            int length = link.Seal(data, sealedBuffer);
            p.Send(sealedBuffer.AsSpan(0, length), method);
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

        public void Dispose()
        {
            manager.Stop();
            foreach (SecureLink link in links.Values)
                link.Dispose();
            links.Clear();
        }
    }
}
