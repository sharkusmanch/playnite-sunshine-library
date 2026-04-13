using Playnite.SDK;
using SunshineLibrary.Models;
using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SunshineLibrary.Services.Hosts
{
    /// <summary>
    /// Instance-level SPKI SHA-256 cert pinning. Never touches the process-global
    /// ServicePointManager callback (which would affect every HTTP call in Playnite).
    ///
    /// Name mismatches are accepted when the pin matches — the pin is the trust
    /// boundary, and Tailscale/MagicDNS/IP-literal access commonly hits
    /// RemoteCertificateNameMismatch even with the correct cert.
    /// </summary>
    internal class PinningWebRequestHandler : WebRequestHandler
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly HostConfig host;

        public string LastObservedSpkiSha256 { get; private set; }

        public PinningWebRequestHandler(HostConfig host)
        {
            this.host = host;
            // Instance callback — DO NOT set ServicePointManager.ServerCertificateValidationCallback.
            ServerCertificateValidationCallback = ValidateCertificate;
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
        }

        private bool ValidateCertificate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
        {
            if (cert == null) return false;
            var x509 = cert as X509Certificate2 ?? new X509Certificate2(cert);

            var fingerprint = ComputeSpkiSha256(x509);
            LastObservedSpkiSha256 = fingerprint;

            var pinned = host.CertFingerprintSpkiSha256;

            if (string.IsNullOrWhiteSpace(pinned))
            {
                // M1: no pin stored — refuse and surface the observed fingerprint in the log
                // so the developer (or M2's Add-Host flow) can capture it. Never trust-on-first-use silently.
                logger.Warn($"[{host.Label}] No cert pin configured. Observed SPKI SHA-256 = {fingerprint}. Set this in the host config to connect.");
                return false;
            }

            if (FixedTimeEquals(fingerprint, pinned))
            {
                // Accept regardless of name mismatch — pin is the trust boundary.
                return true;
            }

            logger.Warn($"[{host.Label}] Cert pin MISMATCH. Expected {pinned}, observed {fingerprint}. Refusing connection.");
            return false;
        }

        private static string ComputeSpkiSha256(X509Certificate2 cert)
        {
            // SubjectPublicKeyInfo lives in PublicKey.EncodedKeyValue + PublicKey.EncodedParameters,
            // but we want the full SPKI DER. cert.GetPublicKey() returns only the key bytes.
            // Use RawData as a stable fallback when SPKI extraction isn't available on net462;
            // this still catches "attacker generates new keypair" because RawData changes too.
            // TODO(M2): switch to proper SPKI extraction via BouncyCastle or manual ASN.1 parse.
            var raw = cert.RawData;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(raw);
                return FormatColonHex(hash);
            }
        }

        private static string FormatColonHex(byte[] bytes)
        {
            var chars = new char[bytes.Length * 3 - 1];
            const string hex = "0123456789ABCDEF";
            int idx = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[idx++] = hex[(bytes[i] >> 4) & 0xF];
                chars[idx++] = hex[bytes[i] & 0xF];
                if (i + 1 < bytes.Length) chars[idx++] = ':';
            }
            return new string(chars);
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
