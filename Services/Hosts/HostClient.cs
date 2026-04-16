using Newtonsoft.Json;
using Playnite.SDK;
using SunshineLibrary.Models;
using SunshineLibrary.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SunshineLibrary.Services.Hosts
{
    /// <summary>
    /// Abstract base. One instance per configured host; owns its HttpClient and
    /// pinned cert handler for the life of the plugin.
    /// </summary>
    public abstract class HostClient : IDisposable
    {
        protected static readonly ILogger logger = LogManager.GetLogger();

        public HostConfig Config { get; }
        public abstract ServerType ServerType { get; }

        protected readonly HttpClient Http;
        private readonly PinningWebRequestHandler handler;

        // Parse limits (§13 hardening).
        protected const long MaxResponseBytes = 4L * 1024 * 1024;
        protected const int MaxJsonDepth = 32;

        static HostClient()
        {
            // Ensure TLS 1.2 (and 1.3 where available). Numeric cast because
            // SecurityProtocolType.Tls13 = 12288 may not exist on older net462 targeting packs.
            try
            {
                ServicePointManager.SecurityProtocol |=
                    (SecurityProtocolType)3072 /* Tls12 */ |
                    (SecurityProtocolType)12288 /* Tls13 */;
            }
            catch { /* some OSes won't accept Tls13 — Tls12 is enough */ }
        }

        protected HostClient(HostConfig config)
            : this(config, null) { }

        /// <summary>
        /// Test-only seam: when <paramref name="messageHandler"/> is non-null we skip the
        /// pinning WebRequestHandler and use the injected handler instead. Production
        /// code paths must use the single-argument constructor.
        /// </summary>
        internal HostClient(HostConfig config, HttpMessageHandler messageHandler)
        {
            Config = config;
            if (messageHandler == null)
            {
                handler = new PinningWebRequestHandler(config);
                Http = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(10),
                    BaseAddress = new Uri(config.BaseUrl + "/"),
                };
            }
            else
            {
                handler = null;
                Http = new HttpClient(messageHandler)
                {
                    Timeout = TimeSpan.FromSeconds(10),
                    BaseAddress = new Uri(config.BaseUrl + "/"),
                };
            }
            if (!string.IsNullOrEmpty(config.AdminUser))
            {
                var cred = Encoding.UTF8.GetBytes($"{config.AdminUser}:{config.AdminPassword ?? string.Empty}");
                Http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", Convert.ToBase64String(cred));
            }
            Http.DefaultRequestHeaders.Accept.Clear();
            Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Bump connection limit + DNS lease (net462 caches DNS forever without it).
            try
            {
                var sp = ServicePointManager.FindServicePoint(Http.BaseAddress);
                sp.ConnectionLeaseTimeout = 5 * 60 * 1000;
                sp.ConnectionLimit = Math.Max(sp.ConnectionLimit, 8);
            }
            catch { /* non-fatal */ }
        }

        public virtual void Dispose() => Http?.Dispose();

        public string LastObservedCertFingerprintSpkiSha256 => handler?.LastObservedSpkiSha256;

        // --- abstract surface -----------------------------------------------------

        /// <summary>Low-level access to /api/config for flavor detection. Returns the raw JSON object.</summary>
        public virtual Task<HostResult<Newtonsoft.Json.Linq.JObject>> ProbeConfigAsync(CancellationToken ct)
            => GetJsonAsync<Newtonsoft.Json.Linq.JObject>("api/config", ct);

        public abstract Task<HostResult<IReadOnlyList<RemoteApp>>> ListAppsAsync(CancellationToken ct);

        /// <summary>Fetch a cover for an app. Returns null content on 404 or absent covers.</summary>
        public abstract Task<HostResult<byte[]>> FetchCoverAsync(RemoteApp app, CancellationToken ct);

        public abstract Task<HostResult> CloseCurrentAppAsync(CancellationToken ct);

        /// <summary>
        /// Ask the host to reconcile its upstream game library before the next app-list fetch.
        /// Vibepollo overrides this to POST /api/playnite/force_sync; all other flavors no-op.
        /// Callers treat failure as advisory — the sync continues with whatever the host has.
        /// </summary>
        public virtual Task<HostResult> ForceSyncAsync(CancellationToken ct)
            => Task.FromResult(HostResult.Ok());

        // --- shared helpers -------------------------------------------------------

        protected async Task<HostResult<T>> GetJsonAsync<T>(string relativePath, CancellationToken ct)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, relativePath))
                using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    return await ParseJsonResponseAsync<T>(resp).ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException)
            {
                return ct.IsCancellationRequested ? HostResult<T>.Cancelled() : HostResult<T>.Timeout();
            }
            catch (OperationCanceledException)
            {
                return HostResult<T>.Cancelled();
            }
            catch (HttpRequestException ex)
            {
                return ClassifyHttpError<T>(ex);
            }
            catch (Exception ex)
            {
                logger.Debug(ex, $"[{Config.Label}] GET {relativePath} failed");
                return HostResult<T>.Unreachable(ex.Message);
            }
        }

        protected async Task<HostResult<byte[]>> GetBytesAsync(string relativePath, CancellationToken ct)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, relativePath))
                using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (resp.StatusCode == HttpStatusCode.NotFound) return HostResult<byte[]>.Ok(null);
                    if (resp.StatusCode == HttpStatusCode.Unauthorized) return HostResult<byte[]>.AuthFailed();
                    if (!resp.IsSuccessStatusCode) return HostResult<byte[]>.ServerError((int)resp.StatusCode, resp.ReasonPhrase);
                    var len = resp.Content.Headers.ContentLength ?? 0;
                    if (len > MaxResponseBytes) return HostResult<byte[]>.ServerError(0, "response too large");
                    var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (bytes.Length > MaxResponseBytes) return HostResult<byte[]>.ServerError(0, "response too large");
                    return HostResult<byte[]>.Ok(bytes);
                }
            }
            catch (TaskCanceledException) { return ct.IsCancellationRequested ? HostResult<byte[]>.Cancelled() : HostResult<byte[]>.Timeout(); }
            catch (OperationCanceledException) { return HostResult<byte[]>.Cancelled(); }
            catch (HttpRequestException ex) { return ClassifyHttpError<byte[]>(ex); }
            catch (Exception ex)
            {
                logger.Debug(ex, $"[{Config.Label}] GET {relativePath} (bytes) failed");
                return HostResult<byte[]>.Unreachable(ex.Message);
            }
        }

        /// <summary>
        /// POST a JSON body and ignore the response payload. Returns a status-only
        /// <see cref="HostResult"/>. Mirrors <see cref="GetJsonAsync{T}"/> for error
        /// classification; callers (e.g. Sunshine CSRF retry) inspect the Kind.
        /// </summary>
        protected async Task<HostResult> PostJsonAsync(
            string relativePath,
            object body,
            IReadOnlyDictionary<string, string> extraHeaders,
            CancellationToken ct)
        {
            try
            {
                using (var req = BuildPostRequest(relativePath, body, extraHeaders))
                {
                    logger.Trace($"[{Config.Label}] {SafeLogging.DescribeRequest(req)}");
                    using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        logger.Trace($"[{Config.Label}] {SafeLogging.DescribeResponse(resp)}");
                        if (resp.StatusCode == HttpStatusCode.Unauthorized) return HostResult.AuthFailed();
                        if (!resp.IsSuccessStatusCode)
                        {
                            return HostResult.ServerError((int)resp.StatusCode, resp.ReasonPhrase);
                        }
                        return HostResult.Ok();
                    }
                }
            }
            catch (TaskCanceledException)
            {
                return ct.IsCancellationRequested ? HostResult.Cancelled() : HostResult.Timeout();
            }
            catch (OperationCanceledException)
            {
                return HostResult.Cancelled();
            }
            catch (HttpRequestException ex)
            {
                return ClassifyHttpError<object>(ex).AsStatus();
            }
            catch (Exception ex)
            {
                logger.Debug(ex, $"[{Config.Label}] POST {relativePath} failed");
                return HostResult.Unreachable(ex.Message);
            }
        }

        private HttpRequestMessage BuildPostRequest(
            string relativePath,
            object body,
            IReadOnlyDictionary<string, string> extraHeaders)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, relativePath);
            if (body == null)
            {
                req.Content = new ByteArrayContent(new byte[0]);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                req.Content.Headers.ContentLength = 0;
            }
            else
            {
                var json = JsonConvert.SerializeObject(body, StreamOverrides.JsonSettings);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            if (extraHeaders != null)
            {
                foreach (var kv in extraHeaders)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                    req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
            }
            return req;
        }

        /// <summary>
        /// POST a JSON body and deserialize the response JSON as <typeparamref name="T"/>.
        /// If <paramref name="body"/> is null the request is sent with an empty body
        /// (Content-Length: 0) — deliberately NOT the literal "null" JSON token.
        /// </summary>
        protected async Task<HostResult<T>> PostJsonAsync<T>(
            string relativePath,
            object body,
            IReadOnlyDictionary<string, string> extraHeaders,
            CancellationToken ct)
        {
            try
            {
                using (var req = BuildPostRequest(relativePath, body, extraHeaders))
                {
                    logger.Trace($"[{Config.Label}] {SafeLogging.DescribeRequest(req)}");
                    using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        logger.Trace($"[{Config.Label}] {SafeLogging.DescribeResponse(resp)}");
                        return await ParseJsonResponseAsync<T>(resp).ConfigureAwait(false);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                return ct.IsCancellationRequested ? HostResult<T>.Cancelled() : HostResult<T>.Timeout();
            }
            catch (OperationCanceledException)
            {
                return HostResult<T>.Cancelled();
            }
            catch (HttpRequestException ex)
            {
                return ClassifyHttpError<T>(ex);
            }
            catch (Exception ex)
            {
                logger.Debug(ex, $"[{Config.Label}] POST {relativePath} failed");
                return HostResult<T>.Unreachable(ex.Message);
            }
        }

        private async Task<HostResult<T>> ParseJsonResponseAsync<T>(HttpResponseMessage resp)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized) return HostResult<T>.AuthFailed();
            if (!resp.IsSuccessStatusCode) return HostResult<T>.ServerError((int)resp.StatusCode, resp.ReasonPhrase);

            var len = resp.Content.Headers.ContentLength ?? 0;
            if (len > MaxResponseBytes) return HostResult<T>.ServerError(0, "response too large");

            using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var limited = new LimitedStream(stream, MaxResponseBytes))
            using (var reader = new StreamReader(limited, Encoding.UTF8))
            using (var jr = new JsonTextReader(reader) { MaxDepth = MaxJsonDepth })
            {
                try
                {
                    var ser = JsonSerializer.Create(StreamOverrides.JsonSettings);
                    var obj = ser.Deserialize<T>(jr);
                    return HostResult<T>.Ok(obj);
                }
                catch (JsonException ex)
                {
                    logger.Warn($"[{Config.Label}] malformed JSON: {SafeLogging.Redact(ex.Message)}");
                    return HostResult<T>.ServerError(0, "malformed JSON");
                }
            }
        }

        private HostResult<T> ClassifyHttpError<T>(HttpRequestException ex)
        {
            // Cert-pin rejection surfaces as HttpRequestException with an inner
            // WebException / AuthenticationException. The PinningWebRequestHandler
            // already logged which case; we classify here based on the pin we observed.
            if (!string.IsNullOrEmpty(LastObservedCertFingerprintSpkiSha256) &&
                !string.IsNullOrEmpty(Config.CertFingerprintSpkiSha256) &&
                !string.Equals(LastObservedCertFingerprintSpkiSha256, Config.CertFingerprintSpkiSha256, StringComparison.OrdinalIgnoreCase))
            {
                return HostResult<T>.CertMismatch(LastObservedCertFingerprintSpkiSha256);
            }
            if (string.IsNullOrEmpty(Config.CertFingerprintSpkiSha256))
            {
                return HostResult<T>.CertMissing();
            }
            return HostResult<T>.Unreachable(ex.Message);
        }

        /// <summary>Bounded read stream — refuses anything past a hard byte cap.</summary>
        private class LimitedStream : Stream
        {
            private readonly Stream inner;
            private readonly long cap;
            private long read;

            public LimitedStream(Stream inner, long cap) { this.inner = inner; this.cap = cap; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => read; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (read >= cap) throw new IOException("response exceeds cap");
                int max = (int)Math.Min(count, cap - read);
                int n = inner.Read(buffer, offset, max);
                read += n;
                return n;
            }
        }
    }
}
