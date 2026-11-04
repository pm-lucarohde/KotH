using System;
using System.Collections.Generic;
using System.Globalization;
using KotH.Client.Steam;
using KotH.Shared.Bots;
using KotH.Shared.Net;

namespace KotH.Client.Net
{
    /// <summary>Host changes over Steam. The lobby says who hosts ("host", the owner writes it) and who would
    /// next ("next": the member with the least estimated ping to the others). The host hands over on a bad
    /// line (median round trip over 90 ms for 10 s, the heir 25 ms better) and when it leaves; when it is
    /// gone without a word (3 s silence) everyone turns to "next". The new host goes on from its final state.</summary>
    public sealed partial class NetClient
    {
        const double SilenceMs = 3000.0, ReconnectMs = 10000.0, BadLineMs = 10000.0, CooldownMs = 60000.0;
        const double BadRtt = 90.0, Better = 25.0;

        readonly HashSet<ulong> deadHosts = new HashSet<ulong>();
        Takeover resumeFrom;              // the newest final state while reconnecting
        double resumedAt, badSince = double.NaN, changedAt = double.NegativeInfinity, nextHeir, nextHand, splitSince = double.NaN;
        string hostAtStart;               // the lobby's "host" when this side began hosting
        bool wasOwner, gaveUp;

        SteamTransport Steam => transport as SteamTransport;

        HostSession NewHost(double now)
        {
            var host = new HostSession(transport, now)
            {
                Invited = peer => Steam?.Invited(peer) ?? false,
                Claims = Claims,
            };
            host.Began += Founded;
            host.CanJoin = () => world.Simulation.Match?.AcceptsJoin ?? true;
            hostAtStart = Text(SteamClient.SteamId);
            return host;
        }

        // A peer that says it was this player: its lobby member data has to say the same.
        bool Claims(int peer, int netId)
            => Steam is not { } steam || Lobby?.MemberData(steam.SteamIdOf(peer), "netid") == netId.ToString(CultureInfo.InvariantCulture);

        static string Text(ulong id) => id.ToString(CultureInfo.InvariantCulture);

        static ulong Id(string text) => ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : 0;

        void FollowHost(double now)
        {
            if (Steam is not { } steam || Lobby is not { Id: not 0 } lobby || gaveUp || Restarting || Session.RestartTo != null)
                return;
            ulong me = SteamClient.SteamId, owner = lobby.Owner;
            if (Session.LocalNetId != 0)
                lobby.SetMemberData("netid", Session.LocalNetId.ToString(CultureInfo.InvariantCulture));
            lobby.SetMemberData("host", Text(Host != null ? me : steam.HostId));
            if (Host != null)
                Hosting(lobby, me, owner, now);
            else
                Guesting(lobby, steam, me, owner, now);
        }

        void Hosting(SteamLobby lobby, ulong me, ulong owner, double now)
        {
            // Handed on (a bad line, or Steam's owner gave it to the one everyone went to): a guest now.
            ulong named = Id(lobby.Data("host"));
            if (owner != me && named != 0 && named != me && Text(named) != hostAtStart && lobby.Has(named))
            {
                Demote(named, now);
                return;
            }
            if (owner != me)
                return;
            wasOwner = true;
            lobby.SetData("host", Text(me));
            if (now >= nextHeir)
            {
                nextHeir = now + 5000.0;
                ulong heir = Heir();
                lobby.Heir = heir;
                lobby.SetData("next", heir != 0 ? Text(heir) : "");
            }
            WatchLine(lobby, me, now);
            Split(lobby, me, now);
        }

        // A bad line for 10 s, and an heir clearly nearer the others: the game goes to the heir.
        void WatchLine(SteamLobby lobby, ulong me, double now)
        {
            if (!(Host.MedianRtt > BadRtt))
            {
                badSince = double.NaN;
                return;
            }
            if (double.IsNaN(badSince))
                badSince = now;
            if (now - badSince < BadLineMs || now - changedAt < CooldownMs)
                return;
            ulong heir = Heir();
            if (heir == 0 || !(MeanPing(heir, me) + Better < MeanPing(me, me)))
                return;
            Console.WriteLine($"[net] bad line ({Host.MedianRtt:F0} ms): the game goes to {heir}");
            HandTo(lobby, heir, now);
        }

        // Everyone else went to another host (they thought this one gone): this one follows them.
        void Split(SteamLobby lobby, ulong me, double now)
        {
            ulong other = 0;
            int others = 0;
            bool agree = true;
            foreach (ulong m in lobby.Members())
            {
                if (m == me)
                    continue;
                others++;
                ulong says = Id(lobby.MemberData(m, "host"));
                agree &= says != 0 && says != me && (other == 0 || says == other);
                other = says;
            }
            if (others == 0 || !agree || !lobby.Has(other))
            {
                splitSince = double.NaN;
                return;
            }
            if (double.IsNaN(splitSince))
                splitSince = now;
            if (now - splitSince > 3000.0)
                HandTo(lobby, other, now);
        }

