using System.Collections.Generic;

namespace SunshineLibrary.Models
{
    /// <summary>
    /// User-configured shape of the Playnite entries this plugin creates: which
    /// platform they land under and which tags they carry.
    ///
    /// Passed into the sync rather than read from settings inside it, so the sync
    /// stays a pure function of its inputs and the metadata shape is testable
    /// without a settings instance.
    ///
    /// Playnite applies platform and tag metadata at first import only — its
    /// library-update reconciliation touches install state, playtime and install
    /// size and nothing else. Changing these settings therefore affects newly
    /// imported games; existing entries are updated by the explicit
    /// "apply to existing games" menu action.
    /// </summary>
    public class LibraryMetadataOptions
    {
        /// <summary>
        /// Playnite platform name. Null or empty selects <see cref="DefaultPlatformSpecId"/>.
        /// </summary>
        public string PlatformName { get; set; }

        /// <summary>Extra tags to apply. Null or empty entries are ignored.</summary>
        public IReadOnlyList<string> Tags { get; set; }

        /// <summary>
        /// Playnite specification id used when no platform is configured — the
        /// value this plugin hardcoded before the setting existed.
        /// </summary>
        public const string DefaultPlatformSpecId = "pc_windows";

        /// <summary>
        /// Whether the user has configured anything at all. Deliberately a question about
        /// the settings, not about what those settings resolve to in the database — the
        /// built-in platform row exists in every library, so resolving first would make
        /// "nothing configured" unreachable.
        /// </summary>
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(PlatformName) || System.Linq.Enumerable.Any(CleanTags());

        /// <summary>Trimmed, de-duplicated, blank-free view of <see cref="Tags"/>.</summary>
        public IEnumerable<string> CleanTags()
        {
            if (Tags == null) yield break;
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var t in Tags)
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                var trimmed = t.Trim();
                if (seen.Add(trimmed)) yield return trimmed;
            }
        }
    }
}
