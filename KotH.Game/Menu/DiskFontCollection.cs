using System;
using System.IO;
using Avalonia.Media.Fonts;

namespace KotH.Client.Menu
{
    /// <summary>Loads the pan-CJK variable font (Fonts/NotoSansCJK.ttc) beside the binary into Avalonia's
    /// font manager once at startup, avoiding embedding 32 MB inside the managed assembly.</summary>
    sealed class DiskFontCollection : FontCollectionBase
    {
        public override Uri Key { get; }

        public DiskFontCollection(Uri key, string relativePath)
        {
            Key = key;
            string path = Path.Combine(AppContext.BaseDirectory, relativePath);
            if (!File.Exists(path))
                return;
            using FileStream stream = File.OpenRead(path);
            TryAddGlyphTypeface(stream, out _);
        }
    }
}
