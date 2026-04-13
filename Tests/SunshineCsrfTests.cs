using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SunshineLibrary.Models;
using SunshineLibrary.Services.Hosts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SunshineLibrary.Tests
{
    /// <summary>
    /// Mocking approach: inject a fake <see cref="HttpMessageHandler"/> via the
    /// <see cref="SunshineHostClient(HostConfig, HttpMessageHandler)"/> test-only ctor.
    /// Picked over a virtual SendAsync seam because net462's HttpClient already
    /// accepts an HttpMessageHandler in its public constructor, so no new virtual
    /// surface is needed — the production code path is unchanged.
    /// </summary>
    [TestClass]
    public class SunshineCsrfTests
    {
        private static HostConfig MakeConfig() => new HostConfig
        {
            Id = Guid.NewGuid(),
            Label = "test",
            Address = "127.0.0.1",
            Port = 47990,
            AdminUser = "admin",
            AdminPassword = "hunter2",
            // Pin fingerprint irrelevant — pinning handler is bypassed by the fake handler.
            CertFingerprintSpkiSha256 = "AA:BB:CC",
        };

        // ------------------------------------------------------------------
        // CsrfTokenCache unit tests (cheap, no HTTP).
        // ------------------------------------------------------------------

        [TestMethod]
        public void Cache_InitiallyEmpty()
        {
            var c = new SunshineHostClient.CsrfTokenCache(TimeSpan.FromMinutes(5));
            Assert.IsFalse(c.TryGet(out var v));
            Assert.IsNull(v);
        }

        [TestMethod]
        public void Cache_SetThenGet_ReturnsValue()
        {
            var c = new SunshineHostClient.CsrfTokenCache(TimeSpan.FromMinutes(5));
            c.Set("abc");
            Assert.IsTrue(c.TryGet(out var v));
            Assert.AreEqual("abc", v);
        }

        [TestMethod]
        public void Cache_InvalidateClearsToken()
        {
            var c = new SunshineHostClient.CsrfTokenCache(TimeSpan.FromMinutes(5));
            c.Set("abc");
            c.Invalidate();
            Assert.IsFalse(c.TryGet(out _));
        }

        [TestMethod]
        public void Cache_ExpiresAfterTtl()
        {
            var clock = new MutableClock(DateTime.UtcNow);
            var c = new SunshineHostClient.CsrfTokenCache(TimeSpan.FromMinutes(5), () => clock.Now);
            c.Set("abc");
            Assert.IsTrue(c.TryGet(out _));
            clock.Advance(TimeSpan.FromMinutes(6));
            Assert.IsFalse(c.TryGet(out _));
        }

        // ------------------------------------------------------------------
        // ExtractCsrfToken — JSON field resolution.
        // ------------------------------------------------------------------

        [TestMethod]
        public void Extract_PrefersCsrfTokenSnakeCase()
        {
            var o = JObject.Parse(@"{""csrf_token"":""A"",""token"":""B""}");
            Assert.AreEqual("A", SunshineHostClient.ExtractCsrfToken(o));
        }

        [TestMethod]
        public void Extract_FallsBackToCamelCase()
        {
            var o = JObject.Parse(@"{""csrfToken"":""A""}");
            Assert.AreEqual("A", SunshineHostClient.ExtractCsrfToken(o));
        }

        [TestMethod]
        public void Extract_FuzzyMatchesAnyTokenField()
        {
            var o = JObject.Parse(@"{""wrapped_csrf"":""X""}");
            Assert.AreEqual("X", SunshineHostClient.ExtractCsrfToken(o));
        }

        [TestMethod]
        public void Extract_ReturnsNullWhenAbsent()
        {
            var o = JObject.Parse(@"{""other"":123}");
            Assert.IsNull(SunshineHostClient.ExtractCsrfToken(o));
        }

        // ------------------------------------------------------------------
        // HTTP-level integration via fake handler.
        // ------------------------------------------------------------------

        [TestMethod]
        public async Task CloseApp_AttachesTokenToMutatingRequest_NotToGet()
        {
            var handler = new FakeHandler();
            handler.OnPost("api/csrf-token", req => JsonResponse(@"{""csrf_token"":""TKN""}"));
            handler.OnPost("api/apps/close", req => new HttpResponseMessage(HttpStatusCode.OK));
            handler.OnGet("api/config", req => JsonResponse(@"{}"));

            using (var client = new SunshineHostClient(MakeConfig(), handler))
            {
                var getResult = await client.ProbeConfigAsync(CancellationToken.None);
                Assert.IsTrue(getResult.IsOk);

                var closeResult = await client.CloseCurrentAppAsync(CancellationToken.None);
                Assert.IsTrue(closeResult.IsOk, $"expected Ok, got {closeResult.Kind} code={closeResult.StatusCode} msg={closeResult.Message}");
            }

            // Expect: GET /api/config (no CSRF), POST /api/csrf-token (no CSRF),
            // POST /api/apps/close (with X-CSRF-Token: TKN).
            var get = handler.Requests.Single(r => r.Method == HttpMethod.Get && r.RelativePath == "api/config");
            Assert.IsFalse(get.Headers.ContainsKey("X-CSRF-Token"), "GET must not carry CSRF token");

            var tokenPost = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.RelativePath == "api/csrf-token");
            Assert.IsFalse(tokenPost.Headers.ContainsKey("X-CSRF-Token"), "token-fetch POST must not carry CSRF token");

            var closePost = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.RelativePath == "api/apps/close");
            Assert.IsTrue(closePost.Headers.ContainsKey("X-CSRF-Token"));
            Assert.AreEqual("TKN", closePost.Headers["X-CSRF-Token"]);
        }

        [TestMethod]
        public async Task CloseApp_403RefreshesTokenAndRetriesOnce()
        {
            var handler = new FakeHandler();

            int tokenCalls = 0;
            handler.OnPost("api/csrf-token", req =>
            {
                tokenCalls++;
                return JsonResponse(tokenCalls == 1 ? @"{""csrf_token"":""OLD""}" : @"{""csrf_token"":""NEW""}");
            });

            int closeCalls = 0;
            handler.OnPost("api/apps/close", req =>
            {
                closeCalls++;
                return closeCalls == 1
                    ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            });

            using (var client = new SunshineHostClient(MakeConfig(), handler))
            {
                var r = await client.CloseCurrentAppAsync(CancellationToken.None);
                Assert.IsTrue(r.IsOk, $"expected Ok after retry, got {r.Kind}/{r.StatusCode}");
            }

            Assert.AreEqual(2, tokenCalls, "token should be refreshed after 403");
            Assert.AreEqual(2, closeCalls, "close should be retried exactly once");

            var closes = handler.Requests.Where(x => x.RelativePath == "api/apps/close").ToList();
            Assert.AreEqual("OLD", closes[0].Headers["X-CSRF-Token"]);
            Assert.AreEqual("NEW", closes[1].Headers["X-CSRF-Token"]);
        }

        [TestMethod]
        public async Task CloseApp_TwoConsecutive403sReturnAuthFailed()
        {
            var handler = new FakeHandler();
            handler.OnPost("api/csrf-token", req => JsonResponse(@"{""csrf_token"":""X""}"));

            int closeCalls = 0;
            handler.OnPost("api/apps/close", req =>
            {
                closeCalls++;
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            });

            using (var client = new SunshineHostClient(MakeConfig(), handler))
            {
                var r = await client.CloseCurrentAppAsync(CancellationToken.None);
                Assert.AreEqual(HostResultKind.AuthFailed, r.Kind);
            }
            Assert.AreEqual(2, closeCalls, "should retry exactly once, not more");
        }

        [TestMethod]
        public async Task CloseApp_CancelledTokenShortCircuitsBeforeAnyPost()
        {
            var handler = new FakeHandler();
            handler.OnPost("api/csrf-token", req =>
            {
                Assert.Fail("csrf-token POST should not fire when already cancelled");
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            handler.OnPost("api/apps/close", req =>
            {
                Assert.Fail("close POST should not fire when already cancelled");
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

            using (var cts = new CancellationTokenSource())
            using (var client = new SunshineHostClient(MakeConfig(), handler))
            {
                cts.Cancel();
                var r = await client.CloseCurrentAppAsync(cts.Token);
                Assert.AreEqual(HostResultKind.Cancelled, r.Kind);
            }
            Assert.AreEqual(0, handler.Requests.Count);
        }

        // ------------------------------------------------------------------
        // Test plumbing.
        // ------------------------------------------------------------------

        private static HttpResponseMessage JsonResponse(string body)
        {
            var msg = new HttpResponseMessage(HttpStatusCode.OK);
            msg.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return msg;
        }

        private class MutableClock
        {
            public DateTime Now;
            public MutableClock(DateTime start) { Now = start; }
            public void Advance(TimeSpan t) { Now = Now.Add(t); }
        }

        private class RecordedRequest
        {
            public HttpMethod Method;
            public string RelativePath;
            public Dictionary<string, string> Headers;
        }

        private class FakeHandler : HttpMessageHandler
        {
            private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> getHandlers
                = new Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> postHandlers
                = new Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>>(StringComparer.OrdinalIgnoreCase);

            public readonly List<RecordedRequest> Requests = new List<RecordedRequest>();

            public void OnGet(string path, Func<HttpRequestMessage, HttpResponseMessage> fn) => getHandlers[path] = fn;
            public void OnPost(string path, Func<HttpRequestMessage, HttpResponseMessage> fn) => postHandlers[path] = fn;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();

                // Strip base URL to a relative path. HttpClient rewrites relative
                // RequestUri to absolute before calling the handler, but some net462
                // pathways leave it relative — handle both.
                var uri = request.RequestUri;
                string path = uri.IsAbsoluteUri
                    ? uri.AbsolutePath.TrimStart('/')
                    : uri.OriginalString.TrimStart('/');

                var captured = new RecordedRequest
                {
                    Method = request.Method,
                    RelativePath = path,
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                };
                foreach (var h in request.Headers)
                {
                    captured.Headers[h.Key] = string.Join(",", h.Value);
                }
                Requests.Add(captured);

                Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> set;
                if (request.Method == HttpMethod.Get) set = getHandlers;
                else if (request.Method == HttpMethod.Post) set = postHandlers;
                else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));

                if (set.TryGetValue(path, out var fn))
                {
                    return Task.FromResult(fn(request));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }
    }
}
