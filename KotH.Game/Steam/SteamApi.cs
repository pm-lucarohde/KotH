using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace KotH.Client.Steam
{
    /// <summary>The few functions of Steam's flat C API (steam_api_flat.h, SDK 1.65) the game uses, with
    /// blittable arguments only, so NativeAOT needs no marshalling. C++ bools come back as bytes, strings
    /// go as zero-terminated UTF-8. The library is loaded from next to the executable.</summary>
    static unsafe class SteamApi
    {
        const string Lib = "steam_api";

        /// <summary>Valve packs callback structs by 4 on Linux and by 8 on Windows: a uint64 after an int moves.</summary>
        public static readonly int Pack = OperatingSystem.IsWindows() ? 8 : 4;

        /// <summary>sizeof(SteamNetworkingIdentity): type, size and a 128-byte union, packed by 1.</summary>
        public const int IdentitySize = 136;

        static bool resolving;

        public static void Load()
        {
            if (resolving)
                return;
            resolving = true;
            NativeLibrary.SetDllImportResolver(typeof(SteamApi).Assembly, Resolve);
        }

        static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
        {
            if (name != Lib)
                return IntPtr.Zero;
            string file = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "steam_api64.dll" : "libsteam_api.so");
            return NativeLibrary.TryLoad(file, out IntPtr handle) ? handle : IntPtr.Zero;
        }

        /// <summary>A string as Steam takes it.</summary>
        public static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

        public static string Text(byte* s) => s == null ? null : Marshal.PtrToStringUTF8((IntPtr)s);

        // Start, end and callbacks (steam_api.h).
        [DllImport(Lib)] public static extern int SteamAPI_InitFlat(byte* errMsg);
        [DllImport(Lib)] public static extern void SteamAPI_Shutdown();
        [DllImport(Lib)] public static extern void SteamAPI_ManualDispatch_Init();
        [DllImport(Lib)] public static extern int SteamAPI_GetHSteamPipe();
        [DllImport(Lib)] public static extern void SteamAPI_ManualDispatch_RunFrame(int pipe);
        [DllImport(Lib)] public static extern byte SteamAPI_ManualDispatch_GetNextCallback(int pipe, byte* message);
        [DllImport(Lib)] public static extern void SteamAPI_ManualDispatch_FreeLastCallback(int pipe);
        [DllImport(Lib)] public static extern byte SteamAPI_ManualDispatch_GetAPICallResult(int pipe, ulong call, byte* result, int size, int expected, byte* failed);

        // User and friends.
        [DllImport(Lib)] public static extern nint SteamAPI_SteamUser_v023();
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamUser_GetSteamID(nint self);
        [DllImport(Lib)] public static extern nint SteamAPI_SteamFriends_v018();
        [DllImport(Lib)] public static extern byte* SteamAPI_ISteamFriends_GetPersonaName(nint self);
        [DllImport(Lib)] public static extern int SteamAPI_ISteamFriends_GetFriendCount(nint self, int flags);
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamFriends_GetFriendByIndex(nint self, int index, int flags);
        [DllImport(Lib)] public static extern int SteamAPI_ISteamFriends_GetFriendPersonaState(nint self, ulong friend);
        [DllImport(Lib)] public static extern byte* SteamAPI_ISteamFriends_GetFriendPersonaName(nint self, ulong friend);

        // Lobbies.
        [DllImport(Lib)] public static extern nint SteamAPI_SteamMatchmaking_v009();
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamMatchmaking_CreateLobby(nint self, int type, int maxMembers);
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamMatchmaking_JoinLobby(nint self, ulong lobby);
        [DllImport(Lib)] public static extern void SteamAPI_ISteamMatchmaking_LeaveLobby(nint self, ulong lobby);
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamMatchmaking_RequestLobbyList(nint self);
        [DllImport(Lib)] public static extern void SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter(nint self, byte* key, byte* value, int comparison);
        [DllImport(Lib)] public static extern void SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter(nint self, int filter);
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyByIndex(nint self, int index);
        [DllImport(Lib)] public static extern byte* SteamAPI_ISteamMatchmaking_GetLobbyData(nint self, ulong lobby, byte* key);
        [DllImport(Lib)] public static extern byte SteamAPI_ISteamMatchmaking_SetLobbyData(nint self, ulong lobby, byte* key, byte* value);
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyOwner(nint self, ulong lobby);
        [DllImport(Lib)] public static extern int SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(nint self, ulong lobby);
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(nint self, ulong lobby, int index);
        [DllImport(Lib)] public static extern byte SteamAPI_ISteamMatchmaking_SetLobbyJoinable(nint self, ulong lobby, byte joinable);
        [DllImport(Lib)] public static extern byte SteamAPI_ISteamMatchmaking_InviteUserToLobby(nint self, ulong lobby, ulong invitee);
        [DllImport(Lib)] public static extern void SteamAPI_ISteamMatchmaking_AddRequestLobbyListResultCountFilter(nint self, int max);
        [DllImport(Lib)] public static extern byte* SteamAPI_ISteamMatchmaking_GetLobbyMemberData(nint self, ulong lobby, ulong user, byte* key);
        [DllImport(Lib)] public static extern void SteamAPI_ISteamMatchmaking_SetLobbyMemberData(nint self, ulong lobby, byte* key, byte* value);
        [DllImport(Lib)] public static extern byte SteamAPI_ISteamMatchmaking_SetLobbyOwner(nint self, ulong lobby, ulong owner);

        // Country and language (ISteamUtils, ISteamApps).
        [DllImport(Lib)] public static extern nint SteamAPI_SteamUtils_v011();
        [DllImport(Lib)] public static extern byte* SteamAPI_ISteamUtils_GetIPCountry(nint self);
        [DllImport(Lib)] public static extern nint SteamAPI_SteamApps_v009();
        [DllImport(Lib)] public static extern byte* SteamAPI_ISteamApps_GetCurrentGameLanguage(nint self);

        // Peer-to-peer messages.
        [DllImport(Lib)] public static extern nint SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
        [DllImport(Lib)] public static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(nint self, byte* identity, byte* data, uint size, int flags, int channel);
        [DllImport(Lib)] public static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(nint self, int channel, nint* messages, int max);
        [DllImport(Lib)] public static extern byte SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(nint self, byte* identity);
        [DllImport(Lib)] public static extern byte SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(nint self, byte* identity);
        [DllImport(Lib)] public static extern nint SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
        [DllImport(Lib)] public static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(nint self);
        // Ping locations: SteamNetworkPingLocation_t is 512 opaque bytes, as text at most 1024.
        public const int PingLocationSize = 512, PingTextSize = 1024;
        [DllImport(Lib)] public static extern float SteamAPI_ISteamNetworkingUtils_GetLocalPingLocation(nint self, byte* location);
        [DllImport(Lib)] public static extern int SteamAPI_ISteamNetworkingUtils_EstimatePingTimeBetweenTwoLocations(nint self, byte* a, byte* b);
        [DllImport(Lib)] public static extern int SteamAPI_ISteamNetworkingUtils_EstimatePingTimeFromLocalHost(nint self, byte* remote);
        [DllImport(Lib)] public static extern void SteamAPI_ISteamNetworkingUtils_ConvertPingLocationToString(nint self, byte* location, byte* text, int size);
        [DllImport(Lib)] public static extern byte SteamAPI_ISteamNetworkingUtils_ParsePingLocationString(nint self, byte* text, byte* location);
        [DllImport(Lib)] public static extern void SteamAPI_SteamNetworkingIdentity_SetSteamID64(byte* self, ulong steamId);
        [DllImport(Lib)] public static extern ulong SteamAPI_SteamNetworkingIdentity_GetSteamID64(byte* self);
        [DllImport(Lib)] public static extern void SteamAPI_SteamNetworkingMessage_t_Release(nint message);

        // Workshop items (ISteamUGC): installed and downloaded ones.
        [DllImport(Lib)] public static extern nint SteamAPI_SteamUGC_v021();
        [DllImport(Lib)] public static extern uint SteamAPI_ISteamUGC_GetNumSubscribedItems(nint self, byte includeDisabled);
        [DllImport(Lib)] public static extern uint SteamAPI_ISteamUGC_GetSubscribedItems(nint self, ulong* items, uint max, byte includeDisabled);
        [DllImport(Lib)] public static extern uint SteamAPI_ISteamUGC_GetItemState(nint self, ulong item);
        [DllImport(Lib)] public static extern byte SteamAPI_ISteamUGC_GetItemInstallInfo(nint self, ulong item, ulong* size, byte* folder, uint folderSize, uint* stamp);
        [DllImport(Lib)] public static extern byte SteamAPI_ISteamUGC_GetItemDownloadInfo(nint self, ulong item, ulong* done, ulong* total);
        [DllImport(Lib)] public static extern byte SteamAPI_ISteamUGC_DownloadItem(nint self, ulong item, byte highPriority);

        // Leaderboards (ISteamUserStats) and names of strangers.
        [DllImport(Lib)] public static extern nint SteamAPI_SteamUserStats_v013();
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamUserStats_FindOrCreateLeaderboard(nint self, byte* name, int sort, int display);
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamUserStats_DownloadLeaderboardEntries(nint self, ulong board, int request, int start, int end);
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamUserStats_DownloadLeaderboardEntriesForUsers(nint self, ulong board, ulong* users, int count);
        [DllImport(Lib)] public static extern byte SteamAPI_ISteamUserStats_GetDownloadedLeaderboardEntry(nint self, ulong entries, int index, byte* entry, int* details, int maxDetails);
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamUserStats_UploadLeaderboardScore(nint self, ulong board, int method, int score, int* details, int count);
        [DllImport(Lib)] public static extern byte SteamAPI_ISteamFriends_RequestUserInformation(nint self, ulong user, byte nameOnly);
    }
}
