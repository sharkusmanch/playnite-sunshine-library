using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace SunshineLibrary.Models
{
    public class HostConfig
    {
        public Guid Id { get; set; }
        public string Label { get; set; }
        public string Address { get; set; }
        public int Port { get; set; } = 47990;
        public string AdminUser { get; set; }

        /// <summary>
        /// In-memory only. Never persists to the plugin settings JSON — the real
        /// credential lives in a per-host DPAPI blob managed by <c>CredentialStore</c>.
        /// Hydrated at plugin load from the store; written to the store on settings save.
        /// </summary>
        [JsonIgnore]
        public string AdminPassword { get; set; }

        /// <summary>
        /// Vibepollo/Apollo Bearer token. When set, used instead of session-cookie login.
        /// In-memory only — stored in the same DPAPI blob as AdminPassword.
        /// </summary>
        [JsonIgnore]
        public string ApiToken { get; set; }
        public string CertFingerprintSpkiSha256 { get; set; }
        public ServerType ServerType { get; set; } = ServerType.Unknown;
        public string ServerVersion { get; set; }
        public byte[] MacAddress { get; set; }
        public bool Enabled { get; set; } = true;
        public List<string> ExcludedAppNames { get; set; } = new List<string>();
        public StreamOverrides Defaults { get; set; } = new StreamOverrides();

        /// <summary>
        /// Per-host override for orphan deletion. Null means inherit the global
        /// <c>AutoRemoveOrphanedGames</c> setting; true/false override it for this host only.
        /// </summary>
        public bool? AutoRemoveOrphanedGames { get; set; } = null;

        public string BaseUrl => $"https://{Address}:{Port}";
    }
}
