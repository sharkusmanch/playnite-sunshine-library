using Newtonsoft.Json;
using SunshineLibrary.Models;
using SunshineLibrary.Services.Clients;
using System.Collections.Generic;

namespace SunshineLibrary.Settings
{
    /// <summary>
    /// Persistent plugin state. Saved via Playnite's <c>SavePluginSettings</c>.
    /// Credentials (host admin password) are NOT stored here — they live in per-host
    /// DPAPI blobs managed by <see cref="Services.CredentialStore"/>.
    /// </summary>
    public class SunshineLibrarySettings
    {
        /// <summary>
        /// Schema version for forward-compat. Plugin refuses to load unknown future versions
        /// rather than silently downgrading. Bump when the POCO shape changes incompatibly.
        /// </summary>
        public int SettingsVersion { get; set; } = 1;

        public List<HostConfig> Hosts { get; set; } = new List<HostConfig>();

        public ClientSettings Client { get; set; } = new ClientSettings();

        public NotificationMode NotificationMode { get; set; } = NotificationMode.Always;

        /// <summary>
        /// When true, games that no longer exist on any reachable host are deleted
        /// from Playnite's library (not just marked uninstalled). Deletion also wipes
        /// per-game overrides, playtime, and cover. Default off — matches PLAN §11's
        /// "preserve history" guidance.
        /// </summary>
        public bool AutoRemoveOrphanedGames { get; set; } = false;

        /// <summary>Global stream overrides, applied before host/client-profile/game layers.</summary>
        public StreamOverrides GlobalOverrides { get; set; } = new StreamOverrides();

        public static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Include,
            DefaultValueHandling = DefaultValueHandling.Include,
            MissingMemberHandling = MissingMemberHandling.Ignore,
        };

        public const int CurrentSchemaVersion = 1;
    }

    /// <summary>
    /// Per PLAN §12a — match ApolloSync's three-value setting so users of both plugins
    /// see one mental model. Security- and launch-critical events fire regardless of mode.
    /// </summary>
    public enum NotificationMode
    {
        Always,
        OnUpdateOnly,
        Never
    }
}
