using Playnite.SDK;
using Playnite.SDK.Models;
using SunshineLibrary.Models;
using SunshineLibrary.Services.Hosts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlayniteGameMetadata = Playnite.SDK.Models.GameMetadata;
using PlayniteMetadata = Playnite.SDK.Models.MetadataNameProperty;
using PlayniteSpec = Playnite.SDK.Models.MetadataSpecProperty;
using MetadataProperty = Playnite.SDK.Models.MetadataProperty;
using MetadataFile = Playnite.SDK.Models.MetadataFile;

namespace SunshineLibrary.Services
{
    /// <summary>
    /// Multi-host fan-out with per-host cache fallback. One offline host surfaces
    /// a status in its HostSyncResult but does not fail the overall sync — cached
    /// apps (marked with an "offline" tag) still yield so the user's library doesn't
    /// vanish when a host is briefly unreachable.
    /// </summary>
    public class SyncService
    {
        private const int ConcurrentHostLimit = 4;
        private const string OfflineTagName = "SunshineLibrary: offline";

        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly Guid pluginId;
        private readonly AppCache appCache;

        public SyncService(Guid pluginId, AppCache appCache)
        {
            this.pluginId = pluginId;
            this.appCache = appCache;
        }

        public class HostSyncResult
        {
            public HostConfig Host { get; set; }
            public HostResult Status { get; set; }
            public List<PlayniteGameMetadata> Games { get; set; } = new List<PlayniteGameMetadata>();
            public bool FromCache { get; set; }
        }

        public class SyncSummary
        {
            public List<HostSyncResult> Results { get; set; } = new List<HostSyncResult>();
            public IEnumerable<PlayniteGameMetadata> AllGames => Results.SelectMany(r => r.Games);
        }

        /// <summary>
        /// Fan out across all hosts in parallel under a semaphore. Per-host errors
        /// are captured in the HostSyncResult and do NOT propagate.
        /// </summary>
        public async Task<SyncSummary> SyncAllAsync(IEnumerable<HostConfig> hosts, CancellationToken ct)
        {
            var summary = new SyncSummary();
            if (hosts == null) return summary;

            var hostList = hosts.Where(h => h != null && h.Enabled).ToList();
            if (hostList.Count == 0) return summary;

            using (var throttle = new SemaphoreSlim(ConcurrentHostLimit))
            {
                var tasks = hostList.Select(async h =>
                {
                    await throttle.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        return await SyncOneAsync(h, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        throttle.Release();
                    }
                }).ToArray();

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                summary.Results.AddRange(results);
            }
            return summary;
        }

        /// <summary>Single-host path. Live-fetches, saves cache on success, yields cache on error.</summary>
        public async Task<HostSyncResult> SyncOneAsync(HostConfig host, CancellationToken ct)
        {
            var result = new HostSyncResult { Host = host };
            if (host == null || !host.Enabled)
            {
                result.Status = HostResult.Ok();
                return result;
            }

            HostClient client = null;
            try
            {
                client = HostClientFactory.Create(host);

                // Probe flavor if we haven't yet — cheap, caches on HostConfig.
                if (host.ServerType == ServerType.Unknown)
                {
                    var probed = await HostClientFactory.ProbeServerTypeAsync(client, ct).ConfigureAwait(false);
                    if (probed != ServerType.Unknown && probed != host.ServerType)
                    {
                        host.ServerType = probed;
                        client.Dispose();
                        client = HostClientFactory.Create(host);
                    }
                }

                var apps = await client.ListAppsAsync(ct).ConfigureAwait(false);
                if (!apps.IsOk)
                {
                    result.Status = apps.AsStatus();
                    TryYieldFromCache(host, result);
                    return result;
                }

                appCache?.Save(host.Id, apps.Value);

                var filtered = PseudoAppFilter.Apply(apps.Value, host).ToList();
                foreach (var app in filtered)
                {
                    var meta = BuildMeta(host, app, fromCache: false);

                    // Inline cover: best-effort, quiet on failure — Playnite falls through to IGDB.
                    var cover = await client.FetchCoverAsync(app, ct).ConfigureAwait(false);
                    if (cover.IsOk && cover.Value != null && cover.Value.Length > 0)
                    {
                        meta.CoverImage = new MetadataFile(
                            $"{host.Label}-{app.Name}.png", cover.Value);
                    }

                    result.Games.Add(meta);
                }

                result.Status = HostResult.Ok();
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Status = HostResult.Cancelled();
                return result;
            }
            catch (Exception ex)
            {
                logger.Warn($"[{host.Label}] sync failed: {SafeLogging.Redact(ex.Message)}");
                result.Status = HostResult.Unreachable(ex.Message);
                TryYieldFromCache(host, result);
                return result;
            }
            finally
            {
                client?.Dispose();
            }
        }

        private void TryYieldFromCache(HostConfig host, HostSyncResult result)
        {
            if (appCache == null) return;
            var cached = appCache.TryLoad(host.Id);
            if (cached == null || cached.Count == 0) return;

            var filtered = PseudoAppFilter.Apply(cached, host);
            foreach (var app in filtered)
            {
                result.Games.Add(BuildMeta(host, app, fromCache: true));
            }
            result.FromCache = true;
            logger.Info($"[{host.Label}] yielded {result.Games.Count} games from cache (live fetch failed).");
        }

        private PlayniteGameMetadata BuildMeta(HostConfig host, RemoteApp app, bool fromCache)
        {
            var sourcePrefix = host.ServerType == ServerType.Vibepollo ? "Vibepollo" : "Sunshine";
            var source = new PlayniteMetadata($"{sourcePrefix}: {host.Label}");
            var platform = new PlayniteSpec("pc_windows");
            var feature = new PlayniteMetadata("Game Streaming");

            var tags = new HashSet<MetadataProperty>();
            if (!string.IsNullOrEmpty(app.PluginName))
                tags.Add(new PlayniteMetadata(app.PluginName));
            if (app.Categories != null)
                foreach (var cat in app.Categories)
                    if (!string.IsNullOrWhiteSpace(cat))
                        tags.Add(new PlayniteMetadata(cat));
            if (fromCache)
                tags.Add(new PlayniteMetadata(OfflineTagName));

            var meta = new PlayniteGameMetadata
            {
                GameId = $"{host.Id}:{app.StableId}",
                Name = app.Name,
                IsInstalled = true,
                Source = source,
                Platforms = new HashSet<MetadataProperty> { platform },
                Features = new HashSet<MetadataProperty> { feature },
            };

            if (tags.Count > 0)
                meta.Tags = tags;

            return meta;
        }
    }
}
