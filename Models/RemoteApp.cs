namespace SunshineLibrary.Models
{
    /// <summary>
    /// Flavor-agnostic app representation consumed by library code. Built from
    /// SunshineAppDto or ApolloAppDto by the corresponding HostClient subclass.
    /// </summary>
    public class RemoteApp
    {
        /// <summary>Stable id within a host: Apollo/Vibepollo uuid, or sha256(name|cmd) for Sunshine.</summary>
        public string StableId { get; set; }

        public string Name { get; set; }

        /// <summary>Sunshine/Apollo: app index in apps.json array. Used for admin endpoints like /appasset/{index}.</summary>
        public int? Index { get; set; }

        public string CoverRelativePath { get; set; }

        /// <summary>Vibepollo: library source display name from Playnite (e.g. "Steam", "GOG"). Null for Sunshine/Apollo.</summary>
        public string PluginName { get; set; }

        /// <summary>Vibepollo: Playnite categories assigned to this game on the host. Null for Sunshine/Apollo.</summary>
        public System.Collections.Generic.List<string> Categories { get; set; }

        /// <summary>
        /// Vibepollo: host-side playtime in minutes from Playnite. Zero until the API exposes this field.
        /// </summary>
        public ulong PlaytimeMinutes { get; set; }
    }
}
