using System;
using System.Collections.Generic;
using System.Globalization;
using KotH.Shared.Match;
using KotH.Shared.Net;
using static KotH.Client.Steam.SteamApi;

namespace KotH.Client.Steam
{
    /// <summary>A round open on Steam, as the lobby list shows it; PingMs -1 when unknown.</summary>
    public readonly record struct LobbyListing(ulong Id, string Host, string Map, int Players, int PingMs,
                                               bool Private, string Country, string[] Languages, bool Modded, MatchMode Mode = MatchMode.Solo);

    /// <summary>A KotH round as a Steam lobby: the host opens one (Open) with its name, map, build, player
    /// count, flags and ping location as lobby data, others find it (Search) and enter it (Enter); the
    /// owner is the host to connect to. App 480 is shared by every Steamworks developer, so searches
    /// filter by game and build. Closed to joins (SetJoinable) it leaves the searches.</summary>
    public sealed unsafe class SteamLobby : IDisposable
    {
        const string Game = "KotH";

        // Callback id of LobbyChatUpdate_t; the others come as call results.
        const int ChatUpdateId = 506;

        // EChatMemberStateChange: left, disconnected, kicked, banned.
        const uint Gone = 2 | 4 | 8 | 16;

        bool joinable = true;
        int players = -1;
        bool disposed;

        SteamLobby(bool hosting)
        {
            Hosting = hosting;
            SteamClient.Callback += OnCallback;
        }

        /// <summary>The lobby; 0 until Steam has made it.</summary>
        public ulong Id { get; private set; }

        public bool Hosting { get; }

        /// <summary>The host's name as the lobby says it.</summary>
        public string HostName { get; private set; }

        /// <summary>A joiner: the host's map and build as the lobby says them.</summary>
        public string Map { get; private set; }
        public string Build { get; private set; }

        /// <summary>Whether a password guards the round (the host checks it).</summary>
        public bool Private { get; private set; }

        /// <summary>A party's lobby (SteamParty), not a round's.</summary>
        public bool IsParty { get; private set; }

        /// <summary>A joiner: the friend it came with (an accepted invite, a friend's Join, the party's
        /// leader), 0 for none; teams join them (<see cref="PartnerNetId"/>).</summary>
        public ulong CameWith { get; set; }

        /// <summary>Why opening it failed; null while it goes well.</summary>
        public string Failure { get; private set; }

        /// <summary>The host to connect to: the lobby's owner.</summary>
        public ulong Owner => Id == 0 ? 0 : SteamAPI_ISteamMatchmaking_GetLobbyOwner(SteamClient.Matchmaking, Id);

        /// <summary>Someone entered the lobby (SteamID).</summary>
        public event Action<ulong> MemberEntered;

        /// <summary>Someone left it, by choice or not (SteamID).</summary>
        public event Action<ulong> MemberLeft;

        /// <summary>Opens a listed lobby for this host's round on the map; it is there a moment later.</summary>
        public static SteamLobby Open(string map, LobbyFlags flags)
        {
            string host = flags.Name;
            var lobby = new SteamLobby(hosting: true) { HostName = host, Private = flags.Private };
            lobby.map = map;
            const int Public = 2;   // k_ELobbyTypePublic
            Create(lobby, Public, NetWorld.MaxPlayers, () =>
            {
                lobby.Set("map", lobby.map);
                lobby.Set("name", host);
                // Flags as Steam filters them, by equality: one key per language.
                lobby.Set("access", flags.Private ? "private" : "public");
                lobby.Set("country", flags.Country ?? "");
                foreach (string language in flags.Languages)
                    lobby.Set("lang_" + language, "1");
                lobby.Set("modded", flags.Modded ? "1" : "0");
                lobby.Set("mode", HillMatch.ModeName(flags.Mode));
                lobby.Set("host", SteamClient.SteamId.ToString(CultureInfo.InvariantCulture));
                lobby.Refresh(force: true);
                if (lobby.players >= 0)
                    lobby.Set("players", lobby.players.ToString(CultureInfo.InvariantCulture));
                if (!lobby.joinable)
                    SteamAPI_ISteamMatchmaking_SetLobbyJoinable(SteamClient.Matchmaking, lobby.Id, 0);
                Console.WriteLine($"[steam] lobby {lobby.Id} open on {map}");
            });
            return lobby;
        }

