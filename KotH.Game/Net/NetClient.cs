using System;
using System.Diagnostics;
using KotH.Shared.Match;
using KotH.Shared.Net;
using KotH.Shared.Player;

namespace KotH.Client.Net
{
    /// <summary>
    /// The client's share of a networked game: its session and, when it
    /// hosts, the host session beside it. The driver calls Update once per
    /// frame instead of ticking the world itself; everything else reads.
    /// </summary>
    public sealed class NetClient : IDisposable
    {
        readonly Stopwatch wall = Stopwatch.StartNew();
        readonly LiteNetTransport transport;
        readonly string address;

        public NetClient(LaunchOptions options, INetWorld world, Func<MoveInput> sampleLocal, HillMatch round)
        {
            if (options.Host)
            {
                transport = LiteNetTransport.Host(options.Port);
                Host = new HostSession(transport, Now);
                Session = new PeerSession(world, Host.LocalTransport, sampleLocal, options.Name, founder: true, Now);
                Host.AttachFounder(Session);
                if (round != null)
                {
                    round.MinPlayers = options.MinPlayers;
                    Host.CanJoin = () => round.AcceptsJoin;
                }
                address = $"Port {options.Port}";
            }
            else
            {
                transport = LiteNetTransport.Join(options.JoinAddress, options.Port);
                Session = new PeerSession(world, transport, sampleLocal, options.Name, founder: false, Now);
                address = $"{options.JoinAddress}:{options.Port}";
            }
        }

        public HostSession Host { get; }
        public PeerSession Session { get; }

        public double Now => wall.Elapsed.TotalMilliseconds;

        public void Update()
        {
            double now = Now;
            Host?.Update(now);
            Session.Update(now);
        }

        public float Alpha => Session.Joined ? Session.Alpha(Now) : 1f;

        /// <summary>What the screen says while there is no game to show, else null.</summary>
        public string Notice
            => Session.Rejected switch
            {
                RejectReason.Version => "Abgelehnt: anderer Build als der Host",
                RejectReason.Full => "Abgelehnt: das Spiel ist voll",
                RejectReason.Closed => "Abgelehnt: die Runde läuft schon zu lange",
                _ => Session.HostLost ? "Der Host ist weg, das Spiel ist vorbei"
                   : !Session.Joined ? $"Verbinde mit {address} …"
                   : null,
            };

        /// <summary>The F3 line: ping, lead, rollbacks, waits.</summary>
        public string Readout
            => string.Format(Hud.HudRenderer.Numbers,
                             "{0} | Ping {1:F0} ms | Vorlauf {2:F1} | Rollback {3} (max {4}) | Warten {5}",
                             Host != null ? "Host" : "Gast", Session.RoundTripMs, Session.Lead,
                             Session.LastRollback, Session.DeepestRollback, Session.Stalls);

        public void Dispose() => transport.Dispose();
    }
}
