using System;
using System.Diagnostics;
using KotH.Shared.Bots;
using KotH.Shared.Match;
using KotH.Shared.Net;
using KotH.Shared.Player;

namespace KotH.Client.Net
{
    /// <summary>The client's share of a networked game: its session and, when it hosts,
    /// the host session beside it. The driver calls Update once per frame instead of
    /// ticking the world itself; everything else reads.</summary>
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
                Session = new PeerSession(world, transport, sampleLocal, options.Name, founder: false, Now, join: false);
                address = $"{options.JoinAddress}:{options.Port}";
            }
        }

        /// <summary>
        /// The host's bots: they join as players and think from the host's
        /// final states, which its own peer then predicts them with.
        /// </summary>
        public void AddBots(BotDirector director, int count)
        {
            Host.Bots = director;
            Session.KnownInputs = director;
            for (int i = 0; i < count; i++)
                director.Add(Host.AddBot($"Bot {i + 1}"));
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
                RejectReason.Version => "Rejected: different build than the host",
                RejectReason.Full => "Rejected: the game is full",
                RejectReason.Closed => "Rejected: the round has run too long",
                _ => Session.HostLost ? "The host is gone, the game is over"
                   : !Session.Joined ? $"Connecting to {address} ..."
                   : null,
            };

        /// <summary>The F3 line: ping, lead, rollbacks, waits.</summary>
        public string Readout
            => string.Format(Hud.HudRenderer.Numbers,
                             "{0} | Ping {1:F0} ms | Lead {2:F1} | Rollback {3} (max {4}) | Stalls {5}",
                             Host != null ? "Host" : "Guest", Session.RoundTripMs, Session.Lead,
                             Session.LastRollback, Session.DeepestRollback, Session.Stalls);

        public void Dispose() => transport.Dispose();
    }
}
