using System;
using System.Diagnostics;
using System.Text;
using KotH.Probes.Harness;
using KotH.Shared.Hosting;
using KotH.Shared.Net;

namespace KotH.Probes.Probes
{
    /// <summary>Direct IP's encryption. The links: a handshake agrees keys both ways, sealed packets hide
    /// their text, a flipped bit, a replay or a counter older than the window is dropped, packets out of
    /// order within it pass, a wrong signature or another host key than the invite's ends it. Then a real
    /// game over UDP on loopback: a guest with the invite joins, one pinned to another key never does.</summary>
    public sealed class SecureProbe : Probe
    {
        protected override string Tag => "secure";

        int failures;

        public override int Run(HeadlessWorld unused, int maxTicks)
        {
            Links();
            Udp();
            Log(failures == 0 ? "[secure] PASSED" : $"[secure] {failures} errors");
            return failures == 0 ? 0 : 1;
        }

        void Links()
        {
            using HostKey key = HostKey.Fresh();
            (SecureLink peer, SecureLink host, string why) = Shake(key, key.Fingerprint);
            Check(why == null && peer.Ready && host.Ready && peer.HostFingerprint == key.Fingerprint,
                  $"handshake, host key {key.Fingerprint} ({key.Fingerprint.Length} characters) as pinned");

            byte[] text = Encoding.UTF8.GetBytes("Hello from Luca, the secret name");
            byte[] packet = Sealed(peer, text);
            Check(packet.AsSpan().IndexOf("Luca"u8) < 0 && packet.Length == text.Length + SecureLink.Overhead,
                  $"sealed packet hides its text, {SecureLink.Overhead} bytes more");
            Check(Opened(host, packet) is { } opened && opened.AsSpan().SequenceEqual(text), "the host opens it");
            Check(Opened(host, packet) == null, "the same packet again (replay) is dropped");
            byte[] back = Sealed(host, text);
            Check(Opened(peer, back) is { } b && b.AsSpan().SequenceEqual(text), "the other way too");
            Check(Opened(host, back) == null, "a packet sealed by the host is not one for the host");

            byte[] flipped = Sealed(peer, text);
            flipped[20] ^= 1;
            Check(Opened(host, flipped) == null, "a flipped bit is dropped");
            byte[] late = Sealed(peer, text), early = Sealed(peer, text);
            Check(Opened(host, early) != null && Opened(host, late) != null, "out of order within the window passes");
            byte[] old = Sealed(peer, text);
            for (int i = 0; i < 70; i++)
                Opened(host, Sealed(peer, text));
            Check(Opened(host, old) == null, "older than the window (64) is dropped");
            peer.Dispose();
            host.Dispose();

            using HostKey other = HostKey.Fresh();
            (peer, host, why) = Shake(other, key.Fingerprint);
            Check(why == "The host's key does not match the invite" && !peer.Ready, $"another host key than the invite's: \"{why}\"");
            peer.Dispose();
            host.Dispose();

            (peer, host, why) = Shake(key, null, forge: true);
            Check(why != null && !peer.Ready, $"a forged signature: \"{why}\"");
            peer.Dispose();
            host.Dispose();
        }

        static (SecureLink, SecureLink, string) Shake(HostKey key, string pinned, bool forge = false)
        {
            SecureLink peer = SecureLink.Peer(out byte[] hello);
            SecureLink host = SecureLink.Host(key, hello, out byte[] reply);
            if (forge)
                reply[^5] ^= 0x40;
            peer.TakeReply(reply, pinned, out string why);
            return (peer, host, why);
        }

        static byte[] Sealed(SecureLink link, byte[] text)
        {
            byte[] into = new byte[text.Length + SecureLink.Overhead];
            link.Seal(text, into);
            return into;
        }

        // The message opened from a sealed packet, null when the link drops it.
        static byte[] Opened(SecureLink link, byte[] packet)
        {
            byte[] into = new byte[Math.Max(0, packet.Length - SecureLink.Overhead)];
            int length = link.Open(packet, into);
            return length < 0 ? null : into[..length];
        }

        // Host, founder and two guests over real sockets, for at most a few seconds.
        void Udp()
        {
            const int Port = 47791;
            using HostKey key = HostKey.Fresh();
            using var hostWorld = new HeadlessWorld();
            hostWorld.LoadArena(Maps.Test);
            hostWorld.StartMatch();
            hostWorld.Simulation.Deactivate();
            var wall = Stopwatch.StartNew();
            double Now() => wall.Elapsed.TotalMilliseconds;

            using LiteNetTransport hostLine = LiteNetTransport.Host(Port, key);
            var host = new HostSession(hostLine, Now());
            var founder = new PeerSession(new HeadlessNetWorld(hostWorld), host.LocalTransport, () => default, "Host", founder: true, Now());
            host.AttachFounder(founder);

            Invites.TryParse(key.Invite("127.0.0.1", Port), out string address, out int port, out string pin);
            using var guestWorld = new HeadlessWorld();
            guestWorld.Simulation.Deactivate();
            using LiteNetTransport guestLine = LiteNetTransport.Join(address, port, pin);
            var guest = new PeerSession(new HeadlessNetWorld(guestWorld), guestLine, () => default, "Guest", founder: false, Now());

            using HostKey stranger = HostKey.Fresh();
            using var wrongWorld = new HeadlessWorld();
            wrongWorld.Simulation.Deactivate();
            using LiteNetTransport wrongLine = LiteNetTransport.Join("127.0.0.1", Port, stranger.Fingerprint);
            var wrong = new PeerSession(new HeadlessNetWorld(wrongWorld), wrongLine, () => default, "Wrong", founder: false, Now());

            while (Now() < 6000.0 && !(guest.Joined && guest.ConfirmedTick > 80 && wrong.HostLost))
            {
                host.Update(Now());
                founder.Update(Now());
                guest.Update(Now());
                wrong.Update(Now());
                System.Threading.Thread.Sleep(2);
            }
            Check(guest.Joined && guest.ConfirmedTick > 80 && guestLine.HostFingerprint == key.Fingerprint,
                  $"a guest with the invite plays over UDP (tick {guest.ConfirmedTick}, {guestLine.Dropped} dropped)");
            Check(wrong.HostLost && !wrong.Joined && wrongLine.DisconnectReason == "The host's key does not match the invite",
                  $"a guest pinned to another key stops: \"{wrongLine.DisconnectReason}\"");
            Check(hostLine.Dropped == 0, $"the host dropped {hostLine.Dropped} packets");
        }

        void Check(bool ok, string what)
        {
            if (!ok)
                failures++;
            Log($"[secure] {(ok ? "ok    " : "ERROR ")} {what}");
        }
    }
}
