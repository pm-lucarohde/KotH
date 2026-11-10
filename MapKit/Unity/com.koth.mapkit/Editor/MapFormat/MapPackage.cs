using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KotH.MapFormat
{
    /// <summary>A map package: a folder with map.toml, map.glb, collision.bin and preview.png. Its
    /// id names it for every peer: "ws:ITEM@HASH" once uploaded, else "local:NAME@HASH"; HASH is
    /// the first 8 bytes of a SHA-256 over toml, collision and glb, so the same id means the same
    /// map to the byte.</summary>
    public sealed class MapPackage
    {
        public const string ManifestFile = "map.toml";
        public const string ModelFile = "map.glb";
        public const string CollisionFile = "collision.bin";
        public const string PreviewFile = "preview.png";

        /// <summary>The files the hash covers, in hash order.</summary>
        public static readonly string[] Hashed = { ManifestFile, CollisionFile, ModelFile };

        public string Folder { get; private set; }
        public MapManifest Manifest { get; private set; }
        public string Hash { get; private set; }
        public string Id { get; private set; }

        /// <summary>The Steam Workshop item it is (or was uploaded as), 0 for none.</summary>
        public ulong WorkshopItem { get; private set; }

        public string ModelPath => Path.Combine(Folder, ModelFile);
        public string CollisionPath => Path.Combine(Folder, CollisionFile);
        public string PreviewPath => Path.Combine(Folder, PreviewFile);

        /// <summary>Reads map.toml and hashes the package; throws with a reason a map maker can act on.
        /// The item comes from Steam for an installed one, else from map.toml or the vdf beside it.</summary>
        public static MapPackage Open(string folder, ulong workshopItem = 0)
        {
            folder = Path.GetFullPath(folder);
            string manifest = Path.Combine(folder, ManifestFile);
            if (!File.Exists(manifest))
                throw new FileNotFoundException($"{folder}: no {ManifestFile}");
            if (!File.Exists(Path.Combine(folder, CollisionFile)))
                throw new FileNotFoundException($"{folder}: no {CollisionFile}");
            MapManifest m;
            try
            {
                m = MapManifest.Parse(File.ReadAllText(manifest, Encoding.UTF8));
            }
            catch (FormatException e)
            {
                throw new FormatException($"{manifest}: {e.Message}");
            }
            string hash = HashOf(folder);
            ulong item = workshopItem != 0 ? workshopItem : m.WorkshopId != 0 ? m.WorkshopId
                       : MapBuilder.ReadPublished(folder.TrimEnd(Path.DirectorySeparatorChar) + ".workshop.vdf");
            return new MapPackage
            {
                Folder = folder,
                Manifest = m,
                Hash = hash,
                WorkshopItem = item,
                Id = (item != 0 ? "ws:" + item : "local:" + m.Name) + "@" + hash,
            };
        }

        public static string HashOf(string folder)
        {
            using (SHA256 sha = SHA256.Create())
            {
                foreach (string name in Hashed)
                {
                    string path = Path.Combine(folder, name);
                    byte[] bytes = File.Exists(path) ? File.ReadAllBytes(path) : new byte[0];
                    byte[] head = Encoding.UTF8.GetBytes(name + "\n" + bytes.Length + "\n");
                    sha.TransformBlock(head, 0, head.Length, null, 0);
                    sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++)
                    sb.Append(sha.Hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Whether a map name is a package id rather than a built-in map.</summary>
        public static bool IsId(string map) => map != null && map.IndexOf(':') > 0 && map.IndexOf('@') > 0;

        /// <summary>The HASH of an id, null for a built-in name.</summary>
        public static string HashOfId(string id) => IsId(id) ? id.Substring(id.LastIndexOf('@') + 1) : null;

        /// <summary>The workshop item of a "ws:" id, 0 otherwise.</summary>
        public static ulong WorkshopOf(string id)
        {
            if (id == null || !id.StartsWith("ws:"))
                return 0;
            int at = id.IndexOf('@');
            ulong.TryParse(id.Substring(3, (at < 0 ? id.Length : at) - 3), out ulong item);
            return item;
        }

        /// <summary>Where the player's own maps live: ~/.local/share/KotH/maps, %LOCALAPPDATA%\KotH\maps.</summary>
        public static string UserFolder()
        {
            string data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(data, "KotH", "maps");
        }

        /// <summary>Every package folder right below a folder (sorted), broken ones included.</summary>
        public static List<string> FoldersIn(string root)
        {
            var list = new List<string>();
            if (!Directory.Exists(root))
                return list;
            foreach (string d in Directory.GetDirectories(root))
                if (File.Exists(Path.Combine(d, ManifestFile)))
                    list.Add(d);
            list.Sort(StringComparer.Ordinal);
            return list;
        }
    }
}
