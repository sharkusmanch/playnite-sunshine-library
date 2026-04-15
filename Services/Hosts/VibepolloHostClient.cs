using SunshineLibrary.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SunshineLibrary.Services.Hosts
{
    /// <summary>
    /// Vibepollo (Nonary/Vibepollo): Apollo fork that exposes Playnite library metadata
    /// via /api/playnite/games. Inherits Apollo's session-cookie authentication.
    ///
    /// ListAppsAsync fetches both /api/apps (for stable UUIDs and cover art) and
    /// /api/playnite/games (for PluginName and Categories), joining on app name.
    /// The Playnite fetch is best-effort — failure falls back to plain Apollo behaviour.
    /// </summary>
    public sealed class VibepolloHostClient : ApolloHostClient
    {
        public override ServerType ServerType => ServerType.Vibepollo;

        public VibepolloHostClient(HostConfig config) : base(config) { }

        internal VibepolloHostClient(HostConfig config, HttpMessageHandler handler) : base(config, handler) { }

        // ── ListAppsAsync ─────────────────────────────────────────────────────────

        public override async Task<HostResult<IReadOnlyList<RemoteApp>>> ListAppsAsync(CancellationToken ct)
        {
            var login = await EnsureSessionAsync(ct).ConfigureAwait(false);
            if (!login.IsOk) return Fail(login);

            // Primary fetch: /api/apps gives stable UUIDs used for cover art and GameId.
            var appsResult = await GetJsonAsync<ApolloAppsResponse>("api/apps", ct).ConfigureAwait(false);
            if (!appsResult.IsOk)
            {
                if (appsResult.Kind == HostResultKind.AuthFailed) InvalidateSession();
                return Fail(appsResult);
            }

            // Enrichment fetch: /api/playnite/games gives PluginName + Categories.
            // Non-fatal — if Playnite is closed or the endpoint fails we just skip enrichment.
            var playniteResult = await GetJsonAsync<List<VibepolloPlayniteGameDto>>("api/playnite/games", ct).ConfigureAwait(false);
            var playniteByName = BuildPlayniteLookup(playniteResult.IsOk ? playniteResult.Value : null);

            var apps = appsResult.Value?.Apps ?? new List<ApolloAppDto>();
            var list = new List<RemoteApp>(apps.Count);
            for (int i = 0; i < apps.Count; i++)
            {
                var a = apps[i];
                if (string.IsNullOrWhiteSpace(a?.Name)) continue;

                playniteByName.TryGetValue(a.Name, out var playnite);

                list.Add(new RemoteApp
                {
                    StableId = !string.IsNullOrWhiteSpace(a.Uuid) ? a.Uuid : FallbackId(a.Name, a.Cmd, i),
                    Name = a.Name,
                    Index = a.Index ?? i,
                    PluginName = playnite?.PluginName,
                    Categories = playnite?.Categories,
                    PlaytimeMinutes = playnite?.PlaytimeMinutes ?? 0,
                });
            }
            return HostResult<IReadOnlyList<RemoteApp>>.Ok(list);
        }

        // ── FetchCoverAsync ───────────────────────────────────────────────────────

        /// <summary>
        /// Vibepollo exposes covers at /api/apps/{uuid}/cover — UUID-based, no index needed.
        /// </summary>
        public override async Task<HostResult<byte[]>> FetchCoverAsync(RemoteApp app, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(app?.StableId) || app.StableId.StartsWith("fallback:", StringComparison.Ordinal))
                return HostResult<byte[]>.Ok(null);

            var login = await EnsureSessionAsync(ct).ConfigureAwait(false);
            if (!login.IsOk) return ToGeneric<byte[]>(login);

            var r = await GetBytesAsync($"api/apps/{app.StableId}/cover", ct).ConfigureAwait(false);
            if (r.Kind == HostResultKind.AuthFailed) InvalidateSession();
            return r;
        }

        // ── ForceSyncAsync ────────────────────────────────────────────────────────

        /// <summary>
        /// Tells Vibepollo to reconcile its Playnite library (POST /api/playnite/force_sync).
        /// Synchronous on the server side — returns when the host's app list is up to date.
        /// </summary>
        public override async Task<HostResult> ForceSyncAsync(CancellationToken ct)
        {
            var login = await EnsureSessionAsync(ct).ConfigureAwait(false);
            if (!login.IsOk) return login;

            var r = await PostJsonAsync("api/playnite/force_sync", null, null, ct).ConfigureAwait(false);
            if (r.Kind == HostResultKind.AuthFailed) InvalidateSession();
            return r;
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Build a case-insensitive name → DTO map from the Playnite games list.
        /// Only installed games are included; first entry wins on duplicate names.
        /// Returns an empty map if the input is null.
        /// </summary>
        private static Dictionary<string, VibepolloPlayniteGameDto> BuildPlayniteLookup(
            List<VibepolloPlayniteGameDto> games)
        {
            var map = new Dictionary<string, VibepolloPlayniteGameDto>(StringComparer.OrdinalIgnoreCase);
            if (games == null) return map;
            foreach (var g in games)
            {
                if (g == null || string.IsNullOrWhiteSpace(g.Name) || !g.Installed) continue;
                if (!map.ContainsKey(g.Name)) map[g.Name] = g;
            }
            return map;
        }

        private static HostResult<IReadOnlyList<RemoteApp>> Fail(HostResult r)
        {
            switch (r.Kind)
            {
                case HostResultKind.AuthFailed: return HostResult<IReadOnlyList<RemoteApp>>.AuthFailed();
                case HostResultKind.CertMismatch: return HostResult<IReadOnlyList<RemoteApp>>.CertMismatch(r.NewCertFingerprintSpkiSha256);
                case HostResultKind.CertMissing: return HostResult<IReadOnlyList<RemoteApp>>.CertMissing();
                case HostResultKind.Timeout: return HostResult<IReadOnlyList<RemoteApp>>.Timeout();
                case HostResultKind.ServerError: return HostResult<IReadOnlyList<RemoteApp>>.ServerError(r.StatusCode ?? 0, r.Message);
                case HostResultKind.Cancelled: return HostResult<IReadOnlyList<RemoteApp>>.Cancelled();
                default: return HostResult<IReadOnlyList<RemoteApp>>.Unreachable(r.Message);
            }
        }

        private static HostResult<IReadOnlyList<RemoteApp>> Fail<T>(HostResult<T> r) => Fail(r.AsStatus());
    }
}