        /// <summary>Opens a party (SteamParty): friends only, two members, never in a round's search.</summary>
        public static SteamLobby OpenParty()
        {
            var lobby = new SteamLobby(hosting: true) { IsParty = true };
            const int FriendsOnly = 1;   // k_ELobbyTypeFriendsOnly
            Create(lobby, FriendsOnly, SteamParty.MaxMembers, () =>
            {
                lobby.Set("kind", "party");
                Console.WriteLine($"[steam] party {lobby.Id} open");
            });
            return lobby;
        }

        // Asks Steam for a lobby; once made (and not given up) it has the game's keys, then the caller's.
        static void Create(SteamLobby lobby, int type, int members, Action filled)
        {
            SteamClient.Await(SteamAPI_ISteamMatchmaking_CreateLobby(SteamClient.Matchmaking, type, members), (p, ok) =>
            {
                // LobbyCreated_t: EResult, then the lobby's id (moved by the packing).
                int result = ok ? *(int*)p : 0;
                ulong id = ok ? *(ulong*)(p + Pack) : 0;
                if (lobby.disposed)
                {
                    if (id != 0)
                        SteamAPI_ISteamMatchmaking_LeaveLobby(SteamClient.Matchmaking, id);
                    return;
                }
                if (result != 1 || id == 0)
                {
                    lobby.Failure = $"Steam made no lobby (result {result})";
                    Console.WriteLine($"[steam] {lobby.Failure}");
                    return;
                }
                lobby.Id = id;
                lobby.Set("game", Game);
                lobby.Set("build", NetProtocol.BuildText);
                filled();
            });
        }

        /// <summary>Enters a lobby; done gets it with its owner known, or null and why not.</summary>
        public static void Enter(ulong id, Action<SteamLobby, string> done)
        {
            SteamClient.Await(SteamAPI_ISteamMatchmaking_JoinLobby(SteamClient.Matchmaking, id), (p, ok) =>
            {
                // LobbyEnter_t: lobby, permissions, locked, EChatRoomEnterResponse at 16.
                uint response = ok ? *(uint*)(p + 16) : 0;
                if (response != 1)
                {
                    if (ok)
                        SteamAPI_ISteamMatchmaking_LeaveLobby(SteamClient.Matchmaking, id);
                    done(null, response switch
                    {
                        2 => "The round is gone",
                        3 => "The round is closed",
                        4 => "The round is full",
                        0 => "Steam did not answer",
                        _ => $"Steam let us not in ({response})",
                    });
                    return;
                }
                var lobby = new SteamLobby(hosting: false) { Id = id };
                lobby.HostName = lobby.Get("name");
                lobby.Map = lobby.Get("map");
                lobby.Build = lobby.Get("build");
                lobby.Private = lobby.Get("access") == "private";
                lobby.IsParty = lobby.Get("kind") == "party";
                Console.WriteLine($"[steam] in lobby {id}, host {lobby.Owner}");
                done(lobby, null);
            });
        }

