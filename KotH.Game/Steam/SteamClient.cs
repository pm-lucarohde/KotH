using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static KotH.Client.Steam.SteamApi;

namespace KotH.Client.Steam
{
    /// <summary>Steam for the client, as app 480 (Valve's Spacewar, for testing) until KotH has its own:
    /// started once at launch, pumped once per frame on the game thread (manual dispatch). Not Ready
    /// without a running Steam, with KOTH_STEAM=0 or in shot and headless runs (unless KOTH_STEAM=1);
    /// the game then plays as before, direct IP only.</summary>
    public static unsafe class SteamClient
    {
        public const uint AppId = 480;

        // Callback ids (k_iCallback): SteamAPICallCompleted_t, GameLobbyJoinRequested_t.
        const int CallCompleted = 703;
        const int JoinRequestedId = 333;

        /// <summary>Whether Steam is there: lobbies, P2P and the persona name work.</summary>
        public static bool Ready { get; private set; }

        /// <summary>Why it is not Ready, for the menu.</summary>
        public static string Failure { get; private set; } = "Steam is off";

        public static ulong SteamId { get; private set; }

        /// <summary>The Steam profile's name; null without Steam.</summary>
        public static string PersonaName { get; private set; }

        internal static nint Friends, Matchmaking, Messages, NetUtils;

        /// <summary>The country Steam places this connection in (ISO 3166, e.g. "DE"), and the
        /// language the player runs Steam in as an ISO 639 code; for the Host page's defaults.</summary>
        public static string IpCountry { get; private set; } = "";
        public static string Language { get; private set; } = "en";

        /// <summary>A callback by its id, with its struct: valid only during the call.</summary>
        internal static event Action<int, nint> Callback;

        static readonly Dictionary<ulong, Action<nint, bool>> calls = new Dictionary<ulong, Action<nint, bool>>();
        static readonly Queue<(ulong Lobby, ulong Friend)> invites = new Queue<(ulong, ulong)>();
        static readonly byte[] result = new byte[4096];
        static int pipe;

        public static void Start()
        {
            bool test = Environment.GetEnvironmentVariable("KOTH_SHOT") != null || Environment.GetEnvironmentVariable("KOTH_HEADLESS") == "1";
            string wanted = Environment.GetEnvironmentVariable("KOTH_STEAM");
            if (Ready || wanted == "0" || test && wanted != "1")
                return;
            try
            {
                Load();
                SetAppId();
                byte* error = stackalloc byte[1024];
                int init = SteamAPI_InitFlat(error);
                if (init != 0)
                {
                    Failure = init switch { 2 => "Steam is not running", 3 => "Steam is out of date", _ => "No Steam: " + Text(error) };
                    Console.WriteLine($"[steam] {Failure} ({Text(error)})");
                    return;
                }
                SteamAPI_ManualDispatch_Init();
                pipe = SteamAPI_GetHSteamPipe();
                Friends = SteamAPI_SteamFriends_v018();
                Matchmaking = SteamAPI_SteamMatchmaking_v009();
                Messages = SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
                SteamId = SteamAPI_ISteamUser_GetSteamID(SteamAPI_SteamUser_v023());
                PersonaName = Text(SteamAPI_ISteamFriends_GetPersonaName(Friends));
                IpCountry = Text(SteamAPI_ISteamUtils_GetIPCountry(SteamAPI_SteamUtils_v011()))?.ToUpperInvariant() ?? "";
                Language = LobbyFlags.LanguageOfSteam(Text(SteamAPI_ISteamApps_GetCurrentGameLanguage(SteamAPI_SteamApps_v009())));
                // The relay network takes a few seconds to reach; started now, not at the first join.
                NetUtils = SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
                SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(NetUtils);
                Ready = true;
                Console.WriteLine($"[steam] ready as {PersonaName}, app {AppId}");
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                Failure = "Steam library missing";
                Console.WriteLine($"[steam] {Failure}: {e.Message}");
            }
            catch (Exception e)
            {
                // Whatever Steam does, the game starts: direct IP only.
                Ready = false;
                Failure = "No Steam: " + e.Message;
                Console.WriteLine($"[steam] {Failure}");
            }
        }

        // Steam takes the app from SteamAppId when not started by Steam; on Linux the
        // process environment needs libc's setenv, .NET only changes its own copy.
        static void SetAppId()
        {
            string id = AppId.ToString();
            if (OperatingSystem.IsWindows())
                Environment.SetEnvironmentVariable("SteamAppId", id);
            else
                SetEnv("SteamAppId", id, 1);
        }

        [DllImport("libc", EntryPoint = "setenv")]
        static extern int SetEnv(string name, string value, int overwrite);

