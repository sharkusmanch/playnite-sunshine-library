using Newtonsoft.Json;
using Playnite.SDK;
using SunshineLibrary.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SunshineLibrary.Services
{
    /// <summary>
    /// Per-game <see cref="StreamOverrides"/>, keyed by Playnite GameId string
    /// (which for our plugin is "{hostId}:{appStableId}").
    ///
    /// Stored as a single JSON file — volumes are modest (hundreds of games at
    /// most), and this keeps the Add/Edit/Clear path a single atomic write.
    /// Corrupt file → discard and start fresh (PLAN §10).
    /// </summary>
    public class OverrideStore
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly string path;
        private readonly object gate = new object();
        private Dictionary<string, StreamOverrides> map;

        public OverrideStore(string extensionDataDir)
        {
            if (extensionDataDir == null) throw new ArgumentNullException(nameof(extensionDataDir));
            path = Path.Combine(extensionDataDir, "overrides.json");
            map = Load();
        }

        public virtual StreamOverrides TryGet(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return null;
            lock (gate)
            {
                return map.TryGetValue(gameId, out var o) ? o : null;
            }
        }

        public virtual void Set(string gameId, StreamOverrides overrides)
        {
            if (string.IsNullOrEmpty(gameId)) return;
            lock (gate)
            {
                if (overrides == null) map.Remove(gameId);
                else map[gameId] = overrides;
                Save_locked();
            }
        }

        public virtual void Remove(string gameId) => Set(gameId, null);

        public virtual IReadOnlyDictionary<string, StreamOverrides> Snapshot()
        {
            lock (gate)
            {
                return new Dictionary<string, StreamOverrides>(map);
            }
        }

        public virtual void Clear()
        {
            lock (gate)
            {
                map.Clear();
                Save_locked();
            }
        }

        // --- IO -----------------------------------------------------------------

        private Dictionary<string, StreamOverrides> Load()
        {
            if (!File.Exists(path)) return new Dictionary<string, StreamOverrides>(StringComparer.Ordinal);
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, StreamOverrides>>(
                    json, StreamOverrides.JsonSettings);
                return loaded ?? new Dictionary<string, StreamOverrides>(StringComparer.Ordinal);
            }
            catch (JsonException ex)
            {
                logger.Debug(ex, $"OverrideStore: corrupt file at {path}, discarding");
                TryDelete();
                return new Dictionary<string, StreamOverrides>(StringComparer.Ordinal);
            }
            catch (IOException ex)
            {
                logger.Debug(ex, $"OverrideStore: read failed");
                return new Dictionary<string, StreamOverrides>(StringComparer.Ordinal);
            }
        }

        private void Save_locked()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var json = JsonConvert.SerializeObject(map, Formatting.Indented, StreamOverrides.JsonSettings);
                // Atomic-ish: write to temp, replace. On net462 File.Replace is fine.
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (IOException ex)
            {
                logger.Warn($"OverrideStore: save failed: {SafeLogging.Redact(ex.Message)}");
            }
        }

        private void TryDelete()
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { logger.Debug(ex, $"OverrideStore: delete failed for {path}"); }
        }
    }
}
