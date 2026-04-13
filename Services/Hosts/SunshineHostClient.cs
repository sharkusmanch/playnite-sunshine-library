using Newtonsoft.Json.Linq;
using SunshineLibrary.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SunshineLibrary.Services.Hosts
{
    /// <summary>
    /// Upstream Sunshine: apps keyed by array index (unstable under reorder), no uuid.
    /// Mutating endpoints require a CSRF token; see <see cref="CsrfTokenCache"/>.
    /// </summary>
    public sealed class SunshineHostClient : HostClient
    {
        public override ServerType ServerType => ServerType.Sunshine;

        // 5-minute TTL mirrors Sunshine's 1-hour server TTL but refreshes more aggressively
        // so clock skew and token rotation don't wedge us on a stale value.
        private static readonly TimeSpan CsrfTtl = TimeSpan.FromMinutes(5);

        internal readonly CsrfTokenCache Csrf;

        public SunshineHostClient(HostConfig config) : base(config)
        {
            Csrf = new CsrfTokenCache(CsrfTtl);
        }

        // Test-only seam: inject a fake handler. Not used in production.
        internal SunshineHostClient(HostConfig config, HttpMessageHandler handler)
            : base(config, handler)
        {
            Csrf = new CsrfTokenCache(CsrfTtl);
        }

        public override async Task<HostResult<IReadOnlyList<RemoteApp>>> ListAppsAsync(CancellationToken ct)
        {
            var raw = await GetJsonAsync<SunshineAppsResponse>("api/apps", ct).ConfigureAwait(false);
            if (!raw.IsOk) return ToStatus(raw);

            var apps = raw.Value?.Apps ?? new List<SunshineAppDto>();
            var list = new List<RemoteApp>(apps.Count);
            for (int i = 0; i < apps.Count; i++)
            {
                var a = apps[i];
                if (string.IsNullOrWhiteSpace(a?.Name)) continue;
                list.Add(new RemoteApp
                {
                    StableId = ComputeStableId(a.Name, a.Cmd),
                    Name = a.Name,
                    Index = a.Index ?? i,
                });
            }
            return HostResult<IReadOnlyList<RemoteApp>>.Ok(list);
        }

        public override Task<HostResult<byte[]>> FetchCoverAsync(RemoteApp app, CancellationToken ct)
        {
            // Sunshine admin HTTP has historically served covers at /appasset/{index}/box.png.
            // 404 is normal and handled upstream. Exact path is in §18 open questions.
            if (app?.Index == null) return Task.FromResult(HostResult<byte[]>.Ok(null));
            return GetBytesAsync($"appasset/{app.Index.Value}/box.png", ct);
        }

        public override async Task<HostResult> CloseCurrentAppAsync(CancellationToken ct)
        {
            // Fast-fail: outer cancellation short-circuits before any network IO.
            if (ct.IsCancellationRequested) return HostResult.Cancelled();

            var first = await PostCloseOnceAsync(ct).ConfigureAwait(false);
            if (first.Kind != HostResultKind.ServerError || (first.StatusCode ?? 0) != 403)
            {
                return first;
            }

            // 403: token likely expired or mismatched. Refresh and retry exactly once.
            Csrf.Invalidate();
            var retry = await PostCloseOnceAsync(ct).ConfigureAwait(false);
            if (retry.Kind == HostResultKind.ServerError && (retry.StatusCode ?? 0) == 403)
            {
                return HostResult.AuthFailed();
            }
            return retry;
        }

        private async Task<HostResult> PostCloseOnceAsync(CancellationToken ct)
        {
            var tokenResult = await EnsureCsrfTokenAsync(ct).ConfigureAwait(false);
            if (!tokenResult.IsOk) return tokenResult.AsStatus();

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "X-CSRF-Token", tokenResult.Value },
            };
            return await PostJsonAsync("api/apps/close", null, headers, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns a cached CSRF token, fetching a fresh one if missing/expired/invalidated.
        /// Never logs the token itself.
        /// </summary>
        internal async Task<HostResult<string>> EnsureCsrfTokenAsync(CancellationToken ct)
        {
            if (Csrf.TryGet(out var cached))
            {
                return HostResult<string>.Ok(cached);
            }

            var resp = await PostJsonAsync<JObject>("api/csrf-token", null, null, ct).ConfigureAwait(false);
            if (!resp.IsOk)
            {
                return HostResult<string>.ServerError(resp.StatusCode ?? 0, resp.Message);
            }

            var token = ExtractCsrfToken(resp.Value);
            if (string.IsNullOrEmpty(token))
            {
                logger.Warn($"[{Config.Label}] /api/csrf-token responded without a recognizable token field");
                return HostResult<string>.ServerError(0, "csrf-token response missing field");
            }

            Csrf.Set(token);
            return HostResult<string>.Ok(token);
        }

        /// <summary>
        /// Sunshine documents <c>csrf_token</c> but tolerate common casings. As a last
        /// resort pick the first string field whose key contains "csrf" or "token".
        /// </summary>
        internal static string ExtractCsrfToken(JObject obj)
        {
            if (obj == null) return null;

            // Preferred field names, in priority order.
            string[] preferred = { "csrf_token", "csrfToken", "csrf", "token" };
            foreach (var name in preferred)
            {
                var v = obj[name];
                if (v != null && v.Type == JTokenType.String)
                {
                    var s = (string)v;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }

            // Fuzzy fallback: first string-valued property whose name contains
            // "csrf" or "token" (case-insensitive).
            foreach (var prop in obj.Properties())
            {
                var k = prop.Name ?? string.Empty;
                if (prop.Value?.Type != JTokenType.String) continue;
                if (k.IndexOf("csrf", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    k.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var s = (string)prop.Value;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            return null;
        }

        private static string ComputeStableId(string name, string cmd)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes((name ?? string.Empty) + "\0" + (cmd ?? string.Empty));
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

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

        /// <summary>
        /// Small thread-safe holder for the CSRF token and its TTL. Exposed as
        /// <c>internal</c> so unit tests can exercise expiry/invalidation logic
        /// without touching HTTP.
        /// </summary>
        internal sealed class CsrfTokenCache
        {
            private readonly object gate = new object();
            private readonly TimeSpan ttl;
            private readonly Func<DateTime> now;

            private string token;
            private DateTime expiresAt;
            private bool needsRefresh;

            public CsrfTokenCache(TimeSpan ttl) : this(ttl, () => DateTime.UtcNow) { }

            // Overload for deterministic tests.
            internal CsrfTokenCache(TimeSpan ttl, Func<DateTime> clock)
            {
                this.ttl = ttl;
                this.now = clock ?? (() => DateTime.UtcNow);
                this.needsRefresh = true;
            }

            public bool TryGet(out string value)
            {
                lock (gate)
                {
                    if (needsRefresh || string.IsNullOrEmpty(token) || now() >= expiresAt)
                    {
                        value = null;
                        return false;
                    }
                    value = token;
                    return true;
                }
            }

            public void Set(string newToken)
            {
                lock (gate)
                {
                    token = newToken;
                    expiresAt = now().Add(ttl);
                    needsRefresh = false;
                }
            }

            public void Invalidate()
            {
                lock (gate)
                {
                    needsRefresh = true;
                    token = null;
                    expiresAt = DateTime.MinValue;
                }
            }
        }
    }
}
