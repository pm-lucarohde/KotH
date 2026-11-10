using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using KotH.Client.Steam;
using KotH.MapFormat;
using KotH.Shared.Hosting;

namespace KotH.Client.Menu
{
    /// <summary>The Maps page: the custom maps the library finds, Test (alone with bots) for each and
    /// Upload or Update to the workshop for the player's own (its folder); MainMenu ticks it while open,
    /// so an upload's progress shows.</summary>
    public partial class MapsPage : UserControl
    {
        readonly RowList list = new RowList();
        SteamUgc.Upload upload;
        string uploading, said;

        /// <summary>Test was chosen: the map's id.</summary>
        public event Action<string> TestRequested;

        public MapLibrary Library { get; set; }

        public MapsPage()
        {
            InitializeComponent();
            Inside.Children.Add(list);
            OpenFolder.Click += (_, _) => Reveal(MapPackage.UserFolder());
        }

        /// <summary>Opened (or an upload done): the maps anew.</summary>
        public void Open()
        {
            var rows = new List<RowList.Row>();
            string own = Path.GetFullPath(MapPackage.UserFolder());
            List<MapPackage> all = Library?.All() ?? new List<MapPackage>();
            foreach (MapPackage p in all)
            {
                MapPackage map = p;
                bool mine = Path.GetFullPath(p.Folder).StartsWith(own, StringComparison.Ordinal);
                ulong item = p.Manifest.WorkshopId != 0 ? p.Manifest.WorkshopId : p.WorkshopItem;
                string detail = mine ? item != 0 ? "yours, on the workshop" : "yours" : "workshop";
                Action send = mine && SteamClient.Ready && upload == null ? () => Send(map) : null;
                rows.Add(new RowList.Row(p.Manifest.Title, detail, "Test", () => TestRequested?.Invoke(map.Id),
                                         mine ? item != 0 ? "Update" : "Upload" : null, send));
            }
            list.Show(rows, keepPlace: true);
            Empty.IsVisible = rows.Count == 0;
            if (Library != null && Library.Problems.Count > 0)
                said = $"Not readable: {Library.Problems[0]}";
            Tick();
        }

        void Send(MapPackage map)
        {
            uploading = map.Manifest.Title;
            upload = SteamUgc.Publish(map.Folder, "");
            Open();
        }

        /// <summary>Each frame while open: the upload's progress, the list again when it ends.</summary>
        public void Tick()
        {
            if (upload != null)
            {
                upload.Poll();
                Status.Text = $"{uploading}: {upload.Status}" + (upload.Done || upload.Failed ? "" : $"  {upload.Progress * 100f:F0} %");
                if (upload.Done || upload.Failed)
                {
                    said = Status.Text;
                    if (upload.Done && upload.NeedsAgreement)
                        Reveal(SteamUgc.PageUrl(upload.Item));
                    upload = null;
                    Open();
                }
                return;
            }
            Status.Text = said ?? (SteamClient.Ready ? "Your maps are in the folder the map kit exports to."
                                                      : "Uploads need Steam. Your maps are in the folder the map kit exports to.");
        }

        // A folder in the file manager or a steam:// page, by the desktop's opener.
        static void Reveal(string target)
        {
            try
            {
                if (!target.Contains("://"))
                    Directory.CreateDirectory(target);
                var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open") { UseShellExecute = false };
                start.ArgumentList.Add(target);
                Process.Start(start)?.Dispose();
            }
            catch (Exception e) when (e is IOException || e is System.ComponentModel.Win32Exception || e is UnauthorizedAccessException)
            {
                Console.WriteLine($"[maps] cannot open {target}: {e.Message}");
            }
        }
    }
}
