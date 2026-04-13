using Newtonsoft.Json;
using Playnite.SDK.Models;
using SunshineLibrary.Models;
using System.Collections.Generic;

namespace SunshineLibrary.Services.Clients
{
    /// <summary>
    /// A local process we spawn to stream from a remote host. Moonlight is v1's only
    /// implementation; the abstraction is here so future clients (Artemis, Moonlight
    /// Embedded, forks) drop in without touching host, sync, or override code.
    /// </summary>
    public abstract class StreamClient
    {
        public abstract string Id { get; }            // "moonlight-qt"
        public abstract string DisplayName { get; }   // "Moonlight"

        public abstract ClientAvailability ProbeAvailability(ClientSettings settings);

        public abstract ClientLaunchSpec BuildLaunch(
            HostConfig host,
            RemoteApp app,
            StreamOverrides merged,
            ClientDisplayInfo display,
            ClientSettings settings = null);
    }

    public class ClientAvailability
    {
        public bool Installed { get; set; }
        public string ExecutablePath { get; set; }
        public string Version { get; set; }
        public string UnavailableReason { get; set; }
    }

    public class ClientLaunchSpec
    {
        public string Executable { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public string TrackedProcessName { get; set; }
        public TrackingMode TrackingMode { get; set; } = TrackingMode.Process;
    }

    /// <summary>Plugin-wide client settings.</summary>
    public class ClientSettings
    {
        public string ActiveClientId { get; set; } = "moonlight-qt";

        /// <summary>Per-client absolute executable paths, keyed by <see cref="StreamClient.Id"/>. Empty = probe defaults.</summary>
        public Dictionary<string, string> ClientPaths { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Legacy settings loader: v0.1 stored a single top-level <c>MoonlightPath</c>.
        /// When deserialized, seed <see cref="ClientPaths"/> with the Moonlight entry so
        /// existing installs don't lose their configured path. Setter writes only; the
        /// property never serializes out — forward-written settings use <see cref="ClientPaths"/>.
        /// </summary>
        [JsonProperty("MoonlightPath")]
        internal string LegacyMoonlightPath
        {
            get => null;
            set
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                if (ClientPaths == null) ClientPaths = new Dictionary<string, string>();
                if (!ClientPaths.ContainsKey("moonlight-qt"))
                {
                    ClientPaths["moonlight-qt"] = value;
                }
            }
        }

        /// <summary>Lookup helper: returns the configured path for a client Id, or null if unset.</summary>
        public string GetPath(string clientId)
        {
            if (string.IsNullOrEmpty(clientId) || ClientPaths == null) return null;
            return ClientPaths.TryGetValue(clientId, out var p) && !string.IsNullOrWhiteSpace(p) ? p : null;
        }

        /// <summary>Set or clear (null) the stored path for a client Id.</summary>
        public void SetPath(string clientId, string path)
        {
            if (string.IsNullOrEmpty(clientId)) return;
            if (ClientPaths == null) ClientPaths = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(path)) ClientPaths.Remove(clientId);
            else ClientPaths[clientId] = path;
        }
    }
}
