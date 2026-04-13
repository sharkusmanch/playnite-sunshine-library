using Playnite.SDK;
using Playnite.SDK.Data;
using SunshineLibrary.Models;
using SunshineLibrary.Services;
using SunshineLibrary.Services.Clients;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace SunshineLibrary.Settings
{
    /// <summary>
    /// ISettings wrapper over <see cref="SunshineLibrarySettings"/>. Follows the
    /// Playnite editing-clone pattern (see ApolloSyncSettings.cs:101-216) — Begin/
    /// CancelEdit revert binding-bound mutations; EndEdit persists and writes any
    /// credentials through to the DPAPI store.
    /// </summary>
    public class SunshineLibrarySettingsViewModel : ObservableObject, ISettings
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly SunshineLibrary plugin;
        private readonly CredentialStore credentialStore;
        private SunshineLibrarySettings editingClone;

        /// <summary>Exposes the host plugin's IPlayniteAPI so the view can create themed dialogs.</summary>
        public IPlayniteAPI Api => plugin.PlayniteApi;

        private SunshineLibrarySettings settings;
        public SunshineLibrarySettings Settings
        {
            get => settings;
            set { settings = value; OnPropertyChanged(); OnPropertyChanged(nameof(Hosts)); }
        }

        /// <summary>Binding-friendly observable view of Settings.Hosts.</summary>
        public ObservableCollection<HostConfig> Hosts { get; } = new ObservableCollection<HostConfig>();

        public SunshineLibrarySettingsViewModel(SunshineLibrary plugin, CredentialStore credentialStore)
        {
            this.plugin = plugin;
            this.credentialStore = credentialStore;

            var saved = plugin.LoadPluginSettings<SunshineLibrarySettings>();
            if (saved == null)
            {
                Settings = new SunshineLibrarySettings();
            }
            else if (saved.SettingsVersion > SunshineLibrarySettings.CurrentSchemaVersion)
            {
                logger.Warn($"Settings version {saved.SettingsVersion} is newer than supported ({SunshineLibrarySettings.CurrentSchemaVersion}). Using defaults.");
                Settings = new SunshineLibrarySettings();
            }
            else
            {
                Settings = saved;
            }

            HydrateCredentials(Settings);
            RebuildHostsCollection();
        }

        /// <summary>Called once at plugin load so the plugin can use credentials even before the user opens settings.</summary>
        /// <summary>Wipe all DPAPI blobs and clear in-memory passwords. Used by the Danger Zone button.</summary>
        public void RevokeAllCredentials()
        {
            credentialStore.DeleteAll();
            if (Settings?.Hosts != null)
            {
                foreach (var h in Settings.Hosts)
                {
                    if (h == null) continue;
                    h.AdminPassword = null;
                }
            }
        }

        public void HydrateCredentials(SunshineLibrarySettings target)
        {
            if (target?.Hosts == null) return;
            foreach (var h in target.Hosts)
            {
                if (h == null) continue;
                var creds = credentialStore.TryLoad(h.Id);
                if (creds.HasValue)
                {
                    h.AdminUser = creds.Value.User;
                    h.AdminPassword = creds.Value.Password;
                }
            }
        }

        private void RebuildHostsCollection()
        {
            Hosts.Clear();
            foreach (var h in Settings.Hosts ?? new List<HostConfig>())
            {
                Hosts.Add(h);
            }
        }

        // --- ISettings ---------------------------------------------------------

        public void BeginEdit()
        {
            // Full clone so any mutation in the bound UI can be rolled back on Cancel.
            editingClone = Serialization.GetClone(Settings);
            HydrateCredentials(editingClone); // clone drops passwords (JsonIgnore) — rehydrate
        }

        public void CancelEdit()
        {
            Settings = editingClone;
            RebuildHostsCollection();
        }

        public void EndEdit()
        {
            // Commit: persist credentials to DPAPI, then save settings (passwords
            // are JsonIgnore'd so they never reach the settings JSON).
            foreach (var h in Settings.Hosts ?? new List<HostConfig>())
            {
                if (h == null || h.Id == System.Guid.Empty) continue;
                credentialStore.Save(h.Id, h.AdminUser ?? string.Empty, h.AdminPassword ?? string.Empty);
            }

            // Prune credential files for hosts that were removed in this edit.
            if (editingClone?.Hosts != null)
            {
                var kept = new HashSet<System.Guid>(Settings.Hosts?.Select(h => h.Id) ?? System.Linq.Enumerable.Empty<System.Guid>());
                foreach (var old in editingClone.Hosts)
                {
                    if (old != null && !kept.Contains(old.Id))
                    {
                        credentialStore.Delete(old.Id);
                    }
                }
            }

            plugin.SavePluginSettings(Settings);
            plugin.OnSettingsSaved();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (Settings?.Hosts == null) return true;

            foreach (var h in Settings.Hosts)
            {
                if (h == null) continue;
                if (string.IsNullOrWhiteSpace(h.Label))
                    errors.Add(Localize("LOC_SunshineLibrary_Validation_LabelRequired"));
                if (string.IsNullOrWhiteSpace(h.Address))
                    errors.Add(Localize("LOC_SunshineLibrary_Validation_AddressRequired"));
                if (h.Port <= 0 || h.Port > 65535)
                    errors.Add(Localize("LOC_SunshineLibrary_Validation_PortRange"));
                if (!string.IsNullOrEmpty(h.Address) && HasForbiddenAddressChars(h.Address))
                    errors.Add(Localize("LOC_SunshineLibrary_Validation_AddressChars"));
            }

            var moonlightPath = Settings.Client?.GetPath(Services.Clients.MoonlightClient.ClientId);
            if (!string.IsNullOrWhiteSpace(moonlightPath) && !File.Exists(moonlightPath))
                errors.Add(Localize("LOC_SunshineLibrary_Validation_MoonlightPathInvalid"));

            return errors.Count == 0;
        }

        /// <summary>Rejects schemes, paths, and shell metacharacters per PLAN §13c.</summary>
        private static bool HasForbiddenAddressChars(string s)
        {
            if (s.Contains("://") || s.Contains("/") || s.Contains("\\")) return true;
            foreach (var c in s)
            {
                if (char.IsWhiteSpace(c)) return true;
                if (c == '&' || c == '|' || c == '>' || c == '<' || c == ';' || c == '"' || c == '\'' || c == '`') return true;
            }
            return false;
        }

        private static string Localize(string key)
        {
            var s = ResourceProvider.GetString(key);
            return string.IsNullOrEmpty(s) ? key : s;
        }
    }
}
