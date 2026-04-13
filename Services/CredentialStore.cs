using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace SunshineLibrary.Services
{
    /// <summary>
    /// Per-host encrypted credential store. Matches the Playnite house pattern
    /// (Epic/Xbox/Amazon use <c>Playnite.Common.Encryption.EncryptToFile</c> over DPAPI
    /// with the current user's SID as entropy — see PLAN §13b).
    ///
    /// NOTE: <c>Playnite.Common</c> is not shipped as a standalone assembly next to the
    /// Playnite SDK on disk (verified against this machine's install), so this class
    /// calls <see cref="ProtectedData.Protect"/> directly with the same semantics:
    /// CurrentUser scope, <c>SID:hostId</c> entropy. API surface is identical to what
    /// callers would get from the Encryption helper, so swapping in the real Playnite
    /// helper later is a mechanical change.
    /// </summary>
    public class CredentialStore
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly string credsDir;

        public CredentialStore(string extensionDataDir)
        {
            if (extensionDataDir == null) throw new ArgumentNullException(nameof(extensionDataDir));
            credsDir = Path.Combine(extensionDataDir, "creds");
        }

        public void Save(Guid hostId, string user, string password)
        {
            Directory.CreateDirectory(credsDir);
            var payload = new CredBlob { User = user ?? string.Empty, Password = password ?? string.Empty };
            var json = JsonConvert.SerializeObject(payload);
            var plaintext = Encoding.UTF8.GetBytes(json);
            var entropy = GetEntropy(hostId);
            var ciphertext = ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(PathFor(hostId), ciphertext);
        }

        public (string User, string Password)? TryLoad(Guid hostId)
        {
            var path = PathFor(hostId);
            if (!File.Exists(path)) return null;

            try
            {
                var ciphertext = File.ReadAllBytes(path);
                var entropy = GetEntropy(hostId);
                var plaintext = ProtectedData.Unprotect(ciphertext, entropy, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(plaintext);
                var blob = JsonConvert.DeserializeObject<CredBlob>(json);
                if (blob == null) return null;
                return (blob.User ?? string.Empty, blob.Password ?? string.Empty);
            }
            catch (CryptographicException ex)
            {
                // Roamed settings, different user, or different machine — blob is
                // unreadable. Clear it so the Add-Host / Edit-Host flow re-prompts.
                logger.Debug(ex, $"CredentialStore: failed to decrypt {hostId}, discarding");
                TryDeleteFile(path);
                return null;
            }
            catch (JsonException ex)
            {
                logger.Debug(ex, $"CredentialStore: corrupt JSON for {hostId}, discarding");
                TryDeleteFile(path);
                return null;
            }
        }

        public void Delete(Guid hostId)
        {
            TryDeleteFile(PathFor(hostId));
        }

        public void DeleteAll()
        {
            if (!Directory.Exists(credsDir)) return;
            foreach (var f in Directory.EnumerateFiles(credsDir, "*.dat"))
            {
                TryDeleteFile(f);
            }
        }

        private string PathFor(Guid hostId) => Path.Combine(credsDir, hostId.ToString() + ".dat");

        private static byte[] GetEntropy(Guid hostId)
        {
            // SID matches Epic/Xbox/Amazon; hostId suffix isolates per-host blobs so a
            // leaked file for one host can't be silently decrypted against another.
            string sid;
            try
            {
                sid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
            }
            catch
            {
                sid = string.Empty;
            }
            return Encoding.UTF8.GetBytes(sid + ":" + hostId.ToString());
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { logger.Debug(ex, $"CredentialStore: delete failed for {path}"); }
        }

        private class CredBlob
        {
            [JsonProperty("user")]
            public string User { get; set; }

            [JsonProperty("password")]
            public string Password { get; set; }
        }
    }
}
