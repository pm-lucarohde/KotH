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

        public static string CollisionUrl(string map) => $"/KotH.Shared/Maps/{map}/{map}Collision";

        /// <summary>Generated ground (Tools/maps), loaded after the collision prefab if the map has one.</summary>
        public static string TerrainResource(string map) => $"Maps/{map}Terrain.bin";

        public static string VisualUrl(string map) => $"Maps/{map}/{map}Visual";
    }
}
