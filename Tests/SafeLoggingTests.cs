using Microsoft.VisualStudio.TestTools.UnitTesting;
using SunshineLibrary.Services;
using System.Net.Http;

namespace SunshineLibrary.Tests
{
    [TestClass]
    public class SafeLoggingTests
    {
        [TestMethod]
        public void Redact_BasicAuthorizationHeader()
        {
            var input = "GET / -> Authorization: Basic dXNlcjpoOG50ZXIy";
            var output = SafeLogging.Redact(input);

            StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("dXNlcjpoOG50ZXIy"));
            StringAssert.Contains(output, "<redacted>");
        }

        [TestMethod]
        public void Redact_BearerToken()
        {
            var input = "Bearer eyJhbGciOiJIUzI1NiJ9.abc.def";
            var output = SafeLogging.Redact(input);

            StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("eyJhbGciOiJIUzI1NiJ9"));
            StringAssert.Contains(output, "<redacted>");
        }

        [TestMethod]
        public void Redact_CsrfTokenHeader()
        {
            var input = "X-CSRF-Token: abc123def456";
            var output = SafeLogging.Redact(input);

            StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("abc123def456"));
        }

        [TestMethod]
        public void Redact_CookieHeader()
        {
            var input = "Set-Cookie: session=supersecret; Path=/";
            var output = SafeLogging.Redact(input);

            StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("supersecret"));
        }

        [TestMethod]
        public void Redact_BenignString_Unchanged()
        {
            var input = "GET /api/apps -> 200 (42 apps returned)";
            Assert.AreEqual(input, SafeLogging.Redact(input));
        }

        [TestMethod]
        public void Redact_NullSafe()
        {
            Assert.IsNull(SafeLogging.Redact(null));
            Assert.AreEqual("", SafeLogging.Redact(""));
        }

        [TestMethod]
        public void DescribeRequest_StripsQueryString()
        {
            using (var msg = new HttpRequestMessage(HttpMethod.Get, "https://host:47990/api/apps?token=secret"))
            {
                var desc = SafeLogging.DescribeRequest(msg);
                StringAssert.DoesNotMatch(desc, new System.Text.RegularExpressions.Regex("secret"));
                StringAssert.DoesNotMatch(desc, new System.Text.RegularExpressions.Regex("\\?"));
                StringAssert.Contains(desc, "/api/apps");
            }
        }

        [TestMethod]
        public void DescribeRequest_NoHeaderValues()
        {
            using (var msg = new HttpRequestMessage(HttpMethod.Post, "https://host/x"))
            {
                msg.Headers.TryAddWithoutValidation("Authorization", "Basic supersecret");
                var desc = SafeLogging.DescribeRequest(msg);

                StringAssert.DoesNotMatch(desc, new System.Text.RegularExpressions.Regex("supersecret"));
                StringAssert.Contains(desc, "headers=");
            }
        }

        [TestMethod]
        public void DescribeRequest_NullRequest_DoesNotThrow()
        {
            var desc = SafeLogging.DescribeRequest(null);
            Assert.IsNotNull(desc);
        }
    }
}
