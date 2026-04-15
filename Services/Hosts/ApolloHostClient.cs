using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SunshineLibrary.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SunshineLibrary.Services.Hosts
{
    /// <summary>
    /// Apollo (ClassicOldSong fork): uses session-cookie authentication
    /// (POST /api/login → Set-Cookie: auth=...) rather than HTTP Basic Auth.
    /// All protected endpoints require the cookie; the base-class Authorization
    /// header is cleared and replaced with a lazy login on first use.
    /// </summary>
    public class ApolloHostClient : HostClient
    {
        public override ServerType ServerType => ServerType.Apollo;

        private readonly SemaphoreSlim _loginGate = new SemaphoreSlim(1, 1);
        private volatile bool _sessionEstablished;
        private volatile bool _loginFailed;

        public ApolloHostClient(HostConfig config) : base(config)
        {
            // Apollo rejects the Basic auth header; clear it so it is never sent.
            Http.DefaultRequestHeaders.Authorization = null;
        }

        // Test-only seam.
        internal ApolloHostClient(HostConfig config, HttpMessageHandler handler) : base(config, handler)
        {
            Http.DefaultRequestHeaders.Authorization = null;
        }

        // ── session management ────────────────────────────────────────────────

        /// <summary>
        /// POST /api/login and store the returned auth cookie.
        /// The underlying HttpClientHandler has UseCookies=true, so the Set-Cookie
        /// response header is captured automatically and replayed on every subsequent
        /// request to the same origin.
        /// </summary>
        protected async Task<HostResult> EnsureSessionAsync(CancellationToken ct)
        {
            if (_sessionEstablished) return HostResult.Ok();
            if (_loginFailed) return HostResult.AuthFailed(); // credentials rejected — don't hammer server

            await _loginGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_sessionEstablished) return HostResult.Ok(); // double-checked
                if (_loginFailed) return HostResult.AuthFailed();

                if (string.IsNullOrEmpty(Config.AdminUser))
                {
                    // No credentials configured — proceed unauthenticated.
                    _sessionEstablished = true;
                    return HostResult.Ok();
                }

                var body = new LoginRequest
                {
                    Username = Config.AdminUser,
                    Password = Config.AdminPassword ?? string.Empty
                };
                var r = await PostJsonAsync("api/login", body, null, ct).ConfigureAwait(false);
                if (!r.IsOk)
                {
                    if (r.Kind == HostResultKind.AuthFailed) _loginFailed = true;
                    return r;
                }

                _sessionEstablished = true;
                return HostResult.Ok();
            }
            finally
            {
                _loginGate.Release();
            }
        }

        // Resets the session so the next call re-authenticates (e.g. after cookie expiry).
        // Does NOT reset _loginFailed — a credential failure stays failed for this instance's lifetime.
        protected void InvalidateSession() => _sessionEstablished = false;

        // ── HostClient overrides ──────────────────────────────────────────────

        public override async Task<HostResult<JObject>> ProbeConfigAsync(CancellationToken ct)
        {
            var login = await EnsureSessionAsync(ct).ConfigureAwait(false);
            if (!login.IsOk) return ToGeneric<JObject>(login);
            var r = await GetJsonAsync<JObject>("api/config", ct).ConfigureAwait(false);
            if (r.Kind == HostResultKind.AuthFailed) InvalidateSession();
            return r;
        }

        public override async Task<HostResult<IReadOnlyList<RemoteApp>>> ListAppsAsync(CancellationToken ct)
        {
            var login = await EnsureSessionAsync(ct).ConfigureAwait(false);
            if (!login.IsOk) return ToGeneric<IReadOnlyList<RemoteApp>>(login);

            var raw = await GetJsonAsync<ApolloAppsResponse>("api/apps", ct).ConfigureAwait(false);
            if (!raw.IsOk)
            {
                if (raw.Kind == HostResultKind.AuthFailed) InvalidateSession();
                return ToStatus(raw);
            }

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

        public override async Task<HostResult<byte[]>> FetchCoverAsync(RemoteApp app, CancellationToken ct)
        {
            if (app?.Index == null) return HostResult<byte[]>.Ok(null);

            var login = await EnsureSessionAsync(ct).ConfigureAwait(false);
            if (!login.IsOk) return ToGeneric<byte[]>(login);

            var r = await GetBytesAsync($"appasset/{app.Index.Value}/box.png", ct).ConfigureAwait(false);
            if (r.Kind == HostResultKind.AuthFailed) InvalidateSession();
            return r;
        }

        public override async Task<HostResult> CloseCurrentAppAsync(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return HostResult.Cancelled();

            var login = await EnsureSessionAsync(ct).ConfigureAwait(false);
            if (!login.IsOk) return login;

            var r = await PostJsonAsync("api/apps/close", null, null, ct).ConfigureAwait(false);
            if (r.Kind == HostResultKind.AuthFailed) InvalidateSession();
            return r;
        }

        /// <summary>
        /// Probes /api/playnite/status to distinguish Vibepollo from plain Apollo.
        /// Vibepollo returns 200; Apollo returns 404 for this endpoint.
        /// Called by <see cref="HostClientFactory"/> during server-type detection.
        /// </summary>
        public async Task<bool> IsVibepolloAsync(CancellationToken ct)
        {
            var login = await EnsureSessionAsync(ct).ConfigureAwait(false);
            if (!login.IsOk) return false;

            // GetJsonAsync catches and classifies all network exceptions internally.
            // This outer catch is a defensive belt-and-suspenders guard against any
            // unexpected throw that would otherwise surface as a detection failure.
            try
            {
                var r = await GetJsonAsync<Newtonsoft.Json.Linq.JToken>("api/playnite/status", ct).ConfigureAwait(false);
                return r.IsOk;
            }
            catch
            {
                return false;
            }
        }

        public override void Dispose()
        {
            _loginGate.Dispose();
            base.Dispose();
        }

        // ── helpers ───────────────────────────────────────────────────────────

        protected static string FallbackId(string name, string cmd, int idx)
            => $"fallback:{idx}:{name?.GetHashCode() ?? 0:x}:{cmd?.GetHashCode() ?? 0:x}";

        /// <summary>Propagate a typed error result into the apps-list result type.</summary>
        private static HostResult<IReadOnlyList<RemoteApp>> ToStatus<T>(HostResult<T> r)
            => ToGeneric<IReadOnlyList<RemoteApp>>(r);

        /// <summary>Lift a status-only <see cref="HostResult"/> into a typed result.</summary>
        protected static HostResult<T> ToGeneric<T>(HostResult r)
        {
            switch (r.Kind)
            {
                case HostResultKind.Ok: throw new InvalidOperationException("ToGeneric called on a successful result");
                case HostResultKind.AuthFailed: return HostResult<T>.AuthFailed();
                case HostResultKind.CertMismatch: return HostResult<T>.CertMismatch(r.NewCertFingerprintSpkiSha256);
                case HostResultKind.CertMissing: return HostResult<T>.CertMissing();
                case HostResultKind.Timeout: return HostResult<T>.Timeout();
                case HostResultKind.ServerError: return HostResult<T>.ServerError(r.StatusCode ?? 0, r.Message);
                case HostResultKind.Cancelled: return HostResult<T>.Cancelled();
                default: return HostResult<T>.Unreachable(r.Message);
            }
        }

        private class LoginRequest
        {
            [JsonProperty("username")]
            public string Username { get; set; }

            [JsonProperty("password")]
            public string Password { get; set; }
        }
    }
}
