using System.Net.Http;
using System.Text.RegularExpressions;

namespace SunshineLibrary.Services
{
    /// <summary>
    /// Call-site helpers to keep credentials and CSRF tokens out of extensions.log.
    /// Matches the house pattern in well-maintained Playnite library plugins
    /// (e.g. HowLongToBeat's LogCookieSummary): summary over content, never log values.
    /// </summary>
    public static class SafeLogging
    {
        // Match the full header value (up to newline or end-of-string), not just the
        // first whitespace-delimited token — `Authorization: Basic {token}` has TWO
        // tokens after the colon, and if we only eat one, the other survives in logs.
        private static readonly Regex AuthHeader = new Regex(
            @"(?i)\b(Authorization|Cookie|Set-Cookie|X-CSRF[-_]?Token|X-XSRF-TOKEN)\s*[:=][^\r\n]*",
            RegexOptions.Compiled);

        private static readonly Regex BearerOrBasic = new Regex(
            @"(?i)(Basic|Bearer)\s+[A-Za-z0-9+/=._\-]+",
            RegexOptions.Compiled);

        public static string Redact(string s)
        {
            if (s == null) return null;
            s = AuthHeader.Replace(s, "$1: <redacted>");
            s = BearerOrBasic.Replace(s, "$1 <redacted>");
            return s;
        }

        /// <summary>Safe one-line request trace — method, scheme+host+path, header count. No query string, no headers, no body.</summary>
        public static string DescribeRequest(HttpRequestMessage r)
        {
            if (r == null) return "(null request)";
            string path;
            if (r.RequestUri == null)
            {
                path = "(no uri)";
            }
            else if (r.RequestUri.IsAbsoluteUri)
            {
                // Strip query string to avoid logging secrets smuggled as query params.
                path = r.RequestUri.GetLeftPart(System.UriPartial.Path);
            }
            else
            {
                // Relative URI — before HttpClient combines with BaseAddress. Log as-is (no query segment to split).
                var s = r.RequestUri.OriginalString ?? string.Empty;
                var q = s.IndexOf('?');
                path = q >= 0 ? s.Substring(0, q) : s;
            }
            int headerCount = 0;
            foreach (var _ in r.Headers) headerCount++;
            return $"{r.Method} {path} (headers={headerCount})";
        }

        public static string DescribeResponse(HttpResponseMessage r)
        {
            if (r == null) return "(null response)";
            long? len = r.Content?.Headers?.ContentLength;
            return $"{(int)r.StatusCode} {r.ReasonPhrase} (bytes={len?.ToString() ?? "?"})";
        }
    }
}