        void HandTo(SteamLobby lobby, ulong to, double now)
        {
            changedAt = now;
            lobby.SetData("next", Text(to));
            lobby.SetData("host", Text(to));
            lobby.SetOwner(to);
        }

        void Guesting(SteamLobby lobby, SteamTransport steam, ulong me, ulong owner, double now)
        {
            ulong host = steam.HostId;
            bool lost = Session.HostLost || host != 0 && !lobby.Has(host)
                     || Session.Joined && now - Session.LastHeard > SilenceMs
                     || !Session.Joined && Session.ResumeNetId != 0 && now - resumedAt > ReconnectMs;
            if (lost && host != 0)
                deadHosts.Add(host);
            ulong named = Id(lobby.Data("host"));
            ulong want = !lost && named != 0 && !deadHosts.Contains(named) ? named : lost ? Successor() : host;

            // Steam's own choice of owner (after a crash) hands the lobby to the one we all went to.
            if (owner == me && want != me && host != 0 && !lost && now >= nextHand)
            {
                nextHand = now + 2000.0;
                lobby.SetData("host", Text(host));
                if (lobby.Has(host))
                    lobby.SetOwner(host);
            }
            if (want == me)
                Promote(now);
            else if (want != 0 && want != host)
                Retarget(want, now);
        }

        // Who hosts once the host is gone: "next" as the host last named it, else the owner, else the
        // smallest SteamID; never a host already lost.
        ulong Successor()
        {
            SteamLobby lobby = Lobby;
            ulong next = Id(lobby.Data("next"));
            if (next != 0 && !deadHosts.Contains(next) && lobby.Has(next))
                return next;
            ulong owner = lobby.Owner;
            if (owner != 0 && !deadHosts.Contains(owner))
                return owner;
            ulong least = 0;
            foreach (ulong m in lobby.Members())
                if (!deadHosts.Contains(m) && (least == 0 || m < least))
                    least = m;
            return least;
        }

        /// <summary>The member the game would go to: the least mean estimated ping to the others (this
        /// host left out), ties to the smaller SteamID; 0 when alone.</summary>
        ulong Heir()
        {
            if (Lobby == null)
                return 0;
            ulong me = SteamClient.SteamId, best = 0;
            double bestMean = double.MaxValue;
            foreach (ulong m in Lobby.Members())
            {
                if (m == me || deadHosts.Contains(m))
                    continue;
                double mean = MeanPing(m, me);
                if (best == 0 || mean < bestMean || mean == bestMean && m < best)
                {
                    best = m;
                    bestMean = mean;
                }
            }
            return best;
        }

        // A member's mean estimated ping to the others but one; MaxValue when nothing is known.
        double MeanPing(ulong member, ulong without)
        {
            string from = Location(member);
            double sum = 0.0;
            int n = 0;
            foreach (ulong other in Lobby.Members())
            {
                if (other == member || other == without || from == null || Location(other) is not { } to)
                    continue;
                int ms = SteamClient.EstimatePing(to, from);
                if (ms < 0)
                    continue;
                sum += ms;
                n++;
            }
            return n > 0 ? sum / n : double.MaxValue;
        }

        string Location(ulong member) => member == SteamClient.SteamId ? SteamClient.PingLocation() : Lobby.MemberData(member, "ping");

        // This side takes the game over from its newest final state, bots included.
        void Promote(double now)
        {
            Takeover from = Session.Joined ? Session.Takeover() : resumeFrom;
            if (from == null)
            {
                gaveUp = true;
                return;
            }
            Console.WriteLine($"[net] taking the game over at tick {from.Tick}, {from.Roster.Count} in it");
            Steam.HostNow();
            Host = NewHost(now);
            Host.Resume(from, now);
            hostAtStart = Lobby.Data("host");
            PeerSession old = Session;
            Session = new PeerSession(old, Host.LocalTransport, founder: true, now);
            if (!Session.StartFrom(from))
            {
                gaveUp = true;
                return;
            }
            Host.AttachFounder(Session);
            changedAt = now;
            wasOwner = false;
            var bots = new List<int>();
            foreach (NetEvent e in from.Roster)
                if (e.Bot)
                    bots.Add(e.NetId);
            if (bots.Count == 0 || MakeBots == null)
                return;
            BotDirector director = MakeBots(Session, from.Tick);
            Host.Bots = director;
            Session.KnownInputs = director;
            foreach (int id in bots)
                director.Add(id);
        }

        // This host hands over: it says hello to the new one as its player.
        void Demote(ulong to, double now)
        {
            Console.WriteLine($"[net] the game goes on with host {to}");
            resumeFrom = Session.Takeover();
            Host = null;
            DropBots?.Invoke();
            changedAt = now;
            Retarget(to, now);
        }

        // To another host as who this player was; the new host sends the state.
        void Retarget(ulong to, double now)
        {
            if (Session.Joined)
                resumeFrom = Session.Takeover() ?? resumeFrom;
            Console.WriteLine($"[net] reconnecting to host {to}");
            Steam.JoinNow(to);
            PeerSession old = Session;
            Session = new PeerSession(old, transport, founder: false, now);
            resumedAt = now;
        }
    }
}
