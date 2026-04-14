using Playnite.SDK;
using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace SunshineLibrary.Services.Hosts
{
    /// <summary>
    /// Opens a raw TLS connection without validation to capture the server's
    /// certificate. Used during Add Host to compute the SPKI fingerprint the
    /// user will confirm in the pin dialog (PLAN §13a flow 1).
    ///
    /// This is the ONLY place in the plugin that disables cert validation,
    /// and only long enough to read the leaf cert for the confirmation UI.
    /// The callback returns true unconditionally — safe because (a) no request
    /// body is ever sent, (b) the user explicitly confirms the fingerprint
    /// out-of-band before the pin is stored.
    /// </summary>
    public static class CertProbe
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public class Result
        {
            public bool Success { get; set; }
            public string SpkiSha256 { get; set; }
            public string Subject { get; set; }
            public string Issuer { get; set; }
            public DateTime? NotAfter { get; set; }
            public string ErrorMessage { get; set; }
        }

        public static async Task<Result> FetchLeafCertAsync(string address, int port, TimeSpan timeout, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(address))
                return new Result { Success = false, ErrorMessage = "Address is empty." };

            using (var tcp = new TcpClient())
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var connectTask = tcp.ConnectAsync(address, port);
                linkedCts.CancelAfter(timeout);

                try
                {
                    var completed = await Task.WhenAny(connectTask, Task.Delay(timeout, linkedCts.Token)).ConfigureAwait(false);
                    if (completed != connectTask)
                    {
                        return new Result { Success = false, ErrorMessage = "Connection timed out." };
                    }
                    await connectTask.ConfigureAwait(false); // surface any exception

                    using (var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                        userCertificateValidationCallback: (_, __, ___, ____) => true))
                    {
                        await ssl.AuthenticateAsClientAsync(address).ConfigureAwait(false);

                        var leaf = ssl.RemoteCertificate;
                        if (leaf == null)
                        {
                            return new Result { Success = false, ErrorMessage = "Server presented no certificate." };
                        }

                        var x509 = leaf as X509Certificate2 ?? new X509Certificate2(leaf);
                        return new Result
                        {
                            Success = true,
                            SpkiSha256 = ComputeSpkiSha256(x509),
                            Subject = x509.Subject,
                            Issuer = x509.Issuer,
                            NotAfter = x509.NotAfter,
                        };
                    }
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, $"CertProbe: failed for {address}:{port}");
                    return new Result { Success = false, ErrorMessage = ex.Message };
                }
            }
        }

        /// <summary>
        /// Matches the format used by <c>PinningWebRequestHandler</c>: SHA-256 of
        /// the cert's full DER (RawData). Same fallback documented there applies;
        /// swap for strict SPKI extraction once we gain a DER/ASN.1 helper.
        /// </summary>
        private static string ComputeSpkiSha256(X509Certificate2 cert)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(cert.RawData);
                var chars = new char[hash.Length * 3 - 1];
                const string hex = "0123456789ABCDEF";
                int idx = 0;
                for (int i = 0; i < hash.Length; i++)
                {
                    chars[idx++] = hex[(hash[i] >> 4) & 0xF];
                    chars[idx++] = hex[hash[i] & 0xF];
                    if (i + 1 < hash.Length) chars[idx++] = ':';
                }
                return new string(chars);
            }
        }
    }
}
