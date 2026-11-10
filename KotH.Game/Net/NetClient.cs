using System;
using System.Collections.Generic;
using System.Diagnostics;
using KotH.Client.Steam;
using KotH.Shared.Bots;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Net;
using KotH.Shared.Player;

namespace KotH.Client.Net
{
    /// <summary>The client's share of a networked game: its session and, when it hosts, the host session beside
    /// it, over a Steam lobby (menu) or direct IP (--host, --join). The host session restarts the game on the
    /// round's map and back, this side changes maps and joins anew; over Steam another player takes over from
    /// a host that goes (HostChange.cs). The driver calls Update once per frame.</summary>
    public sealed partial class NetClient : IDisposable
    {
        readonly Stopwatch wall = Stopwatch.StartNew();
        readonly INetTransport transport;
        readonly string address;
        readonly INetWorld world;
        readonly Func<MoveInput> sampleLocal;
        readonly string name;
        readonly FigureLook look;
        Func<bool> joinWhen;
        bool changing;

        /// <param name="lobby">The Steam lobby to host or join through; null for direct IP.</param>
        public NetClient(LaunchOptions options, INetWorld world, Func<MoveInput> sampleLocal, HillMatch round, SteamLobby lobby = null)
        {
            Lobby = lobby;
            if (lobby != null)
                Comms = new Comms(lobby, id => Session.BodyOf(id)?.Entity?.Name);
            this.world = world;
            this.sampleLocal = sampleLocal;
            name = options.Name;
            look = options.Look;
            if (options.Host)
            {
                if (lobby != null)
                    transport = SteamTransport.Host(lobby);
                else
                {
                    // Direct IP: the lasting key signs every handshake; the invite carries its fingerprint.
                    HostKey key = HostKey.LoadOrCreate();
                    transport = LiteNetTransport.Host(options.Port, key);
                    Invite = key.Invite(Invites.LocalAddress(), options.Port);
                    Console.WriteLine($"[net] invite: {Invite}");
                }
                Host = NewHost(Now);
                // A private round: only the key of its password is kept; invited friends need none.
                Host.Password = options.Password != null ? PasswordKey.Make(options.Password) : null;
                // The lobby's round goes to --map (Mount Destiny unless given); the host may choose another.
                Host.StartIn(MatchStage.Lobby, options.Map);
                Session = new PeerSession(world, Host.LocalTransport, sampleLocal, name, founder: true, Now, look: look);
                Host.AttachFounder(Session);
                if (round != null)
                {
                    round.Stage = MatchStage.Lobby;
                    round.MinPlayers = options.MinPlayers;
                }
                address = lobby != null ? "Steam" : $"Port {options.Port}";
            }
            else
            {
                transport = lobby != null ? SteamTransport.Join(lobby) : LiteNetTransport.Join(options.JoinAddress, options.Port, options.JoinKey);
                Session = new PeerSession(world, transport, sampleLocal, options.Name, founder: false, Now, join: false, look: options.Look)
                {
                    Password = options.Password,
                };
                address = lobby != null ? lobby.HostName ?? "the host" : $"{options.JoinAddress}:{options.Port}";
            }
        }

        /// <summary>A direct-IP host's invite code (address, port, key fingerprint) to hand on; else null.</summary>
        public string Invite { get; }

        /// <summary>The Steam lobby of this game, null over direct IP.</summary>
        public SteamLobby Lobby { get; }

        /// <summary>Chat (and voice) with the other players, over Steam only; null over direct IP.</summary>
        public Comms Comms { get; }

        /// <summary>The own player's name as the host gave it ("Luca 2"), else the one asked for.</summary>
        public string OwnName => Session.BodyOf(Session.LocalNetId)?.Entity?.Name ?? name;

        /// <summary>GameRoot: takes the game down and builds this map in this stage for a restart.</summary>
        public Action<string, MatchStage> ChangeMap { get; set; }

