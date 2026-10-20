using System;
using System.Collections.Generic;

namespace KotH.Shared.Net
{
    public enum TransportEventKind : byte { Connected, Disconnected, Data }

    /// <summary>Something that happened on a connection, in arrival order.</summary>
    public struct TransportEvent
    {
        public TransportEventKind Kind;
        public int Peer;
        public byte[] Data;
    }

    /// <summary>
    /// What the sessions need from a network: send to a peer, reliably in
    /// order or as a datagram that may get lost or overtaken, and hear what
    /// arrived. A peer sees the host as peer 0; the host numbers its peers.
    /// LiteNetLib, Steam and the in-memory network all fit behind it.
    /// </summary>
    public interface INetTransport
    {
        void Send(int peer, ReadOnlySpan<byte> data, bool reliable);
        bool Poll(out TransportEvent e);
        void Disconnect(int peer);
    }

    /// <summary>
    /// A network in memory for probes: every link has a seeded one-way
    /// latency, jitter and loss. Reliable messages never get lost and keep
    /// their order; datagrams may do neither. Time is whatever the probe
    /// says it is, so a run is the same every time.
    /// </summary>
    public sealed class MemoryNetwork
    {
        internal sealed class Link
        {
            public Endpoint To;
            public int PeerOnOtherSide;   // how the receiver names the sender
            public double Latency, Jitter, Loss;
            public double LastReliable;
            public bool Open = true;
        }

        struct Packet
        {
            public double At;
            public long Order;
            public TransportEvent Event;
        }

        public sealed class Endpoint : INetTransport
        {
            readonly MemoryNetwork net;
            internal readonly Dictionary<int, Link> links = new Dictionary<int, Link>();
            readonly List<Packet> inbox = new List<Packet>();

            internal Endpoint(MemoryNetwork net)
            {
                this.net = net;
            }

            public void Send(int peer, ReadOnlySpan<byte> data, bool reliable)
            {
                if (!links.TryGetValue(peer, out Link link) || !link.Open)
                    return;
                if (!reliable && net.random.NextDouble() < link.Loss)
                {
                    net.Lost++;
                    return;
                }

                double at = net.Now + link.Latency + (net.random.NextDouble() * 2 - 1) * link.Jitter;
                if (reliable)
                {
                    // In order: never before the reliable message sent ahead of it.
                    at = Math.Max(at, link.LastReliable);
                    link.LastReliable = at;
                }

                net.Sent++;
                net.Bytes += data.Length;
                link.To.Deliver(at, new TransportEvent
                {
                    Kind = TransportEventKind.Data, Peer = link.PeerOnOtherSide, Data = data.ToArray(),
                });
            }

            internal void Deliver(double at, TransportEvent e)
                => inbox.Add(new Packet { At = at, Order = net.order++, Event = e });

            public bool Poll(out TransportEvent e)
            {
                int best = -1;
                for (int i = 0; i < inbox.Count; i++)
                {
                    Packet p = inbox[i];
                    if (p.At > net.Now)
                        continue;
                    if (best < 0 || p.At < inbox[best].At || (p.At == inbox[best].At && p.Order < inbox[best].Order))
                        best = i;
                }

                if (best < 0)
                {
                    e = default;
                    return false;
                }

                e = inbox[best].Event;
                inbox.RemoveAt(best);
                return true;
            }

            public void Disconnect(int peer)
            {
                if (!links.TryGetValue(peer, out Link link) || !link.Open)
                    return;
                link.Open = false;
                Link back = link.To.links[link.PeerOnOtherSide];
                back.Open = false;
                link.To.Deliver(net.Now + link.Latency, new TransportEvent
                {
                    Kind = TransportEventKind.Disconnected, Peer = link.PeerOnOtherSide,
                });
            }
        }

        readonly Random random;
        long order;
        int nextPeer = 1;

        public MemoryNetwork(int seed)
        {
            random = new Random(seed);
        }

        /// <summary>Milliseconds; the probe advances it.</summary>
        public double Now { get; set; }

        public long Sent { get; private set; }
        public long Lost { get; private set; }
        public long Bytes { get; private set; }

        public Endpoint CreateEndpoint() => new Endpoint(this);

        /// <summary>
        /// Connects a peer to the host. The host hears Connected after one
        /// latency; the peer may send at once, it calls the host peer 0.
        /// </summary>
        public int Connect(Endpoint host, Endpoint peer, double latencyMs, double jitterMs, double loss)
        {
            int id = nextPeer++;
            host.links[id] = new Link { To = peer, PeerOnOtherSide = 0, Latency = latencyMs, Jitter = jitterMs, Loss = loss };
            peer.links[0] = new Link { To = host, PeerOnOtherSide = id, Latency = latencyMs, Jitter = jitterMs, Loss = loss };
            host.Deliver(Now + latencyMs, new TransportEvent { Kind = TransportEventKind.Connected, Peer = id });
            return id;
        }
    }
}
