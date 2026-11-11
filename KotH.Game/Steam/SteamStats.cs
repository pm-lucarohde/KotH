using System;
using System.Collections.Generic;
using KotH.Client.Menu;
using static KotH.Client.Steam.SteamApi;

namespace KotH.Client.Steam
{
    /// <summary>The player's XP and wins as two Steam leaderboards ("KotH XP", "KotH Wins"), which are
    /// the truth; progress.toml only shows them while Steam has not answered. Earned values are added
    /// (Add) and uploaded on Flush (keep best: a stale PC cannot lower them). Cheating needs a changed client.</summary>
    public static unsafe class SteamStats
    {
        public const int XpBoard = 0, WinsBoard = 1;
        static readonly string[] Names = { "KotH XP", "KotH Wins" };

        // Per board: handle, own score (-1 unknown), earned and not sent, sent and not answered.
        static readonly ulong[] boards = new ulong[2];
        static readonly int[] own = { -1, -1 };
        static readonly int[] earned = new int[2];
        static readonly int[] sending = new int[2];
        static nint stats;
        static bool started;

        // k_iSteamUserStatsCallbacks + 4, 5, 6 (call results) and PersonaStateChange_t.
        const int FoundId = 1104, DownloadedId = 1105, UploadedId = 1106, PersonaChangeId = 304;
        const int Descending = 2, Numeric = 1, KeepBest = 1, Global = 0, Friends = 2;

        /// <summary>Counts up whenever the own values change, for the menu to redraw.</summary>
        public static int Version { get; private set; }

        /// <summary>Counts up whenever Steam learns a user's name, for the board to redraw.</summary>
        public static int NamesLearned { get; private set; }

        /// <summary>Whether Steam said what the player has (both boards read).</summary>
        public static bool Known => own[XpBoard] >= 0 && own[WinsBoard] >= 0;

        /// <summary>The player's values on Steam plus what was earned since and is not there yet.</summary>
        public static int Xp => Math.Max(own[XpBoard], 0) + earned[XpBoard] + sending[XpBoard];
        public static int Wins => Math.Max(own[WinsBoard], 0) + earned[WinsBoard] + sending[WinsBoard];

        /// <summary>Finds both boards and reads the own entries; again after a failure.</summary>
        public static void Start()
        {
            if (started || !SteamClient.Ready)
                return;
            started = true;
            stats = SteamAPI_SteamUserStats_v013();
            SteamClient.Callback += OnCallback;
            for (int b = 0; b < 2; b++)
                Find(b);
        }

        static void Find(int b)
        {
            fixed (byte* name = Z(Names[b]))
                SteamClient.Await(SteamAPI_ISteamUserStats_FindOrCreateLeaderboard(stats, name, Descending, Numeric), (result, ok) =>
                {
                    // LeaderboardFindResult_t: handle, found.
                    if (!ok || *(byte*)(result + 8) == 0)
                    {
                        Console.WriteLine($"[stats] no leaderboard \"{Names[b]}\"");
                        return;
                    }
                    boards[b] = *(ulong*)result;
                    ReadOwn(b);
                });
        }

        static void ReadOwn(int b)
        {
            ulong self = SteamClient.SteamId;
            SteamClient.Await(SteamAPI_ISteamUserStats_DownloadLeaderboardEntriesForUsers(stats, boards[b], &self, 1), (result, ok) =>
            {
                if (!ok)
                    return;
                List<Entry> entries = Entries(result);
                own[b] = entries.Count > 0 ? entries[0].Score : 0;
                Console.WriteLine($"[stats] {Names[b]}: {own[b]}");
                Changed();
                Flush();
            });
        }

        /// <summary>Earned in a confirmed round; goes to Steam on the next Flush.</summary>
        public static void Add(int xp, int wins)
        {
            if (xp == 0 && wins == 0)
                return;
            earned[XpBoard] += xp;
            earned[WinsBoard] += wins;
            Changed();
        }

        /// <summary>Uploads what was earned, once the own values are known; one upload per board at a time.</summary>
        public static void Flush()
        {
            if (!SteamClient.Ready)
                return;
            for (int b = 0; b < 2; b++)
            {
                if (boards[b] == 0 || own[b] < 0 || earned[b] == 0 || sending[b] != 0)
                    continue;
                int board = b;
                (sending[b], earned[b]) = (earned[b], 0);
                int score = own[b] + sending[b];
                SteamClient.Await(SteamAPI_ISteamUserStats_UploadLeaderboardScore(stats, boards[b], KeepBest, score, null, 0), (result, ok) =>
                {
                    // LeaderboardScoreUploaded_t: success, then the handle at Pack.
                    if (ok && *(byte*)result != 0)
                        own[board] = Math.Max(own[board], score);
                    else
                    {
                        earned[board] += sending[board];
                        Console.WriteLine($"[stats] upload to \"{Names[board]}\" failed");
                    }
                    sending[board] = 0;
                    Changed();
                });
            }
        }

        /// <summary>One row of a board: global rank, who, score.</summary>
        public readonly record struct Entry(int Rank, ulong Id, int Score);

        /// <summary>The wins board's top rows (global or among friends) and the own row; done gets null on failure.</summary>
        public static void Top(bool friends, int count, Action<List<Entry>, Entry?> done)
        {
            if (!SteamClient.Ready || boards[WinsBoard] == 0)
            {
                done(null, null);
                return;
            }
            ulong call = friends ? SteamAPI_ISteamUserStats_DownloadLeaderboardEntries(stats, boards[WinsBoard], Friends, 0, 0)
                                 : SteamAPI_ISteamUserStats_DownloadLeaderboardEntries(stats, boards[WinsBoard], Global, 1, count);
            SteamClient.Await(call, (result, ok) =>
            {
                if (!ok)
                {
                    done(null, null);
                    return;
                }
                List<Entry> rows = Entries(result);
                if (rows.Count > count)
                    rows.RemoveRange(count, rows.Count - count);
                foreach (Entry row in rows)
                    SteamAPI_ISteamFriends_RequestUserInformation(SteamClient.Friends, row.Id, 1);
                ulong id = SteamClient.SteamId;
                SteamClient.Await(SteamAPI_ISteamUserStats_DownloadLeaderboardEntriesForUsers(stats, boards[WinsBoard], &id, 1), (mine, found) =>
                {
                    List<Entry> self = found ? Entries(mine) : null;
                    done(rows, self is { Count: > 0 } ? self[0] : null);
                });
            });
        }

        // LeaderboardScoresDownloaded_t: board, entries handle, count; LeaderboardEntry_t: user, rank, score,
        // details, then the UGC handle (28 bytes packed by 4, 32 by 8).
        static List<Entry> Entries(nint result)
        {
            ulong handle = *(ulong*)(result + 8);
            int count = *(int*)(result + 16);
            var list = new List<Entry>(count);
            byte* entry = stackalloc byte[32];
            for (int i = 0; i < count; i++)
                if (SteamAPI_ISteamUserStats_GetDownloadedLeaderboardEntry(stats, handle, i, entry, null, 0) != 0)
                    list.Add(new Entry(*(int*)(entry + 8), *(ulong*)entry, *(int*)(entry + 12)));
            return list;
        }

        static void OnCallback(int id, nint param)
        {
            if (id == PersonaChangeId)
                NamesLearned++;
        }

        static void Changed()
        {
            Version++;
            if (Known)
                ProgressFile.Save(Xp, Wins);
        }
    }
}
