using System;
using System.Collections.Generic;
using System.IO;
using KotH.MapFormat;

namespace KotH.Shared.Hosting
{
    /// <summary>The custom map packages this machine has: the player's map folder, folders given by
    /// --map-file and, in the game, Steam's installed workshop items. A package is found by the hash
    /// in its id, wherever it lies; hashes are kept until a hashed file changes.</summary>
    public sealed class MapLibrary
    {
        readonly List<string> roots = new List<string>();
        readonly List<string> folders = new List<string>();
        readonly Dictionary<string, (string stamp, MapPackage package)> cache = new Dictionary<string, (string, MapPackage)>();
        readonly object gate = new object();

        /// <summary>More package folders on each scan with their item (the game: Steam's installed ones).</summary>
        public Func<IEnumerable<(string Folder, ulong Item)>> Installed;

        /// <summary>Why packages were left out on the last scan, folder: reason.</summary>
        public readonly List<string> Problems = new List<string>();

        public void AddRoot(string root)
        {
            lock (gate)
                if (!roots.Contains(root))
                    roots.Add(root);
        }

        /// <summary>Takes one package folder in (--map-file) and returns it, opened; throws if broken.</summary>
        public MapPackage AddFolder(string folder)
        {
            MapPackage p = MapPackage.Open(folder);
            lock (gate)
                if (!folders.Contains(p.Folder))
                    folders.Add(p.Folder);
            return p;
        }

        /// <summary>The package with this id's hash, null if there is none here.</summary>
        public MapPackage Find(string id)
        {
            string hash = MapPackage.HashOfId(id);
            if (hash == null)
                return null;
            foreach (MapPackage p in All())
                if (p.Hash == hash)
                    return p;
            return null;
        }

        /// <summary>Every readable package, the player's own first, then the rest by folder.</summary>
        public List<MapPackage> All()
        {
            lock (gate)
            {
                var list = new List<MapPackage>();
                Problems.Clear();
                var seen = new HashSet<string>();
                foreach (string root in roots)
                    foreach (string folder in MapPackage.FoldersIn(root))
                        Take(folder, 0, list, seen);
                foreach (string folder in folders)
                    Take(folder, 0, list, seen);
                if (Installed != null)
                    foreach ((string folder, ulong item) in Installed())
                        Take(folder, item, list, seen);
                return list;
            }
        }

        void Take(string folder, ulong item, List<MapPackage> list, HashSet<string> seen)
        {
            folder = Path.GetFullPath(folder);
            if (!seen.Add(folder))
                return;
            try
            {
                string stamp = Stamp(folder) + item;
                if (!cache.TryGetValue(folder, out var known) || known.stamp != stamp)
                    cache[folder] = known = (stamp, MapPackage.Open(folder, item));
                list.Add(known.package);
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is UnauthorizedAccessException)
            {
                cache.Remove(folder);
                Problems.Add($"{folder}: {e.Message}");
            }
        }

        /// <summary>Write time and size of each hashed file (and the vdf's time): a change opens it again.</summary>
        static string Stamp(string folder)
        {
            var sb = new System.Text.StringBuilder();
            foreach (string name in MapPackage.Hashed)
            {
                var file = new FileInfo(Path.Combine(folder, name));
                if (file.Exists)
                    sb.Append(file.LastWriteTimeUtc.Ticks).Append(':').Append(file.Length);
                sb.Append('|');
            }
            var vdf = new FileInfo(folder.TrimEnd(Path.DirectorySeparatorChar) + ".workshop.vdf");
            if (vdf.Exists)
                sb.Append(vdf.LastWriteTimeUtc.Ticks);
            return sb.ToString();
        }
    }
}
