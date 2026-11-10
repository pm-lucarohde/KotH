using System.Collections.Generic;
using System.IO;
using KotH.MapFormat;
using static KotH.Client.Steam.SteamApi;

namespace KotH.Client.Steam
{
    /// <summary>Custom maps on the Steam Workshop (ISteamUGC): the installed items the map library
    /// scans and downloads for a guest whose host plays a map it lacks (makers upload with SteamCMD).
    /// Game thread only, like the rest of SteamClient.</summary>
    public static unsafe class SteamUgc
    {
        // Callback id: k_iSteamUGCCallbacks (3400) + 6 DownloadItemResult_t.
        const int DownloadResultId = 3406;
        const int ItemInstalled = 4, ItemNeedsUpdate = 8;
        const int ResultOk = 1;

        static nint ugc;
        static readonly Dictionary<ulong, Download> downloads = new Dictionary<ulong, Download>();

        sealed class Download
        {
            public double Started;
            public int Result;   // 0 while running, else EResult
        }

        public enum Fetched { Installed, Downloading, Failed }

        static bool Ready
        {
            get
            {
                if (!SteamClient.Ready)
                    return false;
                if (ugc == 0)
                {
                    ugc = SteamAPI_SteamUGC_v021();
                    SteamClient.Callback += OnCallback;
                }
                return ugc != 0;
            }
        }

        static void OnCallback(int id, nint param)
        {
            // DownloadItemResult_t: app id, item (after a pad on Windows), EResult.
            if (id != DownloadResultId)
                return;
            int at = Pack == 8 ? 8 : 4;
            ulong item = *(ulong*)(param + at);
            if (downloads.TryGetValue(item, out Download d))
                d.Result = *(int*)(param + at + 8);
        }

        /// <summary>The map library's extra folders: subscribed items and those fetched this session,
        /// where Steam has them installed.</summary>
        public static IEnumerable<(string Folder, ulong Item)> Installed()
        {
            var list = new List<(string, ulong)>();
            if (!Ready)
                return list;
            var items = new HashSet<ulong>(downloads.Keys);
            uint count = SteamAPI_ISteamUGC_GetNumSubscribedItems(ugc, 0);
            if (count > 0)
            {
                var ids = new ulong[count];
                fixed (ulong* p = ids)
                    count = SteamAPI_ISteamUGC_GetSubscribedItems(ugc, p, count, 0);
                for (int i = 0; i < count; i++)
                    items.Add(ids[i]);
            }
            foreach (ulong item in items)
                if (FolderOf(item) is { } folder && File.Exists(Path.Combine(folder, MapPackage.ManifestFile)))
                    list.Add((folder, item));
            return list;
        }

        static string FolderOf(ulong item)
        {
            if ((SteamAPI_ISteamUGC_GetItemState(ugc, item) & ItemInstalled) == 0)
                return null;
            byte* folder = stackalloc byte[1024];
            ulong size;
            uint stamp;
            return SteamAPI_ISteamUGC_GetItemInstallInfo(ugc, item, &size, folder, 1024, &stamp) != 0 ? Text(folder) : null;
        }

        /// <summary>Gets an item onto this machine (subscribed or not): Downloading with its share done
        /// until Steam reports the result; Failed without Steam, on an error or after two minutes.</summary>
        public static Fetched Fetch(ulong item, double now, out float progress)
        {
            progress = 0f;
            if (!Ready || item == 0)
                return Fetched.Failed;
            if (!downloads.TryGetValue(item, out Download d))
            {
                // High priority; Steam also checks for a newer version of an installed item.
                if (SteamAPI_ISteamUGC_DownloadItem(ugc, item, 1) == 0)
                    return Fetched.Failed;
                downloads[item] = d = new Download { Started = now };
            }
            if (d.Result == ResultOk)
                return (SteamAPI_ISteamUGC_GetItemState(ugc, item) & (ItemInstalled | ItemNeedsUpdate)) == ItemInstalled
                    ? Fetched.Installed : Fetched.Downloading;
            if (d.Result != 0 || now - d.Started > 120.0)
                return Fetched.Failed;
            ulong done, total;
            if (SteamAPI_ISteamUGC_GetItemDownloadInfo(ugc, item, &done, &total) != 0 && total > 0)
                progress = (float)done / total;
            return Fetched.Downloading;
        }
    }
}
