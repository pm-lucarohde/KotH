using System;
using System.Collections.Generic;
using System.Globalization;
using KotH.Shared.Net;
using static KotH.Client.Steam.SteamApi;

namespace KotH.Client.Steam
{
    /// <summary>A round open on Steam, as the lobby list shows it.</summary>
    public readonly record struct LobbyListing(ulong Id, string Host, string Map, int Players);

    /// <summary>A KotH round as a Steam lobby: the host opens one (Open) with its name, map, build and
    /// player count as lobby data, others find it (Search) and enter it (Enter); the owner is the host
    /// to connect to. App 480 is shared by every Steamworks developer, so searches filter by game and
    /// build. Closed to joins (SetJoinable) it leaves the searches.</summary>
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

        /// <summary>Why opening it failed; null while it goes well.</summary>
        public string Failure { get; private set; }

        /// <summary>The host to connect to: the lobby's owner.</summary>
        public ulong Owner => Id == 0 ? 0 : SteamAPI_ISteamMatchmaking_GetLobbyOwner(SteamClient.Matchmaking, Id);

        /// <summary>Someone entered the lobby (SteamID).</summary>
        public event Action<ulong> MemberEntered;

        /// <summary>Someone left it, by choice or not (SteamID).</summary>
        public event Action<ulong> MemberLeft;

        /// <summary>Opens a public lobby for this host's round on the map; it is there a moment later.</summary>
        public static SteamLobby Open(string map, string host)
        {
            var lobby = new SteamLobby(hosting: true) { HostName = host };
            lobby.map = map;
            const int Public = 2;   // k_ELobbyTypePublic
            SteamClient.Await(SteamAPI_ISteamMatchmaking_CreateLobby(SteamClient.Matchmaking, Public, NetWorld.MaxPlayers), (p, ok) =>
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
                lobby.Set("map", lobby.map);
                lobby.Set("name", host);
                if (lobby.players >= 0)
                    lobby.Set("players", lobby.players.ToString(CultureInfo.InvariantCulture));
                if (!lobby.joinable)
                    SteamAPI_ISteamMatchmaking_SetLobbyJoinable(SteamClient.Matchmaking, id, 0);
                Console.WriteLine($"[steam] lobby {id} open on {map}");
            });
            return lobby;
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
                Console.WriteLine($"[steam] in lobby {id}, host {lobby.Owner}");
                done(lobby, null);
            });
        }

        /// <summary>The open KotH rounds of this build, on any map, fullest first.</summary>
        public static void Search(Action<List<LobbyListing>> done)
        {
            Filter("game", Game);
            Filter("build", NetProtocol.BuildText);
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
                    list.Add(new LobbyListing(id, Data(id, "name") ?? "?", Data(id, "map"), n));
                }
                list.Sort((a, b) => b.Players.CompareTo(a.Players));
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

        /// <summary>Invites a friend; Steam shows it in their chat with a Join button.</summary>
        public bool Invite(ulong friend)
            => Id != 0 && SteamAPI_ISteamMatchmaking_InviteUserToLobby(SteamClient.Matchmaking, Id, friend) != 0;

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
            if (Id != 0 && SteamClient.Ready)
                SteamAPI_ISteamMatchmaking_LeaveLobby(SteamClient.Matchmaking, Id);
            Id = 0;
        }
    }
}