        /// <summary>The open KotH rounds of this build that pass the filter, on any map, nearest first
        /// (by the ping Steam estimates to the host); unknown pings last, fullest first among them.</summary>
        public static void Search(LobbyFilter filter, Action<List<LobbyListing>> done)
        {
            Filter("game", Game);
            Filter("build", NetProtocol.BuildText);
            if (filter.Private is bool locked)
                Filter("access", locked ? "private" : "public");
            if (!string.IsNullOrEmpty(filter.Country))
                Filter("country", filter.Country);
            if (!string.IsNullOrEmpty(filter.Language))
                Filter("lang_" + filter.Language, "1");
            if (filter.Modded is bool modded)
                Filter("modded", modded ? "1" : "0");
            if (filter.Mode is MatchMode mode)
                Filter("mode", HillMatch.ModeName(mode));
            SteamAPI_ISteamMatchmaking_AddRequestLobbyListResultCountFilter(SteamClient.Matchmaking, 100);
            const int Worldwide = 3;   // k_ELobbyDistanceFilterWorldwide
            SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter(SteamClient.Matchmaking, Worldwide);
            SteamClient.Await(SteamAPI_ISteamMatchmaking_RequestLobbyList(SteamClient.Matchmaking), (p, ok) =>
            {
                var list = new List<LobbyListing>();
                int count = ok ? (int)*(uint*)p : 0;
                for (int i = 0; i < count; i++)
                {
                    ulong id = SteamAPI_ISteamMatchmaking_GetLobbyByIndex(SteamClient.Matchmaking, i);
                    int.TryParse(Data(id, "players"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n);
                    var languages = new List<string>();
                    foreach ((string code, _) in LobbyFlags.AllLanguages)
                        if (Data(id, "lang_" + code) == "1")
                            languages.Add(code);
                    list.Add(new LobbyListing(id, Data(id, "name") ?? "?", Data(id, "map"), n, SteamClient.EstimatePing(Data(id, "ping")),
                                              Data(id, "access") == "private", Data(id, "country") ?? "", languages.ToArray(), Data(id, "modded") == "1",
                                              HillMatch.ParseMode(Data(id, "mode")) ?? MatchMode.Solo));
                }
                list.Sort((a, b) => (a.PingMs < 0, b.PingMs < 0) switch
                {
                    (false, false) => a.PingMs.CompareTo(b.PingMs),
                    (true, true) => b.Players.CompareTo(a.Players),
                    (var x, _) => x ? 1 : -1,
                });
                done(list);
            });
        }

        // String filters for the next RequestLobbyList, compared for equality.
        static void Filter(string key, string value)
        {
            fixed (byte* k = Z(key), v = Z(value))
                SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter(SteamClient.Matchmaking, k, v, 0);
        }

        static string Data(ulong lobby, string key)
        {
            fixed (byte* k = Z(key))
                return Text(SteamAPI_ISteamMatchmaking_GetLobbyData(SteamClient.Matchmaking, lobby, k)) is { Length: > 0 } s ? s : null;
        }

        string Get(string key) => Data(Id, key);

        void Set(string key, string value)
        {
            fixed (byte* k = Z(key), v = Z(value))
                SteamAPI_ISteamMatchmaking_SetLobbyData(SteamClient.Matchmaking, Id, k, v);
        }

        /// <summary>Once in a while (NetClient's frames): this player's ping location as member data (the
        /// choice of the next host), the owner's as lobby data (the list's estimate); Steam measures it a few
        /// seconds after the start.</summary>
        public void Refresh(bool force = false)
        {
            double now = Environment.TickCount64 / 1000.0;
            if (Id == 0 || !force && now < nextRefresh)
                return;
            nextRefresh = now + 10.0;
            string location = SteamClient.PingLocation();
            if (location == null)
                return;
            SetMemberData("ping", location);
            // The owner hosts (or soon will, after a host change).
            SetData("ping", location);
        }

        double nextRefresh;

        /// <summary>The members (SteamIDs), this player included.</summary>
        public List<ulong> Members()
        {
            var list = new List<ulong>();
            Members(list);
            return list;
        }

        /// <summary>The members into a list of the caller's (cleared first), so a frame allocates nothing.</summary>
        public void Members(List<ulong> into)
        {
            into.Clear();
            int count = Id == 0 ? 0 : SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(SteamClient.Matchmaking, Id);
            for (int i = 0; i < count; i++)
                into.Add(SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(SteamClient.Matchmaking, Id, i));
        }

        /// <summary>This player's data for the others (NetId, the host it plays with); kept while unchanged.</summary>
        public void SetMemberData(string key, string value)
        {
            if (Id == 0 || mine.TryGetValue(key, out string was) && was == value)
                return;
            mine[key] = value;
            fixed (byte* k = Z(key), v = Z(value))
                SteamAPI_ISteamMatchmaking_SetLobbyMemberData(SteamClient.Matchmaking, Id, k, v);
        }

        readonly Dictionary<string, string> mine = new Dictionary<string, string>();

        /// <summary>The lobby's data, null when none (another member's view of who hosts, who is next).</summary>
        public string Data(string key) => Id == 0 ? null : Get(key);

        /// <summary>The owner only: lobby data for everyone; kept while unchanged.</summary>
        public void SetData(string key, string value)
        {
            if (Id == 0 || Owner != SteamClient.SteamId || Get(key) == value)
                return;
            Set(key, value);
        }

        /// <summary>The owner only: hands the lobby on (its data, and who new players connect to).</summary>
        public bool SetOwner(ulong member)
            => Id != 0 && member != 0 && SteamAPI_ISteamMatchmaking_SetLobbyOwner(SteamClient.Matchmaking, Id, member) != 0;

        /// <summary>Whom the owner hands the lobby to when it leaves (NetClient's choice of the next host).</summary>
        public ulong Heir { get; set; }

        /// <summary>A member's data (another player's ping location, NetId), null when none.</summary>
        public string MemberData(ulong member, string key)
        {
            fixed (byte* k = Z(key))
                return Id == 0 ? null : Text(SteamAPI_ISteamMatchmaking_GetLobbyMemberData(SteamClient.Matchmaking, Id, member, k)) is { Length: > 0 } s ? s : null;
        }

        /// <summary>The host: whether others may still join; closed, searches no longer find it.</summary>
        public void SetJoinable(bool open)
        {
            if (open == joinable)
                return;
            joinable = open;
            if (Id != 0)
                SteamAPI_ISteamMatchmaking_SetLobbyJoinable(SteamClient.Matchmaking, Id, open ? (byte)1 : (byte)0);
        }

        /// <summary>The host: the map the game is on now, for the list.</summary>
        public void SetMap(string value)
        {
            if (value == null || value == map)
                return;
            map = value;
            if (Id != 0)
                Set("map", value);
        }

        string map;

        /// <summary>The host: players in the round, bots included, for the list.</summary>
        public void SetPlayers(int count)
        {
            if (count == players)
                return;
            players = count;
            if (Id != 0)
                Set("players", count.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Whether someone is in this lobby.</summary>
        public bool Has(ulong member)
        {
            if (Id == 0)
                return false;
            int count = SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(SteamClient.Matchmaking, Id);
            for (int i = 0; i < count; i++)
                if (SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(SteamClient.Matchmaking, Id, i) == member)
                    return true;
            return false;
        }

        /// <summary>Invites a friend; Steam shows it in their chat with a Join button. A private round
        /// lets invited friends in without the password.</summary>
        public bool Invite(ulong friend)
        {
            if (Id == 0 || SteamAPI_ISteamMatchmaking_InviteUserToLobby(SteamClient.Matchmaking, Id, friend) == 0)
                return false;
            invited.Add(friend);
            SetMemberData("invited", string.Join(",", invited));
            return true;
        }

        readonly HashSet<ulong> invited = new HashSet<ulong>();

        /// <summary>The host: whether it invited the member, or the member is its party's (they need no password).</summary>
        public bool WasInvited(ulong member) => invited.Contains(member) || SteamParty.Has(member);

        /// <summary>Teams: the NetId of the player this one should be teamed with, 0 for none. That is the
        /// friend it came with, else its party's mate, else a member who invited it; each by its member data.</summary>
        public int PartnerNetId()
        {
            ulong me = SteamClient.SteamId;
            int found = NetIdOf(CameWith);
            if (found == 0)
                found = NetIdOf(SteamParty.Mate);
            string mine = me.ToString(CultureInfo.InvariantCulture);
            foreach (ulong member in Members())
                if (found == 0 && member != me && MemberData(member, "invited") is { } list && Array.IndexOf(list.Split(','), mine) >= 0)
                    found = NetIdOf(member);
            return found;
        }

        int NetIdOf(ulong member)
            => member != 0 && member != SteamClient.SteamId && Has(member)
               && int.TryParse(MemberData(member, "netid"), NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : 0;

        void OnCallback(int id, nint p)
        {
            // LobbyChatUpdate_t: lobby, the member changed, who changed it, the change at 24.
            if (id != ChatUpdateId || Id == 0 || *(ulong*)p != Id)
                return;
            ulong member = *(ulong*)(p + 8);
            uint change = *(uint*)(p + 24);
            if ((change & 1) != 0)
                MemberEntered?.Invoke(member);
            if ((change & Gone) != 0)
                MemberLeft?.Invoke(member);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            SteamClient.Callback -= OnCallback;
            // Leaving as the owner: the heir hosts on, and everyone knows it before Steam says we left.
            if (Id != 0 && SteamClient.Ready && Owner == SteamClient.SteamId && Heir != 0 && Has(Heir))
            {
                string heir = Heir.ToString(CultureInfo.InvariantCulture);
                Set("next", heir);
                Set("host", heir);
                SetOwner(Heir);
            }
            if (Id != 0 && SteamClient.Ready)
                SteamAPI_ISteamMatchmaking_LeaveLobby(SteamClient.Matchmaking, Id);
            Id = 0;
        }
    }
}