        /// <summary>GameRoot: whether the restart's map is loaded and baked.</summary>
        public Func<bool> MapReady { get; set; }

        /// <summary>The host's map is not here: true while it is being fetched (a workshop download),
        /// false when it cannot be; then MapUnavailable names it and the game leaves.</summary>
        public Func<string, bool> FetchMap { get; set; }
        public Action<string> MapUnavailable { get; set; }
        bool gaveUpMap;

        /// <summary>GameRoot: a map's title for the lobby list (a custom map's from its package).</summary>
        public Func<string, string> TitleOf { get; set; }

        /// <summary>GameRoot: the lobby's next map is not here; starts its download before the round.</summary>
        public Action<string> PrefetchMap { get; set; }
        string prefetched;

        /// <summary>GameRoot: a new bot director for the restarted world (or a host change's, whose states
        /// begin at the given tick), thinking from these states.</summary>
        public Func<IFinalStates, int, BotDirector> MakeBots { get; set; }

        /// <summary>GameRoot: this side no longer hosts (a host change); its bot director goes.</summary>
        public Action DropBots { get; set; }

        /// <summary>Between the host's restart and this side's new session.</summary>
        public bool Restarting => changing || Host is { Restarting: true };

        /// <summary>The stage of the game now and, in the lobby, the map its round will be on.</summary>
        public MatchStage Stage => Host?.Stage ?? Session.Stage;
        public string NextMap => Host?.NextMap ?? Session.NextMap;

        /// <summary>A guest from the menu: joins once the host has named the map and ready says it is
        /// loaded (the menu's at once, another after its bake).</summary>
        public void JoinWhenReady(Func<bool> ready) => joinWhen = ready;

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

        public HostSession Host { get; private set; }

        /// <summary>In the game: joined, or back with a new host after a host change.</summary>
        public bool Joined => Session.Joined || Session.ResumeNetId != 0 && !gaveUp;
        public PeerSession Session { get; private set; }

        public double Now => wall.Elapsed.TotalMilliseconds;

        /// <summary>The frame's share on the game thread: the host's work, then what reached the own
        /// session (it may spawn), restarts, joins and the lobby. The ticks follow in Step.</summary>
        public void Update()
        {
            double now = Now;
            Host?.Update(now);
            Session.Exchange(now);
            FollowRestart(now);
            if (Session.MissingMap is { } missing && !gaveUpMap)
            {
                Session.MapInstalled();
                if (Session.MissingMap != null && !(FetchMap?.Invoke(missing) ?? false))
                {
                    gaveUpMap = true;
                    MapUnavailable?.Invoke(missing);
                }
            }
            if (NextMap is { } next && next != prefetched && KotH.MapFormat.MapPackage.IsId(next) && !world.HasMap(next))
            {
                prefetched = next;
                PrefetchMap?.Invoke(next);
            }
            if (joinWhen != null && Session.MapKnown && joinWhen())
            {
                joinWhen = null;
                // Teams: the player it came with, by the NetId that player's lobby member data says.
                Session.Wish = Lobby?.PartnerNetId() ?? 0;
                Session.Join(now);
            }
            Lobby?.Refresh();
            SteamParty.Playing(Lobby?.Id ?? 0);
            Comms?.Update(now);
            FollowHost(now);
            if (Host != null && Lobby != null)
            {
                int players = HillMatch.CountPlayers(Session.Simulation.Bodies);
                Lobby.SetPlayers(players);
                Lobby.SetMap(world.Map, TitleOf?.Invoke(world.Map));
                Lobby.SetJoinable(players < NetWorld.MaxPlayers && !Host.Restarting && Host.CanJoin());
            }
        }

        /// <summary>The own session's ticks, up to where its clock will be ahead ms from now; on the
        /// simulation thread while the game thread draws (SimulationDriver), never during a restart.</summary>
        public void Step(double ahead) => Session.Step(Now, ahead);

