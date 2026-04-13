namespace SunshineLibrary.Models
{
    /// <summary>
    /// Flavor-agnostic app representation consumed by library code. Built from
    /// SunshineAppDto or ApolloAppDto by the corresponding HostClient subclass.
    /// </summary>
    public class RemoteApp
    {
        /// <summary>Stable id within a host: Apollo uuid, or sha256(name|cmd) for Sunshine.</summary>
        public string StableId { get; set; }

        public string Name { get; set; }

        /// <summary>Sunshine-only: app index in apps.json array. Used for admin endpoints like /appasset/{index}.</summary>
        public int? Index { get; set; }

        public string CoverRelativePath { get; set; }
    }
}
