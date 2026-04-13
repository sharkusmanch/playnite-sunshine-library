using SunshineLibrary.Models;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SunshineLibrary.Services.Hosts
{
    /// <summary>
    /// Apollo (ClassicOldSong fork): guarantees `uuid` on each app, adds
    /// /api/otp, /api/clients/list, /api/clients/update, /api/apps/launch.
    /// M1 implements only the shared surface; Apollo-only endpoints come in M5.
    /// </summary>
    public sealed class ApolloHostClient : HostClient
    {
        public override ServerType ServerType => ServerType.Apollo;

        public ApolloHostClient(HostConfig config) : base(config) { }

        // Test-only seam.
        internal ApolloHostClient(HostConfig config, HttpMessageHandler handler) : base(config, handler) { }

        public override async Task<HostResult<IReadOnlyList<RemoteApp>>> ListAppsAsync(CancellationToken ct)
        {
            var raw = await GetJsonAsync<ApolloAppsResponse>("api/apps", ct).ConfigureAwait(false);
            if (!raw.IsOk) return ToStatus(raw);

            var apps = raw.Value?.Apps ?? new List<ApolloAppDto>();
            var list = new List<RemoteApp>(apps.Count);
            for (int i = 0; i < apps.Count; i++)
            {
                var a = apps[i];
                if (string.IsNullOrWhiteSpace(a?.Name)) continue;
                list.Add(new RemoteApp
                {
                    StableId = !string.IsNullOrWhiteSpace(a.Uuid) ? a.Uuid : FallbackId(a.Name, a.Cmd, i),
                    Name = a.Name,
                    Index = a.Index ?? i,
                });
            }
            return HostResult<IReadOnlyList<RemoteApp>>.Ok(list);
        }

        public override Task<HostResult<byte[]>> FetchCoverAsync(RemoteApp app, CancellationToken ct)
        {
            // Apollo historically uses the same /appasset/{index}/box.png path as Sunshine.
            // A uuid-keyed endpoint may exist — verify at implementation time; fall back gracefully on 404.
            if (app?.Index == null) return Task.FromResult(HostResult<byte[]>.Ok(null));
            return GetBytesAsync($"appasset/{app.Index.Value}/box.png", ct);
        }

        public override Task<HostResult> CloseCurrentAppAsync(CancellationToken ct)
        {
            // Apollo's confighttp.cpp (ClassicOldSong/Apollo, master@2026-04) defines
            // closeApp() with only validateContentType + authenticate — no CSRF check
            // and no /api/csrf-token endpoint exists. Verified from source; simple POST.
            if (ct.IsCancellationRequested) return Task.FromResult(HostResult.Cancelled());
            return PostJsonAsync("api/apps/close", null, null, ct);
        }

        private static string FallbackId(string name, string cmd, int idx)
            => $"fallback:{idx}:{name?.GetHashCode() ?? 0:x}:{cmd?.GetHashCode() ?? 0:x}";

        private static HostResult<IReadOnlyList<RemoteApp>> ToStatus<T>(HostResult<T> r)
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
    }
}
