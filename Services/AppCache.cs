using Newtonsoft.Json;
using Playnite.SDK;
using SunshineLibrary.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace SunshineLibrary.Services
{
    /// <summary>
    /// Per-host cache of the last-good <see cref="RemoteApp"/> list. Used as an
    /// offline fallback: when a host is unreachable or cert-pin-broken, sync yields
    /// the cached apps tagged offline rather than wiping the library (PLAN §11).
    ///
    /// No HMAC for M2a — files live in the user-local extension data dir, and the
    /// value of tampering with a list of app names is low. M2b may add signing.
    /// </summary>
    public class AppCache
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly string cacheDir;

        public AppCache(string extensionDataDir)
        {
            if (extensionDataDir == null) throw new ArgumentNullException(nameof(extensionDataDir));
            cacheDir = Path.Combine(extensionDataDir, "cache");
        }

        public virtual void Save(Guid hostId, IReadOnlyList<RemoteApp> apps)
        {
            Directory.CreateDirectory(cacheDir);
            var json = JsonConvert.SerializeObject(apps ?? new List<RemoteApp>(), StreamOverrides.JsonSettings);
            File.WriteAllText(PathFor(hostId), json);
        }

        public virtual IReadOnlyList<RemoteApp> TryLoad(Guid hostId)
        {
            var path = PathFor(hostId);
            if (!File.Exists(path)) return null;

            try
            {
                var json = File.ReadAllText(path);
                var list = JsonConvert.DeserializeObject<List<RemoteApp>>(json, StreamOverrides.JsonSettings);
                return list;
            }
            catch (JsonException ex)
            {
                logger.Debug(ex, $"AppCache: corrupt cache for {hostId}, discarding");
                TryDeleteFile(path);
                return null;
            }
            catch (IOException ex)
            {
                logger.Debug(ex, $"AppCache: read failed for {hostId}");
                return null;
            }
        }

        private string PathFor(Guid hostId) => Path.Combine(cacheDir, hostId.ToString() + ".json");

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { logger.Debug(ex, $"AppCache: delete failed for {path}"); }
        }
    }
}