        /// <summary>Once per frame: Steam's work, then its callbacks and call results.</summary>
        public static void RunFrame()
        {
            if (!Ready)
                return;
            SteamAPI_ManualDispatch_RunFrame(pipe);
            byte* message = stackalloc byte[32];
            while (SteamAPI_ManualDispatch_GetNextCallback(pipe, message) != 0)
            {
                // CallbackMsg_t: user, callback id, then the struct's pointer and size.
                int id = *(int*)(message + 4);
                nint param = *(nint*)(message + 8);
                try
                {
                    if (id == CallCompleted)
                        Complete(param);
                    else if (id == JoinRequestedId)
                        invites.Enqueue((*(ulong*)param, *(ulong*)(param + 8)));   // lobby, the friend's
                    else
                        Callback?.Invoke(id, param);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[steam] callback {id}: {e}");
                }
                finally
                {
                    SteamAPI_ManualDispatch_FreeLastCallback(pipe);
                }
            }
        }

        // SteamAPICallCompleted_t: the call, the result's callback id and size.
        static void Complete(nint param)
        {
            ulong call = *(ulong*)param;
            int expected = *(int*)(param + 8), size = *(int*)(param + 12);
            if (!calls.Remove(call, out Action<nint, bool> done))
                return;
            fixed (byte* buffer = result)
            {
                byte failed = 1;
                bool ok = size <= result.Length
                       && SteamAPI_ManualDispatch_GetAPICallResult(pipe, call, buffer, size, expected, &failed) != 0 && failed == 0;
                done((nint)buffer, ok);
            }
        }

        /// <summary>Waits for an asynchronous call's result: its struct (valid during done) and whether it came.</summary>
        internal static void Await(ulong call, Action<nint, bool> done)
        {
            if (call == 0)
                done(0, false);
            else
                calls[call] = done;
        }

        /// <summary>Where this player sits in Valve's relay network, as text for lobby data; null
        /// until the relays have been measured (a few seconds after the start).</summary>
        public static string PingLocation()
        {
            if (!Ready)
                return null;
            byte* location = stackalloc byte[PingLocationSize];
            if (SteamAPI_ISteamNetworkingUtils_GetLocalPingLocation(NetUtils, location) < 0f)
                return null;
            byte* text = stackalloc byte[PingTextSize];
            SteamAPI_ISteamNetworkingUtils_ConvertPingLocationToString(NetUtils, location, text, PingTextSize);
            return Text(text);
        }

        /// <summary>The estimated round trip in ms from here to a ping location, or from a to b; -1 unknown.</summary>
        public static int EstimatePing(string to, string from = null)
        {
            if (!Ready || string.IsNullOrEmpty(to))
                return -1;
            byte* b = stackalloc byte[PingLocationSize];
            if (!Parse(to, b))
                return -1;
            if (from == null)
                return SteamAPI_ISteamNetworkingUtils_EstimatePingTimeFromLocalHost(NetUtils, b);
            byte* a = stackalloc byte[PingLocationSize];
            return Parse(from, a) ? SteamAPI_ISteamNetworkingUtils_EstimatePingTimeBetweenTwoLocations(NetUtils, a, b) : -1;
        }

        static bool Parse(string text, byte* location)
        {
            fixed (byte* t = Z(text))
                return SteamAPI_ISteamNetworkingUtils_ParsePingLocationString(NetUtils, t, location) != 0;
        }

        /// <summary>Whether an accepted invite waits for the main menu.</summary>
        public static bool HasInvite => invites.Count > 0;

        /// <summary>A lobby a friend invited to (or plays in) and the player accepted in Steam, if one is
        /// waiting; friend is who it came from, 0 when unknown.</summary>
        public static bool TakeInvite(out ulong lobby, out ulong friend)
        {
            bool any = invites.TryDequeue(out (ulong Lobby, ulong Friend) invite);
            (lobby, friend) = invite;
            return any;
        }

        /// <summary>Queues a lobby as if accepted (+connect_lobby on the command line).</summary>
        public static void Invite(ulong lobby) => invites.Enqueue((lobby, 0));

        /// <summary>A Steam user's name as Steam knows it (friends, lobby members), "?" when it does not.</summary>
        public static string NameOf(ulong id)
            => Ready && Text(SteamAPI_ISteamFriends_GetFriendPersonaName(Friends, id)) is { Length: > 0 } name ? name : "?";

        /// <summary>Friends online now, by name.</summary>
        public static List<(ulong Id, string Name)> OnlineFriends()
        {
            var list = new List<(ulong, string)>();
            if (!Ready)
                return list;
            const int Immediate = 4;   // k_EFriendFlagImmediate: real friends
            int count = SteamAPI_ISteamFriends_GetFriendCount(Friends, Immediate);
            for (int i = 0; i < count; i++)
            {
                ulong id = SteamAPI_ISteamFriends_GetFriendByIndex(Friends, i, Immediate);
                if (SteamAPI_ISteamFriends_GetFriendPersonaState(Friends, id) != 0)
                    list.Add((id, Text(SteamAPI_ISteamFriends_GetFriendPersonaName(Friends, id))));
            }
            list.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public static void Shutdown()
        {
            if (!Ready)
                return;
            Ready = false;
            SteamAPI_Shutdown();
        }
    }
}