        // The host starts a restart where the confirmed round holds (lobby's countdown, round's win
        // pause); everyone, host's own peer too, then changes maps and joins the new timeline.
        void FollowRestart(double now)
        {
            if (Host is { Restarting: false } && Host.Stage != MatchStage.Single && Session.Joined
                && Session.TryConfirmedMatch(out MatchState m) && m.PhaseTicks == 0)
            {
                if (Host.Stage == MatchStage.Lobby && m.Phase == MatchPhase.Countdown)
                {
                    FillTeams(m);
                    Host.Restart(Host.NextMap ?? Maps.Default, MatchStage.Round);
                }
                else if (Host.Stage == MatchStage.Round && m.Phase == MatchPhase.Over)
                    Host.Restart(Maps.Default, MatchStage.Lobby);
            }
            if (Session.RestartTo is { } to && !changing)
            {
                changing = true;
                joinWhen = null;
                ChangeMap?.Invoke(to.Map, to.Stage);
            }
            if (changing && (MapReady?.Invoke() ?? true))
            {
                changing = false;
                if (Host != null)
                    Host.FounderReady();
                else
                    Session = new PeerSession(world, transport, sampleLocal, name, founder: false, now, look: look);
            }
        }

        // Teams (Luca): bots fill the last team for the round, so nobody plays it short.
        void FillTeams(in MatchState m)
        {
            int count = HillMatch.CountPlayers(Session.Simulation.Bodies), size = HillMatch.Size(m.Mode);
            if (MakeBots == null)
                return;
            for (int k = (size - count % size) % size; k > 0 && count < NetWorld.MaxPlayers; k--, count++)
                Host.AddBot($"Bot {Host.BotIds.Count + 1}");
        }

        // A restart begins: the host's own peer starts the new timeline, its bots think anew.
        void Founded()
        {
            Session = new PeerSession(world, Host.LocalTransport, sampleLocal, name, founder: true, Now, look: look);
            Host.AttachFounder(Session);
            List<int> bots = Host.BotIds;
            if (bots.Count == 0 || MakeBots == null)
                return;
            BotDirector director = MakeBots(Session, 0);
            Host.Bots = director;
            Session.KnownInputs = director;
            foreach (int id in bots)
                director.Add(id);
        }

        public float Alpha => Session.Joined ? Session.Alpha(Now) : 1f;

        /// <summary>What the screen says while there is no game to show, else null.</summary>
        public string Notice
            => Session.Rejected switch
            {
                RejectReason.Version => "Rejected: different build than the host",
                RejectReason.Full => "Rejected: the game is full",
                RejectReason.Closed => "Rejected: the round has run too long",
                RejectReason.Password => "Rejected: wrong password",
                _ => KeyRefused is { } why ? why
                   : gaveUp || Session.HostLost && Lobby == null ? "The host is gone, the game is over"
                   : Session.ResumeNetId != 0 && !Session.Joined ? "The host changed, reconnecting ..."
                   : !Session.Joined ? $"Connecting to {address} ..."
                   : null,
            };

        /// <summary>Direct IP: why the host's handshake failed its check (key not the invite's, bad signature), else null.</summary>
        public string KeyRefused
            => transport is LiteNetTransport { DisconnectReason: { } why } && why.StartsWith("The host's", StringComparison.Ordinal) ? why : null;

        /// <summary>The F3 line: ping, lead, rollbacks, waits.</summary>
        public string Readout
            => string.Format(Hud.HudRenderer.Numbers,
                             "{0} | Ping {1:F0} ms | Lead {2:F1} | Rollback {3} (max {4}) | Stalls {5}",
                             Host != null ? "Host" : "Guest", Session.RoundTripMs, Session.Lead,
                             Session.LastRollback, Session.DeepestRollback, Session.Stalls);

        public void Dispose()
        {
            // The lobby first: it names the heir, so the guests go there before the sessions close.
            if (Lobby != null && Host != null)
                Lobby.Heir = Heir();
            Lobby?.Dispose();
            (transport as IDisposable)?.Dispose();
        }
    }
}
