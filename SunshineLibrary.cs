using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using SunshineLibrary.Models;
using SunshineLibrary.Services;
using SunshineLibrary.Services.Clients;
using SunshineLibrary.Services.Hosts;
using SunshineLibrary.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace SunshineLibrary
{
    public class SunshineLibrary : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public override Guid Id { get; } = Guid.Parse("496637e1-1607-4016-aa4b-d6f732c21210");
        public override string Name => ResourceProvider.GetString("LOC_SunshineLibrary_Name");
        public override string LibraryIcon => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(GetType().Assembly.Location), "icon.png");
        public override LibraryClient Client => libraryClient;

        private readonly StreamClientRegistry clientRegistry = new StreamClientRegistry();
        private readonly SyncService syncService;
        private readonly CredentialStore credentialStore;
        private readonly AppCache appCache;
        private readonly OverrideStore overrideStore;
        private readonly SunshineLibrarySettingsViewModel settingsVm;
        private readonly SunshineLibraryClient libraryClient;

        public SunshineLibrary(IPlayniteAPI api) : base(api)
        {
            Properties = new LibraryPluginProperties
            {
                HasSettings = true,
                CanShutdownClient = false,
            };

            var dataDir = GetPluginUserDataPath();
            credentialStore = new CredentialStore(dataDir);
            appCache = new AppCache(dataDir);
            overrideStore = new OverrideStore(dataDir);
            settingsVm = new SunshineLibrarySettingsViewModel(this, credentialStore);
            libraryClient = new SunshineLibraryClient(settingsVm);
            syncService = new SyncService(Id, appCache);
        }

        public override ISettings GetSettings(bool firstRunSettings) => settingsVm;
        public override UserControl GetSettingsView(bool firstRunSettings) => new SunshineLibrarySettingsView { DataContext = settingsVm };

        /// <summary>Called by the settings VM after EndEdit — gives the plugin a chance to react.</summary>
        public void OnSettingsSaved()
        {
            // Notification preview is surfaced on next sync; no immediate action needed for M2b.
            logger.Info($"Settings saved — {settingsVm.Settings.Hosts?.Count ?? 0} host(s) configured.");
        }

        private IEnumerable<HostConfig> ActiveHosts() =>
            settingsVm.Settings?.Hosts?.Where(h => h != null && h.Enabled) ?? Enumerable.Empty<HostConfig>();

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var hosts = ActiveHosts().ToList();
            var ct = args?.CancelToken ?? CancellationToken.None;

            var summary = hosts.Count == 0
                ? new SyncService.SyncSummary()
                : Task.Run(() => syncService.SyncAllAsync(hosts, ct), ct).GetAwaiter().GetResult();

            foreach (var r in summary.Results)
            {
                if (r.Status != null && !r.Status.IsOk && !r.FromCache)
                {
                    SurfaceError(r.Host, r.Status);
                }
                else if (r.Status != null && r.Status.IsOk)
                {
                    SurfaceSyncSuccess(r.Host, r.Games.Count);
                }
            }

            ReconcileOrphansByName(summary);
            MarkOrphansUninstalled(summary, hosts);

            return summary.AllGames;
        }

        /// <summary>
        /// Server identity is unstable: Apollo rotates `uuid` when an app is removed
        /// and re-added; Sunshine's `sha256(name|cmd)` changes when the launch cmd
        /// is edited. Without reconciliation, either edit creates a duplicate game
        /// in Playnite and the old playtime/overrides become orphan state.
        ///
        /// Before Playnite processes the yield, walk yielded metadata: for each
        /// GameId that doesn't already exist in the DB, look for an existing game
        /// under our plugin scoped to the same host whose Name matches (case-
        /// insensitive, exact). If found, rewrite that game's GameId to the new
        /// one and migrate its OverrideStore entry. Playnite's diff then matches
        /// the existing row and preserves everything.
        ///
        /// Scoped per-host so a rename on host A never touches host B. Known gap:
        /// if the user renames in Playnite AND the app is re-added server-side,
        /// the name no longer matches and we create a duplicate. Acceptable for v1.
        /// </summary>
        private void ReconcileOrphansByName(SyncService.SyncSummary summary)
        {
            if (summary?.Results == null) return;

            // Pre-index existing games by (hostId, lowercased name). Allocating once
            // is cheap; walking DB per yielded app would be O(N·M).
            var byHostName = new Dictionary<string, Game>(StringComparer.Ordinal);
            var existingGameIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in PlayniteApi.Database.Games)
            {
                if (g.PluginId != Id || string.IsNullOrEmpty(g.GameId)) continue;
                existingGameIds.Add(g.GameId);

                var parts = g.GameId.Split(new[] { ':' }, 2);
                if (parts.Length != 2 || string.IsNullOrEmpty(g.Name)) continue;
                var key = parts[0] + ":" + g.Name.ToLowerInvariant();
                // First one wins — duplicates under the same name on the same host are
                // already ambiguous and we can't auto-resolve them.
                if (!byHostName.ContainsKey(key)) byHostName[key] = g;
            }

            var updates = new List<Game>();
            foreach (var meta in summary.AllGames)
            {
                if (string.IsNullOrEmpty(meta.GameId)) continue;
                if (existingGameIds.Contains(meta.GameId)) continue; // primary match wins, skip

                var parts = meta.GameId.Split(new[] { ':' }, 2);
                if (parts.Length != 2 || string.IsNullOrEmpty(meta.Name)) continue;
                var key = parts[0] + ":" + meta.Name.ToLowerInvariant();

                if (!byHostName.TryGetValue(key, out var orphan)) continue;
                if (orphan.GameId == meta.GameId) continue; // shouldn't happen given existingGameIds check

                var oldGameId = orphan.GameId;
                orphan.GameId = meta.GameId;
                orphan.IsInstalled = true;
                updates.Add(orphan);

                // Migrate per-game override so it doesn't get orphaned itself.
                var ov = overrideStore.TryGet(oldGameId);
                if (ov != null)
                {
                    overrideStore.Set(meta.GameId, ov);
                    overrideStore.Remove(oldGameId);
                }

                // Refresh our in-memory indices so a second yielded app with the same
                // name on the same host doesn't re-bind to the same orphan.
                existingGameIds.Remove(oldGameId);
                existingGameIds.Add(meta.GameId);
                byHostName.Remove(key);

                logger.Info($"Reconciled orphan '{meta.Name}' on host {parts[0]}: {oldGameId} -> {meta.GameId}");
            }

            if (updates.Count > 0)
            {
                PlayniteApi.Database.Games.Update(updates);
            }
        }

        /// <summary>
        /// When an app is removed from a Sunshine/Apollo host (or the host itself is
        /// removed from settings), mark the corresponding Playnite games uninstalled.
        /// Preserves playtime, overrides, and cover — the user may want to keep the
        /// history if the app comes back.
        ///
        /// Scoping rules to avoid thrashing on transient failures:
        ///   - Only prune against hosts that synced LIVE (not cache-fallback).
        ///     A host that failed to reach its admin API might just be offline;
        ///     uninstalling its games would churn the library on every blip.
        ///   - Host removed from settings entirely → prune its games (host.Id no
        ///     longer in the active set, and we're not in cache-fallback for a
        ///     non-existent host).
        /// </summary>
        private void MarkOrphansUninstalled(SyncService.SyncSummary summary, IReadOnlyList<HostConfig> activeHosts)
        {
            // Host IDs that definitely synced live — safe to compare against.
            var liveHostIds = new HashSet<string>(
                summary.Results
                    .Where(r => r.Status != null && r.Status.IsOk && !r.FromCache)
                    .Select(r => r.Host.Id.ToString()),
                StringComparer.Ordinal);

            // Host IDs the user still has in settings (any sync state). Anything NOT here was
            // explicitly removed from the plugin's configuration → its games are orphans too.
            var configuredHostIds = new HashSet<string>(
                activeHosts.Select(h => h.Id.ToString()),
                StringComparer.Ordinal);

            // Every GameId that sync produced this pass — either live or from cache.
            // Games in this set are definitely still present on their host.
            var yieldedIds = new HashSet<string>(
                summary.AllGames.Select(g => g.GameId),
                StringComparer.Ordinal);

            var ourGames = PlayniteApi.Database.Games
                .Where(g => g.PluginId == Id && !string.IsNullOrEmpty(g.GameId))
                .ToList();

            var updates = new List<Game>();          // games to mark uninstalled this pass
            var confirmedOrphans = new List<Game>(); // all confirmed orphans regardless of IsInstalled
            foreach (var g in ourGames)
            {
                var parts = g.GameId.Split(new[] { ':' }, 2);
                if (parts.Length != 2) continue;
                var hostId = parts[0];

                bool orphan;
                if (!configuredHostIds.Contains(hostId))
                {
                    // Host removed from settings — definitively orphan.
                    orphan = true;
                }
                else if (liveHostIds.Contains(hostId))
                {
                    // Host synced live and game wasn't in the yield → truly removed upstream.
                    orphan = !yieldedIds.Contains(g.GameId);
                }
                else
                {
                    // Host exists in settings but didn't sync live (offline, auth-broken, cache).
                    // Leave alone — don't thrash on transient failures.
                    orphan = false;
                }

                if (!orphan) continue;

                confirmedOrphans.Add(g);
                if (g.IsInstalled)
                {
                    g.IsInstalled = false;
                    updates.Add(g);
                }
            }

            if (updates.Count > 0)
            {
                PlayniteApi.Database.Games.Update(updates);
                logger.Info($"Marked {updates.Count} game(s) uninstalled (removed from host or host removed from settings).");
            }

            // Opt-in deletion pass over ALL confirmed orphans — not just newly-marked ones —
            // so that enabling the setting also cleans up orphans accumulated from prior syncs.
            // Per-host AutoRemoveOrphanedGames takes precedence over the global setting.
            // Games whose host was removed from settings fall back to the global setting.
            if (confirmedOrphans.Count > 0)
            {
                var hostMap = activeHosts.ToDictionary(h => h.Id.ToString(), StringComparer.Ordinal);
                var globalDelete = settingsVm?.Settings?.AutoRemoveOrphanedGames ?? false;
                var toDelete = confirmedOrphans
                    .Where(g =>
                    {
                        var parts = g.GameId.Split(new[] { ':' }, 2);
                        if (parts.Length != 2) return globalDelete;
                        var hid = parts[0];
                        return hostMap.TryGetValue(hid, out var h) && h.AutoRemoveOrphanedGames.HasValue
                            ? h.AutoRemoveOrphanedGames.Value
                            : globalDelete;
                    })
                    .ToList();
                if (toDelete.Count > 0)
                    DeleteOrphanGames(toDelete);
            }
        }

        private void DeleteOrphanGames(IReadOnlyList<Game> games)
        {
            if (games == null || games.Count == 0) return;
            try
            {
                foreach (var g in games)
                {
                    if (!string.IsNullOrEmpty(g.GameId)) overrideStore.Remove(g.GameId);
                }
                PlayniteApi.Database.Games.Remove(games.ToList());
                logger.Info($"Auto-removed {games.Count} orphan game(s) from library (AutoRemoveOrphanedGames enabled).");
            }
            catch (Exception ex)
            {
                logger.Warn($"Failed to auto-remove orphan games: {SafeLogging.Redact(ex.Message)}");
            }
        }

        public override IEnumerable<PlayController> GetPlayActions(GetPlayActionsArgs args)
        {
            if (args?.Game == null || args.Game.PluginId != Id) yield break;

            var host = ResolveHostFromGame(args.Game);
            if (host == null) yield break;

            var appStableId = ParseAppId(args.Game.GameId);
            if (appStableId == null) yield break;

            var clientSettings = settingsVm.Settings?.Client ?? new ClientSettings();
            var client = clientRegistry.Resolve(clientSettings);
            var availability = client.ProbeAvailability(clientSettings);
            if (!availability.Installed)
            {
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "sunshine-client-missing",
                    ResourceProvider.GetString("LOC_SunshineLibrary_Error_ClientNotInstalled"),
                    NotificationType.Error));
                yield break;
            }

            // Use the app name from the cache (the name Sunshine knows), not args.Game.Name,
            // so that games renamed in Playnite still match on the host side.
            var cachedApps = appCache.TryLoad(host.Id);
            var cachedApp = cachedApps?.FirstOrDefault(a => a.StableId == appStableId);
            var appName = cachedApp?.Name ?? args.Game.Name;
            if (cachedApp == null)
                logger.Warn($"[{host.Label}] App '{args.Game.Name}' not found in cache — using Playnite name as fallback");

            var remoteApp = new RemoteApp { Name = appName, StableId = appStableId };
            var display = DisplayProbe.Detect();

            var perGame = overrideStore.TryGet(args.Game.GameId);
            var merged = StreamOverrides.BuiltinDefault
                .MergedWith(settingsVm.Settings?.GlobalOverrides)
                .MergedWith(host.Defaults)
                .MergedWith(perGame);

            logger.Debug($"[{host.Label}] Override layers — global:{settingsVm.Settings?.GlobalOverrides != null} " +
                $"hostDefaults:{host.Defaults != null} perGame:{perGame != null}");
            logger.Debug($"[{host.Label}] Merged overrides — " +
                $"res:{merged.ResolutionMode}/{merged.ResolutionStatic} " +
                $"fps:{merged.FpsMode}/{merged.FpsStatic} " +
                $"hdr:{merged.Hdr} " +
                $"bitrate:{merged.BitrateKbps?.ToString() ?? "auto"} " +
                $"codec:{merged.VideoCodec ?? "inherit"} " +
                $"yuv444:{merged.Yuv444?.ToString() ?? "inherit"} " +
                $"display:{display.Width}x{display.Height}@{display.RefreshHz}Hz hdr={display.HdrEnabled} known={display.IsKnown}");

            // Advisory: surface sanity warnings (HDR+H.264, bitrate/fps range) as toasts.
            // Non-blocking — the launch proceeds with whatever the user configured.
            foreach (var w in PreLaunchValidator.Inspect(merged, display))
            {
                var msg = ResourceProvider.GetString(w.MessageKey);
                if (w.FormatArgs != null && w.FormatArgs.Length > 0)
                {
                    try
                    {
                        msg = string.Format(msg, w.FormatArgs);
                    }
                    catch (Exception ex)
                    {
                        logger.Debug(ex, $"SunshineLibrary: string.Format failed for key {w.MessageKey}, using raw string");
                    }
                }
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    $"sunshine-prelaunch-{w.MessageKey}",
                    msg,
                    NotificationType.Info));
            }

            var spec = client.BuildLaunch(host, remoteApp, merged, display, clientSettings);
            logger.Debug($"[{host.Label}] Launch: {spec.Executable} {spec.Arguments}");

            yield return new AutomaticPlayController(args.Game)
            {
                Name = ResourceProvider.GetString("LOC_SunshineLibrary_PlayAction_Stream"),
                Type = AutomaticPlayActionType.File,
                Path = spec.Executable,
                Arguments = spec.Arguments,
                WorkingDir = spec.WorkingDirectory,
                TrackingMode = spec.TrackingMode,
            };
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            if (args?.Game == null || args.Game.PluginId != Id) return;
            var host = ResolveHostFromGame(args.Game);
            if (host == null) return;

            Task.Run(async () =>
            {
                HostClient client = null;
                try
                {
                    client = HostClientFactory.Create(host);
                    var r = await client.CloseCurrentAppAsync(CancellationToken.None).ConfigureAwait(false);
                    if (!r.IsOk) logger.Debug($"[{host.Label}] /api/apps/close returned {r.Kind}");
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, $"[{host.Label}] close-on-stop failed");
                }
                finally
                {
                    client?.Dispose();
                }
            });
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var ourGames = args?.Games?.Where(g => g != null && g.PluginId == Id).ToList();
            if (ourGames == null || ourGames.Count == 0) yield break;

            var section = ResourceProvider.GetString("LOC_SunshineLibrary_MenuSection");

            if (ourGames.Count == 1)
            {
                var game = ourGames[0];
                yield return new GameMenuItem
                {
                    MenuSection = section,
                    Description = ResourceProvider.GetString("LOC_SunshineLibrary_Menu_StreamingSettings"),
                    Action = _ => OpenPerGameOverrideDialog(game),
                };
                yield return new GameMenuItem
                {
                    MenuSection = section,
                    Description = ResourceProvider.GetString("LOC_SunshineLibrary_Menu_ViewEffectiveSettings"),
                    Action = _ => OpenEffectiveSettingsDialog(game),
                };
            }
            else
            {
                yield return new GameMenuItem
                {
                    MenuSection = section,
                    Description = string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_Menu_BulkEdit"), ourGames.Count),
                    Action = _ => OpenBulkOverrideDialog(ourGames),
                };
            }

            yield return new GameMenuItem
            {
                MenuSection = section,
                Description = ourGames.Count == 1
                    ? ResourceProvider.GetString("LOC_SunshineLibrary_Menu_ClearOverrides")
                    : string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_Menu_ClearOverridesSelection"), ourGames.Count),
                Action = _ => ClearOverridesForGames(ourGames),
            };
        }

        private void OpenPerGameOverrideDialog(Game game)
        {
            var host = ResolveHostFromGame(game);
            var fallback = StreamOverrides.BuiltinDefault
                .MergedWith(settingsVm.Settings?.GlobalOverrides)
                .MergedWith(host?.Defaults);
            var current = overrideStore.TryGet(game.GameId);

            // Look up the remote app name from cache so the preview shows the host-side name.
            RemoteApp remoteApp = null;
            if (host != null)
            {
                var appStableId = ParseAppId(game.GameId);
                var cachedApps = appStableId != null ? appCache.TryLoad(host.Id) : null;
                var cachedApp = cachedApps?.FirstOrDefault(a => a.StableId == appStableId);
                remoteApp = new RemoteApp { Name = cachedApp?.Name ?? game.Name, StableId = appStableId };
            }

            var dlg = new GameOverridesWindow(PlayniteApi, game.Name, current, fallback, host, remoteApp);
            if (!dlg.ShowDialog(System.Windows.Application.Current?.MainWindow)) return;

            if (dlg.CleanClear)
            {
                overrideStore.Remove(game.GameId);
            }
            else
            {
                overrideStore.Set(game.GameId, dlg.Result);
            }
        }

        private void OpenEffectiveSettingsDialog(Game game)
        {
            var host = ResolveHostFromGame(game);
            if (host == null)
            {
                System.Windows.MessageBox.Show(
                    ResourceProvider.GetString("LOC_SunshineLibrary_EffectiveSettings_HostGone"),
                    ResourceProvider.GetString("LOC_SunshineLibrary_Name"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            var appStableId = ParseAppId(game.GameId);
            if (appStableId == null) return;

            var display = DisplayProbe.Detect();
            var global = settingsVm.Settings?.GlobalOverrides;
            var hostDefs = host.Defaults;
            var perGame = overrideStore.TryGet(game.GameId);
            var merged = StreamOverrides.BuiltinDefault
                .MergedWith(global)
                .MergedWith(hostDefs)
                .MergedWith(perGame);

            var cachedApps = appCache.TryLoad(host.Id);
            var cachedApp = cachedApps?.FirstOrDefault(a => a.StableId == appStableId);
            var remoteApp = new RemoteApp { Name = cachedApp?.Name ?? game.Name, StableId = appStableId };

            var args = MoonlightCompatibleClient.ComposeArgs(host, remoteApp, merged, display);
            var cmdLine = PasteArguments.Build(args);

            var provenance = EffectiveSettingsHelper.BuildProvenanceList(
                StreamOverrides.BuiltinDefault, global, hostDefs, perGame, merged, display);

            var dlg = new EffectiveSettingsWindow(
                PlayniteApi, game.Name, host.Label, provenance, cmdLine, display.IsKnown);
            dlg.ShowDialog(System.Windows.Application.Current?.MainWindow);
        }

        private void OpenBulkOverrideDialog(IReadOnlyList<Game> games)
        {
            var dlg = new BulkOverridesWindow(PlayniteApi, games.Count);
            if (!dlg.ShowDialog(System.Windows.Application.Current?.MainWindow) || dlg.Result == null) return;

            foreach (var g in games)
            {
                var existing = overrideStore.TryGet(g.GameId);
                var updated = dlg.Result.ApplyTo(existing);
                overrideStore.Set(g.GameId, updated);
            }
        }

        private void ClearOverridesForGames(IReadOnlyList<Game> games)
        {
            if (games.Count > 1)
            {
                var confirm = System.Windows.MessageBox.Show(
                    string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_Menu_ClearOverridesConfirm"), games.Count),
                    ResourceProvider.GetString("LOC_SunshineLibrary_Name"),
                    System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                if (confirm != System.Windows.MessageBoxResult.Yes) return;
            }
            foreach (var g in games) overrideStore.Remove(g.GameId);
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            var section = ResourceProvider.GetString("LOC_SunshineLibrary_MenuSection");

            yield return new MainMenuItem
            {
                MenuSection = $"@{section}",
                Description = ResourceProvider.GetString("LOC_SunshineLibrary_Menu_ResyncAll"),
                Action = _ => RunManualResync(),
            };

            yield return new MainMenuItem
            {
                MenuSection = $"@{section}",
                Description = ResourceProvider.GetString("LOC_SunshineLibrary_Menu_TestClient"),
                Action = _ => RunClientProbe(),
            };

            yield return new MainMenuItem
            {
                MenuSection = $"@{section}",
                Description = ResourceProvider.GetString("LOC_SunshineLibrary_Menu_HostStatus"),
                Action = _ => RunHostStatusProbe(),
            };

            yield return new MainMenuItem
            {
                MenuSection = $"@{section}",
                Description = ResourceProvider.GetString("LOC_SunshineLibrary_Menu_RemoveOrphanGames"),
                Action = _ => RemoveOrphanGamesNow(),
            };

            yield return new MainMenuItem
            {
                MenuSection = $"@{section}",
                Description = ResourceProvider.GetString("LOC_SunshineLibrary_Menu_CleanOrphans"),
                Action = _ => CleanOrphanOverrides(),
            };

            if (ActiveHosts().Any(h => h.ServerType == ServerType.Vibepollo))
            {
                yield return new MainMenuItem
                {
                    MenuSection = $"@{section}",
                    Description = ResourceProvider.GetString("LOC_SunshineLibrary_Menu_RefreshVibepolloLibrary"),
                    Action = _ => RunVibepolloRefresh(),
                };
            }
        }

        /// <summary>
        /// Manual one-shot orphan-game removal. Independent of AutoRemoveOrphanedGames
        /// — this is the "I just want to clean up right now" path. Requires explicit
        /// confirmation because deletion wipes playtime, overrides, and covers.
        ///
        /// Definition of orphan here matches the sync-time logic: a game is an orphan
        /// if its host was removed from settings OR if its host synced live in the
        /// most recent sync and didn't yield the game. Cache-fallback hosts are never
        /// pruned — we can't tell "removed" from "offline" there.
        /// </summary>
        private void RemoveOrphanGamesNow()
        {
            var hosts = ActiveHosts().ToList();
            var ct = CancellationToken.None;

            // Run a fresh sync to get authoritative yield state.
            var summary = hosts.Count == 0
                ? new SyncService.SyncSummary()
                : Task.Run(() => syncService.SyncAllAsync(hosts, ct), ct).GetAwaiter().GetResult();

            var liveHostIds = new HashSet<string>(
                summary.Results
                    .Where(r => r.Status != null && r.Status.IsOk && !r.FromCache)
                    .Select(r => r.Host.Id.ToString()),
                StringComparer.Ordinal);
            var configuredHostIds = new HashSet<string>(
                hosts.Select(h => h.Id.ToString()),
                StringComparer.Ordinal);
            var yieldedIds = new HashSet<string>(
                summary.AllGames.Select(g => g.GameId),
                StringComparer.Ordinal);

            var orphans = new List<Game>();
            foreach (var g in PlayniteApi.Database.Games)
            {
                if (g.PluginId != Id || string.IsNullOrEmpty(g.GameId)) continue;
                var parts = g.GameId.Split(new[] { ':' }, 2);
                if (parts.Length != 2) continue;
                var hostId = parts[0];

                if (!configuredHostIds.Contains(hostId))
                {
                    orphans.Add(g); // host removed from settings
                }
                else if (liveHostIds.Contains(hostId) && !yieldedIds.Contains(g.GameId))
                {
                    orphans.Add(g); // host synced live and game wasn't yielded
                }
                // else: host didn't sync live (cache or offline) — don't touch
            }

            if (orphans.Count == 0)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    ResourceProvider.GetString("LOC_SunshineLibrary_RemoveOrphanGames_None"),
                    ResourceProvider.GetString("LOC_SunshineLibrary_Name"));
                return;
            }

            var confirm = PlayniteApi.Dialogs.ShowMessage(
                string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_RemoveOrphanGames_Confirm"), orphans.Count),
                ResourceProvider.GetString("LOC_SunshineLibrary_Name"),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            DeleteOrphanGames(orphans);

            PlayniteApi.Notifications.Add(new NotificationMessage(
                "sunshine-orphan-games-removed",
                string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_RemoveOrphanGames_Done"), orphans.Count),
                NotificationType.Info));
        }

        /// <summary>
        /// POSTs /api/playnite/force_sync to each Vibepollo host (telling it to reconcile
        /// its Playnite library), then runs a full resync so newly installed games appear
        /// in Playnite immediately.
        /// </summary>
        private void RunVibepolloRefresh()
        {
            var vibepolloHosts = ActiveHosts().Where(h => h.ServerType == ServerType.Vibepollo).ToList();
            if (vibepolloHosts.Count == 0) return;

            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                // Step 1: force_sync on each Vibepollo host
                progress.ProgressMaxValue = vibepolloHosts.Count + 1;
                foreach (var host in vibepolloHosts)
                {
                    if (progress.CancelToken.IsCancellationRequested) return;
                    progress.CurrentProgressValue++;

                    HostClient client = null;
                    try
                    {
                        client = HostClientFactory.Create(host);
                        var r = client.ForceSyncAsync(progress.CancelToken).GetAwaiter().GetResult();
                        if (!r.IsOk)
                            logger.Debug($"[{host.Label}] force_sync returned {r.Kind}");
                    }
                    catch (Exception ex)
                    {
                        logger.Warn($"[{host.Label}] Vibepollo refresh failed: {SafeLogging.Redact(ex.Message)}");
                    }
                    finally
                    {
                        client?.Dispose();
                    }
                }

                if (progress.CancelToken.IsCancellationRequested) return;

                // Step 2: pull the updated app lists into Playnite
                progress.CurrentProgressValue++;
                var allHosts = ActiveHosts().ToList();
                var summary = syncService.SyncAllAsync(allHosts, progress.CancelToken).GetAwaiter().GetResult();
                ReconcileOrphansByName(summary);
                MarkOrphansUninstalled(summary, allHosts);

                using (PlayniteApi.Database.BufferedUpdate())
                {
                    foreach (var meta in summary.AllGames)
                        PlayniteApi.Database.ImportGame(meta, this);
                }

                foreach (var r in summary.Results)
                {
                    if (r.Status != null && !r.Status.IsOk && !r.FromCache)
                        SurfaceError(r.Host, r.Status);
                }
            }, new GlobalProgressOptions(ResourceProvider.GetString("LOC_SunshineLibrary_Menu_RefreshVibepolloLibrary"), true));
        }

        private void RunHostStatusProbe()
        {
            var hosts = ActiveHosts().ToList();
            if (hosts.Count == 0)
            {
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "sunshine-host-status",
                    ResourceProvider.GetString("LOC_SunshineLibrary_HostStatus_NoHosts"),
                    NotificationType.Info));
                return;
            }

            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.ProgressMaxValue = hosts.Count;
                var probe = new Services.Hosts.TestConnectionService();
                var lines = new System.Text.StringBuilder();
                foreach (var h in hosts)
                {
                    if (progress.CancelToken.IsCancellationRequested) break;
                    progress.CurrentProgressValue++;

                    try
                    {
                        var outcome = probe.RunAsync(h, null, progress.CancelToken).GetAwaiter().GetResult();
                        var status = outcome.Success
                            ? string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_HostStatus_Ok"), outcome.DetectedServerType, outcome.AppCount)
                            : string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_HostStatus_Fail"), outcome.Steps.Count > 0 ? outcome.Steps[outcome.Steps.Count - 1].Step.ToString() : "unknown");
                        lines.AppendLine($"{h.Label}: {status}");
                    }
                    catch (Exception ex)
                    {
                        lines.AppendLine($"{h.Label}: {ex.Message}");
                    }
                }
                PlayniteApi.Dialogs.ShowMessage(
                    lines.ToString().TrimEnd(),
                    ResourceProvider.GetString("LOC_SunshineLibrary_Menu_HostStatus"));
            }, new GlobalProgressOptions(ResourceProvider.GetString("LOC_SunshineLibrary_Menu_HostStatus"), true));
        }

        private void CleanOrphanOverrides()
        {
            var liveGameIds = new HashSet<string>(
                PlayniteApi.Database.Games
                    .Where(g => g.PluginId == Id && !string.IsNullOrEmpty(g.GameId))
                    .Select(g => g.GameId),
                StringComparer.Ordinal);

            var snapshot = overrideStore.Snapshot();
            var removed = 0;
            foreach (var kv in snapshot)
            {
                if (!liveGameIds.Contains(kv.Key))
                {
                    overrideStore.Remove(kv.Key);
                    removed++;
                }
            }

            PlayniteApi.Notifications.Add(new NotificationMessage(
                "sunshine-orphan-cleanup",
                string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_CleanOrphans_Done"), removed, snapshot.Count),
                NotificationType.Info));
        }

        private void RunManualResync()
        {
            var hosts = ActiveHosts().ToList();
            if (hosts.Count == 0) return;

            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.ProgressMaxValue = hosts.Count;
                var summary = syncService.SyncAllAsync(hosts, progress.CancelToken).GetAwaiter().GetResult();

                using (PlayniteApi.Database.BufferedUpdate())
                {
                    foreach (var meta in summary.AllGames)
                    {
                        PlayniteApi.Database.ImportGame(meta, this);
                    }
                }

                foreach (var r in summary.Results)
                {
                    if (r.Status != null && !r.Status.IsOk && !r.FromCache)
                    {
                        SurfaceError(r.Host, r.Status);
                    }
                    else if (r.Status != null && r.Status.IsOk)
                    {
                        SurfaceSyncSuccess(r.Host, r.Games.Count);
                    }
                }
            }, new GlobalProgressOptions(ResourceProvider.GetString("LOC_SunshineLibrary_Menu_ResyncAll"), true));
        }

        private void RunClientProbe()
        {
            var client = clientRegistry.Resolve(settingsVm.Settings?.Client ?? new ClientSettings());
            var availability = client.ProbeAvailability(settingsVm.Settings?.Client ?? new ClientSettings());
            if (availability.Installed)
            {
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "sunshine-client-probe",
                    string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_Info_ClientInstalled"), availability.ExecutablePath),
                    NotificationType.Info));
            }
            else
            {
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "sunshine-client-probe",
                    availability.UnavailableReason ?? ResourceProvider.GetString("LOC_SunshineLibrary_Error_ClientNotInstalled"),
                    NotificationType.Error));
            }
        }

        // --- helpers --------------------------------------------------------------

        private HostConfig ResolveHostFromGame(Game game)
        {
            var parts = game.GameId?.Split(new[] { ':' }, 2);
            if (parts == null || parts.Length != 2) return null;
            if (!Guid.TryParse(parts[0], out var hostId)) return null;
            return ActiveHosts().FirstOrDefault(h => h.Id == hostId);
        }

        private static string ParseAppId(string gameId)
        {
            var parts = gameId?.Split(new[] { ':' }, 2);
            return (parts != null && parts.Length == 2) ? parts[1] : null;
        }

        // --- notification dispatch (PLAN §12a) -----------------------------------

        private void SurfaceSyncSuccess(HostConfig host, int count)
        {
            // "Sync complete" — chatty. Respects NotificationMode.
            var mode = settingsVm.Settings?.NotificationMode ?? NotificationMode.Always;
            if (mode == NotificationMode.Never) return;
            // OnUpdateOnly could gate on "count changed since last sync" — M4 polish.
            if (mode != NotificationMode.Always) return;

            var id = $"sunshine-sync-{host.Id}";
            var text = string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_Sync_Success"), host.Label, count);
            PlayniteApi.Notifications.Add(new NotificationMessage(id, text, NotificationType.Info));
        }

        private void SurfaceError(HostConfig host, HostResult status)
        {
            var id = $"sunshine-host-{host.Id}";
            bool isSecurityCritical =
                status.Kind == HostResultKind.AuthFailed ||
                status.Kind == HostResultKind.CertMismatch ||
                status.Kind == HostResultKind.CertMissing;

            var mode = settingsVm.Settings?.NotificationMode ?? NotificationMode.Always;
            // Security- and launch-critical events ALWAYS fire regardless of mode (PLAN §12a).
            if (!isSecurityCritical)
            {
                if (mode == NotificationMode.Never) return;
                // Unreachable/Timeout/ServerError are "chatty" — gate by mode.
            }

            string text;
            switch (status.Kind)
            {
                case HostResultKind.AuthFailed:
                    text = string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_Error_AuthFailed"), host.Label);
                    break;
                case HostResultKind.CertMismatch:
                    text = string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_Error_CertMismatch_Body"), host.Label);
                    break;
                case HostResultKind.CertMissing:
                    text = string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_Error_MissingPin"), host.Label);
                    break;
                case HostResultKind.Timeout:
                    text = string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_Error_Timeout"), host.Label);
                    break;
                case HostResultKind.Unreachable:
                    text = string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_Error_Unreachable"), host.Label);
                    break;
                case HostResultKind.ServerError:
                    text = string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_Error_ServerError"), host.Label, status.StatusCode);
                    break;
                default:
                    return;
            }

            PlayniteApi.Notifications.Add(new NotificationMessage(id, text, NotificationType.Error));
        }
    }
}
