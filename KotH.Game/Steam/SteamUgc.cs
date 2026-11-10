using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KotH.MapFormat;
using static KotH.Client.Steam.SteamApi;

namespace KotH.Client.Steam
{
    /// <summary>Custom maps on the Steam Workshop (ISteamUGC): the installed items the map library
    /// scans, downloads for a guest whose host plays a map it lacks, and uploads from the Maps page.
    /// Game thread only, like the rest of SteamClient.</summary>
    public static unsafe class SteamUgc
    {
        // Callback ids: k_iSteamUGCCallbacks (3400) + 3 CreateItemResult_t, + 4 SubmitItemUpdateResult_t,
        // + 6 DownloadItemResult_t.
        const int DownloadResultId = 3406;
        const int ItemInstalled = 4, ItemNeedsUpdate = 8;
        const int ResultOk = 1, ResultLimitExceeded = 25;

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

        /// <summary>One upload of a package folder: creates the item on the first upload (its id goes
        /// into map.toml and the SteamCMD file), then sends title, description, folder and preview.</summary>
        public sealed class Upload
        {
            public string Status = "Starting";
            public float Progress;
            public bool Done, Failed;
            public ulong Item;
            /// <summary>Steam wants the workshop agreement accepted before the item can be seen.</summary>
            public bool NeedsAgreement;
            /// <summary>Steam had no room for the preview; the item went up without it.</summary>
            public bool NoPreview;
            internal ulong Handle;

            /// <summary>Updates Progress and Status from Steam while the files go up.</summary>
            public void Poll()
            {
                if (Done || Failed || Handle == 0 || !SteamClient.Ready)
                    return;
                ulong sent, total;
                int stage = SteamAPI_ISteamUGC_GetItemUpdateProgress(ugc, Handle, &sent, &total);
                Status = stage switch
                {
                    1 => "Preparing", 2 => "Reading the files", 3 => "Uploading", 4 => "Uploading the preview", 5 => "Committing",
                    _ => Status,
                };
                if (total > 0)
                    Progress = (float)sent / total;
            }
        }

        public static Upload Publish(string folder, string changeNote)
        {
            var up = new Upload();
            if (!Ready)
            {
                up.Failed = true;
                up.Status = SteamClient.Failure;
                return up;
            }
            MapPackage p;
            try
            {
                p = MapPackage.Open(folder);
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is UnauthorizedAccessException)
            {
                up.Failed = true;
                up.Status = e.Message;
                return up;
            }
            up.Item = p.Manifest.WorkshopId != 0 ? p.Manifest.WorkshopId : p.WorkshopItem;
            if (up.Item != 0)
            {
                Submit(up, p, changeNote);
                return up;
            }
            up.Status = "Creating the workshop item";
            SteamClient.Await(SteamAPI_ISteamUGC_CreateItem(ugc, SteamClient.AppId, 0), (result, ok) =>
            {
                // CreateItemResult_t: EResult, item (after a pad on Windows), agreement flag.
                int at = Pack == 8 ? 8 : 4;
                if (!ok || *(int*)result != ResultOk)
                {
                    up.Failed = true;
                    up.Status = ok ? $"Steam refused the item (result {*(int*)result})" : "No answer from Steam";
                    return;
                }
                up.Item = *(ulong*)(result + at);
                up.NeedsAgreement = *(byte*)(result + at + 8) != 0;
                try
                {
                    p = Remember(p, up.Item, changeNote);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    up.Failed = true;
                    up.Status = $"Item {up.Item} made, but map.toml not written: {e.Message}";
                    return;
                }
                Submit(up, p, changeNote);
            });
            return up;
        }

        // The new id into map.toml (it changes the package's hash) and the SteamCMD file.
        static MapPackage Remember(MapPackage p, ulong item, string changeNote)
        {
            p.Manifest.WorkshopId = item;
            File.WriteAllText(p.ManifestPath, p.Manifest.ToString(), new UTF8Encoding(false));
            MapPackage again = MapPackage.Open(p.Folder);
            string vdf = p.Folder.TrimEnd(Path.DirectorySeparatorChar) + ".workshop.vdf";
            File.WriteAllText(vdf, MapBuilder.Vdf(again, changeNote, item), new UTF8Encoding(false));
            return again;
        }

        // A preview counts against the app's Steam Cloud space, which on app 480 is full: then
        // Steam answers LimitExceeded and the item goes up once more without it.
        static void Submit(Upload up, MapPackage p, string changeNote, bool preview = true)
        {
            MapManifest m = p.Manifest;
            ulong handle = SteamAPI_ISteamUGC_StartItemUpdate(ugc, SteamClient.AppId, up.Item);
            up.Handle = handle;
            fixed (byte* title = Z(m.Title))
                SteamAPI_ISteamUGC_SetItemTitle(ugc, handle, title);
            fixed (byte* text = Z(m.Description))
                SteamAPI_ISteamUGC_SetItemDescription(ugc, handle, text);
            fixed (byte* content = Z(Path.GetFullPath(p.Folder)))
                SteamAPI_ISteamUGC_SetItemContent(ugc, handle, content);
            preview &= File.Exists(p.PreviewPath);
            if (preview)
                fixed (byte* image = Z(Path.GetFullPath(p.PreviewPath)))
                    SteamAPI_ISteamUGC_SetItemPreview(ugc, handle, image);
            fixed (byte* tag = Z("Map"))
            {
                byte* one = tag;
                var tags = new StringArray { Strings = &one, Count = 1 };
                SteamAPI_ISteamUGC_SetItemTags(ugc, handle, &tags, 0);
            }
            int visibility = m.Visibility == "public" ? 0 : m.Visibility == "friends" ? 1 : m.Visibility == "unlisted" ? 3 : 2;
            SteamAPI_ISteamUGC_SetItemVisibility(ugc, handle, visibility);
            up.Status = "Uploading";
            fixed (byte* note = Z(changeNote ?? ""))
                SteamClient.Await(SteamAPI_ISteamUGC_SubmitItemUpdate(ugc, handle, note), (result, ok) =>
                {
                    // SubmitItemUpdateResult_t: EResult, agreement flag, item (at 8 either way).
                    up.Handle = 0;
                    if (ok && *(int*)result == ResultLimitExceeded && preview)
                    {
                        Submit(up, p, changeNote, preview: false);
                        up.Status = "Uploading without the preview";
                        up.NoPreview = true;
                        return;
                    }
                    if (!ok || *(int*)result != ResultOk)
                    {
                        up.Failed = true;
                        up.Status = ok ? $"Upload refused (result {*(int*)result})" : "No answer from Steam";
                        return;
                    }
                    up.NeedsAgreement |= *(byte*)(result + 4) != 0;
                    up.Progress = 1f;
                    up.Done = true;
                    up.Status = up.NeedsAgreement ? "Uploaded; accept the workshop agreement on Steam to show it"
                        : up.NoPreview ? "Uploaded without the preview (Steam has no room for it)" : "Uploaded";
                });
        }

        /// <summary>The item's workshop page in the Steam overlay or client.</summary>
        public static string PageUrl(ulong item) => $"steam://url/CommunityFilePage/{item}";
    }
}
