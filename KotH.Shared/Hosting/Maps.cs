namespace KotH.Shared.Hosting
{
    /// <summary>The maps by name: folder Maps/NAME with NAMECollision in KotH.Shared (colliders,
    /// markers, zone, bot settings; loads headless) and NAMEVisual in KotH.Game, both root assets.
    /// A generated map embeds its ground as Resources/Maps/NAMETerrain.bin in KotH.Shared.</summary>
    public static class Maps
    {
        /// <summary>The map without --map.</summary>
        public const string Default = "MountDestiny";

        /// <summary>The test ground the probes measure on, as it came from Unity.</summary>
        public const string Test = "Test";

        /// <summary>Shotbow's Smash map from Minecraft (Tools/maps/Atrium.cs).</summary>
        public const string Atrium = "Atrium";

        /// <summary>The tower of "The Ultimate King of the Ladder" from Minecraft (Tools/maps/Ladder.cs).</summary>
        public const string TheLadder = "TheLadder";

        /// <summary>Mount Destiny's rounder first version of 22.10.2026 (Tools/maps/Mountain.cs --map Volcano).</summary>
        public const string Volcano = "Volcano";

        /// <summary>Every map a host may choose, in the menu's order; a new map is added here.</summary>
        public static readonly string[] All = { Default, Volcano, Atrium, TheLadder, Test };

        /// <summary>A built-in map's name for people (the id where it reads well).</summary>
        public static string Title(string map) => map switch
        {
            Default => "Mount Destiny",
            TheLadder => "The Ladder",
            Test => "Test Map",
            _ => map,
        };

        public static string CollisionUrl(string map) => $"/KotH.Shared/Maps/{map}/{map}Collision";

        /// <summary>Generated ground (Tools/maps), loaded after the collision prefab if the map has one.</summary>
        public static string TerrainResource(string map) => $"Maps/{map}Terrain.bin";

        public static string VisualUrl(string map) => $"Maps/{map}/{map}Visual";
    }
}
