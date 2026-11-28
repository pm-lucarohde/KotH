using System;
using System.Collections.Generic;
using System.Globalization;
using KotH.Client.Localization;
using KotH.Shared.Match;
using KotH.Shared.Net;
using static KotH.Client.Steam.SteamApi;

namespace KotH.Client.Steam
{
    /// <summary>A round open on Steam, as the lobby list shows it; PingMs -1 when unknown.</summary>
    public readonly record struct LobbyListing(ulong Id, string Host, string Map, int Players, int PingMs,
                                               bool Private, string[] Regions, string[] Languages, bool Modded, MatchMode Mode = MatchMode.Solo,
                                               int Max = 0, bool Running = false)
    {
        public int Capacity => Max > 0 ? Max : HillMatch.MaxPlayers(Mode);
        public bool Full => Players >= Capacity;
    }

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
            lobby.mode = flags.Mode;
            lobby.max = flags.Max;
            const int Public = 2;   // k_ELobbyTypePublic
            // Room for the largest mode: the host may change it in the lobby, its own capacity turns joins away.
            Create(lobby, Public, HillMatch.FreeMaxPlayers, () =>
            {
                lobby.Set("map", lobby.map);
                lobby.Set("maptitle", lobby.mapTitle ?? lobby.map);
                lobby.Set("name", host);
                // Flags as Steam filters them, by equality: one key per region and language.
                lobby.Set("access", flags.Private ? "private" : "public");
                foreach (string region in flags.Regions)
                    lobby.Set("region_" + region, "1");
                foreach (string language in flags.Languages)
                    lobby.Set("lang_" + language, "1");
                lobby.Set("modded", flags.Modded ? "1" : "0");
                lobby.Set("mode", HillMatch.ModeName(lobby.mode));
                lobby.Set("running", lobby.running == true ? "1" : "0");
                if (lobby.max > 0)
                    lobby.Set("max", lobby.max.ToString(CultureInfo.InvariantCulture));
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
                    lobby.Failure = Loc.F("steam.no_lobby", result);
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
                        2 => Loc.T("steam.round_gone"),
                        3 => Loc.T("steam.round_closed"),
                        4 => Loc.T("steam.round_full"),
                        0 => Loc.T("board.no_answer"),
                        _ => Loc.F("steam.enter_failed", response),
                    });
                    return;
                }
                var lobby = new SteamLobby(hosting: false) { Id = id };
                lobby.HostName = lobby.Get("name");
                lobby.Map = lobby.Get("map");
                lobby.Build = lobby.Get("build");
                lobby.Private = lobby.Get("access") == "private";
                lobby.IsParty = lobby.Get("kind") == "party";
                Console.WriteLine($"[steam] in lobby {id}");
                done(lobby, null);
            });
        }

        const float PingWeight = 0.75f, PlayerWeight = 0.15f, LobbyWeight = 0.10f, MaxScoredPingMs = 200f;

        /// <summary>The open, non-full KotH rounds of this build that pass the filter, ranked by 75 %
        /// ping (lower is better), 15 % player count (more is better) and 10 % waiting in lobby.</summary>
        public static void Search(LobbyFilter filter, Action<List<LobbyListing>> done)
        {
            Filter("game", Game);
            Filter("build", NetProtocol.BuildText);
            if (filter.Private is bool locked)
                Filter("access", locked ? "private" : "public");
            if (!string.IsNullOrEmpty(filter.Region))
                Filter("region_" + filter.Region, "1");
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
                    string[] regions = Marked(id, "region_", LobbyFlags.AllRegions), languages = Marked(id, "lang_", LobbyFlags.AllLanguages);
                    var listing = new LobbyListing(id, Data(id, "name") ?? "?", Data(id, "maptitle") ?? Data(id, "map"), n, SteamClient.EstimatePing(Data(id, "ping")),
                                                   Data(id, "access") == "private", regions, languages, Data(id, "modded") == "1",
                                                   HillMatch.ParseMode(Data(id, "mode")) ?? MatchMode.Solo,
                                                   int.TryParse(Data(id, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int most) ? most : 0,
                                                   Data(id, "running") == "1");
                    if (!listing.Full)
                        list.Add(listing);
                }
                list.Sort(CompareListings);
                done(list);
            });
        }

        /// <summary>Weighted lobby score in [0, 1]: 75 % low ping (0..200 ms, unknown = 0),
        /// 15 % fill relative to the mode's max (12 normal, 16 Free) and 10 % waiting in lobby.</summary>
        public static float RankScore(in LobbyListing r)
        {
            float ping = r.PingMs < 0 ? 0f : 1f - Math.Clamp(r.PingMs, 0, MaxScoredPingMs) / MaxScoredPingMs;
            float players = Math.Clamp(r.Players / (float)HillMatch.MaxPlayers(r.Mode), 0f, 1f);
            float waiting = r.Running ? 0f : 1f;
            return ping * PingWeight + players * PlayerWeight + waiting * LobbyWeight;
        }

        static int CompareListings(LobbyListing a, LobbyListing b)
        {
            int byScore = RankScore(b).CompareTo(RankScore(a));
            if (byScore != 0)
                return byScore;
            if (a.PingMs < 0 != b.PingMs < 0)
                return a.PingMs < 0 ? 1 : -1;
            int byPing = a.PingMs.CompareTo(b.PingMs);
            return byPing != 0 ? byPing : b.Players.CompareTo(a.Players);
        }

        // The codes a lobby marks with key prefix + code = "1".
        static string[] Marked(ulong id, string prefix, (string Code, string Name)[] all)
        {
            var marked = new List<string>();
            foreach ((string code, _) in all)
                if (Data(id, prefix + code) == "1")
                    marked.Add(code);
            return marked.ToArray();
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

        /// <summary>The host: the map the game is on now and its title (a custom map's id says little), for the list.</summary>
        public void SetMap(string value, string title)
        {
            if (value == null || value == map)
                return;
            map = value;
            mapTitle = title;
            if (Id == 0)
                return;
            Set("map", value);
            Set("maptitle", title ?? value);
        }

        string mapTitle;
        MatchMode mode;
        int max;

        /// <summary>The most players the host lets in (Host page, Round), for the list; 0 unknown.</summary>
        public int Max => int.TryParse(Get("max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) ? m : max;

        /// <summary>The host: the most it lets in now (Round, a mode change), for the list.</summary>
        public void SetMax(int value)
        {
            if (value == max)
                return;
            max = value;
            if (Id != 0)
                Set("max", value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>The host changed the mode in the lobby (Esc, Round): the list shows the new one (Luca).</summary>
        public void SetMode(MatchMode value)
        {
            if (value == mode)
                return;
            mode = value;
            if (Id != 0)
                Set("mode", HillMatch.ModeName(value));
        }

        string map;
        bool? running;

        /// <summary>The host: whether a round is currently running (not waiting in the lobby), for ranking.</summary>
        public void SetRunning(bool value)
        {
            if (value == running)
                return;
            running = value;
            if (Id != 0)
                Set("running", value ? "1" : "0");
        }

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
