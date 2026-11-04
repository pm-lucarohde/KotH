using System;
using System.Buffers;
using KotH.Probes.Harness;
using KotH.Shared.Hosting;
using KotH.Shared.Net;
using KotH.Shared.Player;

namespace KotH.Probes.Probes
{
    /// <summary>A private round's door: peers ask for the map, get salt and nonce, and say hello with the
    /// HMAC of the nonce under the password's key. The right password is welcomed, a wrong one and none
    /// are rejected (Password), an invited friend gets in without one, a proof replayed on another
    /// connection fails, and an open round still takes a hello without any proof.</summary>
    public sealed class PasswordProbe : Probe
    {
        protected override string Tag => "password";

        const string Secret = "mushroom summit";
        MemoryNetwork net;
        MemoryNetwork.Endpoint hostEnd;
        HostSession host;
        HeadlessWorld founderWorld;
        PeerSession founder;
        int failures;

        public override int Run(HeadlessWorld unused, int maxTicks)
        {
            Start(PasswordKey.Make(Secret));
            Check(Knock(Secret, invited: false, out _) == MsgType.Welcome, "the right password is welcomed");
            Check(Knock("summit mushroom", invited: false, out _) == MsgType.Reject, "a wrong password is rejected");
            Check(Knock(null, invited: false, out _) == MsgType.Reject, "no password is rejected");
            Check(Knock(null, invited: true, out _) == MsgType.Welcome, "an invited friend needs none");
            Check(Replay() == MsgType.Reject, "a proof recorded on one connection fails on another");
            Stop();

            Start(null);
            Check(Knock(null, invited: false, out bool challenged) == MsgType.Welcome && !challenged, "an open round asks for nothing");
            Stop();
            return failures == 0 ? 0 : 1;
        }

        void Start(PasswordKey key)
        {
            net = new MemoryNetwork(7);
            hostEnd = net.CreateEndpoint();
            host = new HostSession(hostEnd, net.Now) { Password = key };
            founderWorld = new HeadlessWorld();
            founderWorld.LoadArena(Maps.Test);
            founderWorld.StartMatch();
            founderWorld.Simulation.Deactivate();
            founder = new PeerSession(new HeadlessNetWorld(founderWorld), host.LocalTransport, () => default, "Host", founder: true, net.Now);
            host.AttachFounder(founder);
            host.CanJoin = () => true;
            Pump(500.0, null);
        }

        void Stop() => founderWorld.Dispose();

        // A peer asks for the map, answers its challenge (with the password, if any) and says hello:
        // what the host answers to the hello, Welcome or Reject.
        MsgType Knock(string password, bool invited, out bool challenged)
        {
            var end = net.CreateEndpoint();
            net.Connect(hostEnd, end, 20.0, 0.0, 0.0);
            host.Invited = invited ? peer => true : null;
            Send(end, MsgType.Ask, (ref Writer w) => w.Bytes(NetProtocol.BuildId));
            (byte[] salt, byte[] nonce)? challenge = null;
            Pump(300.0, data =>
            {
                var r = new Reader(data);
                if ((MsgType)r.Byte() != MsgType.Map)
                    return;
                r.Text();
                if (r.Byte() == 1)
                    challenge = (r.Bytes(PasswordKey.SaltSize).ToArray(), r.Bytes(PasswordKey.NonceSize).ToArray());
            }, end);
            challenged = challenge != null;
            byte[] proof = challenge != null && password != null
                ? PasswordKey.Proof(PasswordKey.Derive(password, challenge.Value.salt), challenge.Value.nonce) : Array.Empty<byte>();
            lastProof = proof;
            return Hello(end, proof);
        }

        byte[] lastProof;

        // The right password's proof, sent again from a new connection that got its own nonce.
        MsgType Replay()
        {
            Knock(Secret, invited: false, out _);
            byte[] old = lastProof;
            var end = net.CreateEndpoint();
            net.Connect(hostEnd, end, 20.0, 0.0, 0.0);
            host.Invited = null;
            Send(end, MsgType.Ask, (ref Writer w) => w.Bytes(NetProtocol.BuildId));
            Pump(300.0, null, end);
            return Hello(end, old);
        }

        MsgType Hello(MemoryNetwork.Endpoint end, byte[] proof)
        {
            Send(end, MsgType.Hello, (ref Writer w) =>
            {
                w.Bytes(NetProtocol.BuildId);
                w.Double(net.Now);
                w.Text("Guest");
                FigureLook.Default.Write(ref w);
                w.Byte((byte)proof.Length);
                w.Bytes(proof);
            });
            MsgType answer = 0;
            Pump(1500.0, data =>
            {
                if (Environment.GetEnvironmentVariable("KOTH_DEBUG") == "1")
                    Log($"[password]   got {(MsgType)data[0]} ({data.Length} B) at {net.Now:F0}");
                if (answer == 0 && (MsgType)data[0] is MsgType.Welcome or MsgType.Reject)
                    answer = (MsgType)data[0];
            }, end);
            if (Environment.GetEnvironmentVariable("KOTH_DEBUG") == "1")
                Log($"[password] hello with {proof.Length} B proof: {answer}");
            return answer;
        }

        delegate void Write(ref Writer w);

        static void Send(MemoryNetwork.Endpoint end, MsgType type, Write body)
        {
            var buffer = new ArrayBufferWriter<byte>();
            var w = new Writer(buffer);
            w.Byte((byte)type);
            body(ref w);
            end.Send(0, buffer.WrittenSpan, reliable: true);
        }

        // Runs host and founder for a while; what reaches the endpoint goes to seen.
        void Pump(double ms, Action<byte[]> seen, MemoryNetwork.Endpoint end = null)
        {
            for (double until = net.Now + ms; net.Now < until; net.Now += 8.0)
            {
                host.Update(net.Now);
                founder.Update(net.Now);
                while (end != null && end.Poll(out TransportEvent e))
                    if (e.Kind == TransportEventKind.Data && e.Data.Length > 0)
                        seen?.Invoke(e.Data);
            }
        }

        void Check(bool ok, string what)
        {
            if (!ok)
                failures++;
            Log($"[password] {(ok ? "ok    " : "ERROR ")} {what}");
        }
    }
}
