using System;

namespace KotH.Client.Settings
{
    /// <summary>The settings in force for this process: read once at the start, replaced by the
    /// Settings page's Apply (and saved). Scripts read <see cref="Applied"/> every frame, so a map
    /// change or a new figure needs nothing; <see cref="Version"/> counts the applies.</summary>
    public static class ClientSettings
    {
        static PlayerSettings applied;

        public static PlayerSettings Applied => applied ??= SettingsFile.Load();

        public static int Version { get; private set; }

        /// <summary>After every Apply, with the new settings.</summary>
        public static event Action<PlayerSettings> Changed;

        /// <summary>The Host page's choices: kept and saved, nothing to apply.</summary>
        public static void RememberHost(PlayerSettings from)
        {
            PlayerSettings s = Applied;
            (s.HostName, s.HostPrivate, s.HostCountry, s.HostModded, s.HostDuo) = (from.HostName, from.HostPrivate, from.HostCountry, from.HostModded, from.HostDuo);
            s.HostLanguages = (string[])from.HostLanguages.Clone();
            SettingsFile.Save(s);
        }

        public static void Apply(PlayerSettings settings)
        {
            applied = settings.Clone();
            Version++;
            SettingsFile.Save(applied);
            Changed?.Invoke(applied);
        }
    }
}
