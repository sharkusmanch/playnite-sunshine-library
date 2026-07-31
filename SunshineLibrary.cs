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
            logger.Info($"Settings saved — {settingsVm.Settings.Hosts?.Count ?? 0} host(s) configured.");
            CleanUpRemovedHostGames();
        }

        /// <summary>
        /// When hosts are removed from settings, immediately mark their games uninstalled
        /// (and delete them if AutoRemoveOrphanedGames is on) rather than waiting for the
        /// next library sync cycle.
        /// </summary>
        private void CleanUpRemovedHostGames()
        {
            var configuredIds = new HashSet<Guid>(
                settingsVm.Settings.Hosts?.Where(h => h != null).Select(h => h.Id)
                ?? Enumerable.Empty<Guid>());

            var globalDelete = settingsVm.Settings?.AutoRemoveOrphanedGames ?? false;

            var toUninstall = new List<Game>();
            var toDelete = new List<Game>();
            foreach (var g in PlayniteApi.Database.Games)
            {
                if (g.PluginId != Id || string.IsNullOrEmpty(g.GameId)) continue;
                var parts = g.GameId.Split(new[] { ':' }, 2);
                if (parts.Length != 2) continue;
                if (!Guid.TryParse(parts[0], out var hostId)) continue;
                if (configuredIds.Contains(hostId)) continue;

                if (globalDelete)
                    toDelete.Add(g);
                else if (g.IsInstalled)
                {
                    g.IsInstalled = false;
                    toUninstall.Add(g);
                }
            }

            if (toUninstall.Count > 0)
            {
                PlayniteApi.Database.Games.Update(toUninstall);
                logger.Info($"Marked {toUninstall.Count} game(s) uninstalled after host removal from settings.");
            }
            if (toDelete.Count > 0)
            {
                DeleteOrphanGames(toDelete);
            }
        }

        /// <summary>Hosts we actually talk to — sync, launch, and status probes.</summary>
        private IEnumerable<HostConfig> ActiveHosts() => HostScope.Active(settingsVm.Settings?.Hosts);

        /// <summary>
        /// Every host still present in settings, enabled or not. Orphan scoping must use
        /// this, never <see cref="ActiveHosts"/>: a disabled host's apps still exist on the
        /// server, so unchecking "Enabled" must not make its games look deleted.
        /// </summary>
        private IEnumerable<HostConfig> ConfiguredHosts() => HostScope.Configured(settingsVm.Settings?.Hosts);

        /// <summary>
        /// Current user-configured platform / tag shape for imported entries. Read
        /// fresh on every sync so a settings change takes effect without a restart.
        /// </summary>
        private LibraryMetadataOptions MetadataOptions() => new LibraryMetadataOptions
        {
            PlatformName = settingsVm.Settings?.LibraryPlatform,
            Tags = settingsVm.Settings?.AdditionalTags,
        };

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var hosts = ActiveHosts().ToList();
            var ct = args?.CancelToken ?? CancellationToken.None;

            var summary = hosts.Count == 0
                ? new SyncService.SyncSummary()
                : Task.Run(() => syncService.SyncAllAsync(hosts, MetadataOptions(), ct), ct).GetAwaiter().GetResult();

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
            MarkOrphansUninstalled(summary);

            return summary.AllGames;
        }

        /// <summary>
        /// Applies <see cref="OrphanResolver.ResolveRebinds"/> to the database: rewrites the
        /// GameId of games whose upstream app ID changed, and migrates their OverrideStore
        /// entries so those don't get orphaned in turn. Playnite's diff then matches the
        /// existing row and preserves playtime, cover and overrides.
        /// </summary>
        private void ReconcileOrphansByName(SyncService.SyncSummary summary)
        {
            if (summary?.Results == null) return;

            var ourGames = PlayniteApi.Database.Games
                .Where(g => g.PluginId == Id && !string.IsNullOrEmpty(g.GameId))
                .ToList();

            var rebinds = OrphanResolver.ResolveRebinds(
                ourGames.Select(g => new GameRef(g.GameId, g.Name)),
                summary.AllGames.Select(m => new GameRef(m.GameId, m.Name)));
            if (rebinds.Count == 0) return;

            var byId = new Dictionary<string, Game>(StringComparer.Ordinal);
            foreach (var g in ourGames)
            {
                if (!byId.ContainsKey(g.GameId)) byId[g.GameId] = g;
            }

            var updates = new List<Game>();
            foreach (var rebind in rebinds)
            {
                Game game;
                if (!byId.TryGetValue(rebind.OldGameId, out game)) continue;

                game.GameId = rebind.NewGameId;
                game.IsInstalled = true;
                updates.Add(game);

                var ov = overrideStore.TryGet(rebind.OldGameId);
                if (ov != null)
                {
                    overrideStore.Set(rebind.NewGameId, ov);
                    overrideStore.Remove(rebind.OldGameId);
                }

                logger.Info($"Reconciled orphan '{game.Name}': {rebind.OldGameId} -> {rebind.NewGameId}");
            }

            if (updates.Count > 0)
            {
                PlayniteApi.Database.Games.Update(updates);
            }
        }

        /// <summary>
        /// Builds this pass's host-state sets from the sync summary and returns the database
        /// rows that <see cref="OrphanResolver.ResolveOrphans"/> confirms are gone upstream.
        ///
        /// Reads the configured-host set itself rather than taking it as a parameter: passing
        /// the enabled-only list here is precisely the bug this replaced, and there is no
        /// caller that legitimately wants a narrower set.
        ///
        /// <paramref name="guardEmptyYield"/> is on for automatic sync, where a host that
        /// returns nothing is more likely misconfigured than genuinely empty. The manual
        /// "Remove orphaned games…" path turns it off — the user is looking at a count and a
        /// confirmation dialog, and that menu item is the documented way to clear a host that
        /// really did have all its apps removed.
        /// </summary>
        private List<Game> FindOrphanGames(SyncService.SyncSummary summary, bool guardEmptyYield)
        {
            var live = summary.Results
                .Where(r => r.Host != null && r.Status != null && r.Status.IsOk && !r.FromCache)
                .ToList();

            var liveHostIds = new HashSet<string>(
                live.Select(r => r.Host.Id.ToString()), StringComparer.Ordinal);

            var emptyYieldHostIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in live.Where(r => r.Games.Count == 0))
            {
                if (guardEmptyYield)
                {
                    emptyYieldHostIds.Add(r.Host.Id.ToString());
                    logger.Warn($"[{r.Host.Label}] synced live but returned no apps — skipping orphan pruning for this host. Use \"Remove orphaned games…\" if the apps really were all removed.");
                    SurfaceEmptyYieldGuard(r.Host);
                }
                else
                {
                    logger.Info($"[{r.Host.Label}] synced live and returned no apps; pruning anyway (manual, confirmed).");
                }
            }

            var configuredHostIds = new HashSet<string>(
                ConfiguredHosts().Select(h => h.Id.ToString()), StringComparer.Ordinal);

            var yieldedIds = new HashSet<string>(
                summary.AllGames.Select(g => g.GameId), StringComparer.Ordinal);

            var ourGames = PlayniteApi.Database.Games
                .Where(g => g.PluginId == Id && !string.IsNullOrEmpty(g.GameId))
                .ToList();

            var orphanIds = OrphanResolver.ResolveOrphans(
                ourGames.Select(g => g.GameId),
                yieldedIds,
                liveHostIds,
                configuredHostIds,
                emptyYieldHostIds);

            return ourGames.Where(g => orphanIds.Contains(g.GameId)).ToList();
        }

        /// <summary>
        /// When an app is removed from a Sunshine/Apollo host (or the host itself is
        /// removed from settings), mark the corresponding Playnite games uninstalled.
        /// Preserves playtime, overrides, and cover — the user may want to keep the
        /// history if the app comes back.
        ///
        /// See <see cref="OrphanResolver.ResolveOrphans"/> for what does and doesn't
        /// count as evidence a game is gone.
        /// </summary>
        private void MarkOrphansUninstalled(SyncService.SyncSummary summary)
        {
            var confirmedOrphans = FindOrphanGames(summary, guardEmptyYield: true);

            var updates = new List<Game>();
            foreach (var g in confirmedOrphans)
            {
                if (!g.IsInstalled) continue;
                g.IsInstalled = false;
                updates.Add(g);
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
                var hostMap = new Dictionary<string, HostConfig>(StringComparer.Ordinal);
                foreach (var h in ConfiguredHosts())
                {
                    hostMap[h.Id.ToString()] = h;
                }

                var globalDelete = settingsVm?.Settings?.AutoRemoveOrphanedGames ?? false;
                var toDelete = confirmedOrphans
                    .Where(g =>
                    {
                        var hid = OrphanResolver.HostIdOf(g.GameId);
                        HostConfig h;
                        return hid != null && hostMap.TryGetValue(hid, out h) && h.AutoRemoveOrphanedGames.HasValue
                            ? h.AutoRemoveOrphanedGames.Value
                            : globalDelete;
                    })
                    .ToList();
                if (toDelete.Count > 0)
                    DeleteOrphanGames(toDelete);
            }
        }

        /// <summary>
        /// Import pass for the manual menu paths, which push metadata into the database
        /// themselves rather than returning it from <see cref="GetGames"/>.
        ///
        /// Playnite's <c>ImportGame(GameMetadata, Guid)</c> does NOT de-duplicate — that
        /// lives only in the <c>ImportGames(LibraryPlugin, …)</c> overload behind the
        /// GetGames path. Calling it unguarded clones the entire library on every click,
        /// so we diff against existing GameIds first.
        ///
        /// A game already present but marked uninstalled is coming back (it's in this
        /// pass's yield), so flip it installed instead of importing a second copy.
        /// </summary>
        private void ImportNewGames(SyncService.SyncSummary summary)
        {
            if (summary == null) return;

            var byId = new Dictionary<string, Game>(StringComparer.Ordinal);
            foreach (var g in PlayniteApi.Database.Games)
            {
                if (g.PluginId != Id || string.IsNullOrEmpty(g.GameId)) continue;
                if (!byId.ContainsKey(g.GameId)) byId[g.GameId] = g;
            }

            var known = new HashSet<string>(byId.Keys, StringComparer.Ordinal);
            var reinstalled = new List<Game>();
            var imported = 0;

            using (PlayniteApi.Database.BufferedUpdate())
            {
                foreach (var meta in summary.AllGames)
                {
                    if (string.IsNullOrEmpty(meta.GameId)) continue;

                    if (!known.Add(meta.GameId))
                    {
                        Game existing;
                        if (byId.TryGetValue(meta.GameId, out existing) && !existing.IsInstalled)
                        {
                            existing.IsInstalled = true;
                            reinstalled.Add(existing);
                        }
                        continue;
                    }

                    PlayniteApi.Database.ImportGame(meta, this);
                    imported++;
                }

                if (reinstalled.Count > 0)
                {
                    PlayniteApi.Database.Games.Update(reinstalled);
                }
            }

            logger.Info($"Resync imported {imported} new game(s); marked {reinstalled.Count} installed again.");
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

            yield return new MainMenuItem
            {
                MenuSection = $"@{section}",
                Description = ResourceProvider.GetString("LOC_SunshineLibrary_Menu_ApplyPlatformTags"),
                Action = _ => ApplyPlatformAndTagsToExisting(),
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
        /// Definition of orphan here is exactly the sync-time one — see
        /// <see cref="OrphanResolver.ResolveOrphans"/>. Disabled, offline and
        /// cache-fallback hosts are never pruned.
        /// </summary>
        private void RemoveOrphanGamesNow()
        {
            var hosts = ActiveHosts().ToList();
            var ct = CancellationToken.None;

            // Run a fresh sync to get authoritative yield state.
            var summary = hosts.Count == 0
                ? new SyncService.SyncSummary()
                : Task.Run(() => syncService.SyncAllAsync(hosts, MetadataOptions(), ct), ct).GetAwaiter().GetResult();

            // Manual, count-confirmed path — no empty-yield guard. This is the escape hatch
            // for a host whose apps really were all removed.
            var orphans = FindOrphanGames(summary, guardEmptyYield: false);

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
                var summary = syncService.SyncAllAsync(allHosts, MetadataOptions(), progress.CancelToken).GetAwaiter().GetResult();
                ReconcileOrphansByName(summary);
                MarkOrphansUninstalled(summary);
                ImportNewGames(summary);

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

        /// <summary>
        /// Pushes the configured platform and tags onto games that are already in the
        /// library.
        ///
        /// Playnite applies platform and tag metadata at first import only — its
        /// library-update reconciliation touches install state, playtime, last activity,
        /// completion status and install size, and nothing else. Changing the setting
        /// therefore reaches existing entries only through a deliberate pass like this
        /// one, which is why it is a menu action rather than something the sync does.
        ///
        /// Tags are added, never removed: the configured list is a floor, not the whole
        /// set, so tags the user added by hand — and the host-derived category and
        /// library-source tags — survive. The platform, being single-valued, is replaced.
        /// </summary>
        private void ApplyPlatformAndTagsToExisting()
        {
            var ourGames = PlayniteApi.Database.Games.Where(g => g != null && g.PluginId == Id).ToList();
            if (ourGames.Count == 0)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    ResourceProvider.GetString("LOC_SunshineLibrary_ApplyPlatformTags_NoGames"),
                    ResourceProvider.GetString("LOC_SunshineLibrary_Name"));
                return;
            }

            var options = MetadataOptions();
            if (!options.IsConfigured)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    ResourceProvider.GetString("LOC_SunshineLibrary_ApplyPlatformTags_NothingConfigured"),
                    ResourceProvider.GetString("LOC_SunshineLibrary_Name"));
                return;
            }

            // Describe the pass from the settings alone. Resolving first would create the
            // platform and tag rows in Playnite's database, leaving them behind as orphans
            // if the user then answers No.
            var confirm = PlayniteApi.Dialogs.ShowMessage(
                string.Format(
                    ResourceProvider.GetString("LOC_SunshineLibrary_ApplyPlatformTags_Confirm"),
                    ourGames.Count,
                    string.IsNullOrWhiteSpace(options.PlatformName)
                        ? ResourceProvider.GetString("LOC_SunshineLibrary_ApplyPlatformTags_PlatformUnchanged")
                        : options.PlatformName.Trim(),
                    options.CleanTags().Count()),
                ResourceProvider.GetString("LOC_SunshineLibrary_Name"),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            // Only now do we touch the database.
            var platform = ResolveConfiguredPlatform(options);
            var tagIds = ResolveConfiguredTagIds(options);

            var updates = new List<Game>();
            using (PlayniteApi.Database.BufferedUpdate())
            {
                foreach (var g in ourGames)
                {
                    var changed = false;

                    if (platform != null &&
                        (g.PlatformIds == null || g.PlatformIds.Count != 1 || g.PlatformIds[0] != platform.Id))
                    {
                        g.PlatformIds = new List<Guid> { platform.Id };
                        changed = true;
                    }

                    // Assign a new list rather than mutating in place: TagIds' setter is what
                    // raises change notification for both TagIds and the derived read-only
                    // Tags collection, so an in-place Add would persist but leave the tag
                    // chips and filter counts stale until restart.
                    var missing = tagIds.Where(id => g.TagIds == null || !g.TagIds.Contains(id)).ToList();
                    if (missing.Count > 0)
                    {
                        var merged = g.TagIds == null ? new List<Guid>() : new List<Guid>(g.TagIds);
                        merged.AddRange(missing);
                        g.TagIds = merged;
                        changed = true;
                    }

                    if (changed) updates.Add(g);
                }

                if (updates.Count > 0) PlayniteApi.Database.Games.Update(updates);
            }

            logger.Info($"Applied platform/tags to {updates.Count} of {ourGames.Count} game(s).");
            PlayniteApi.Notifications.Add(new NotificationMessage(
                "sunshine-apply-platform-tags",
                string.Format(
                    ResourceProvider.GetString("LOC_SunshineLibrary_ApplyPlatformTags_Done"),
                    updates.Count, ourGames.Count),
                NotificationType.Info));
        }

        /// <summary>
        /// The configured platform as a database row, creating it if the name is new.
        /// <c>Add(name)</c> is get-or-add, so it reuses an existing row of that name.
        ///
        /// Returns null when no platform is configured, which leaves each game's platform
        /// untouched. Resolving the built-in specification instead would reset platforms
        /// on a user who only wanted to apply tags.
        /// </summary>
        private Platform ResolveConfiguredPlatform(LibraryMetadataOptions options)
        {
            var name = options?.PlatformName;
            return string.IsNullOrWhiteSpace(name) ? null : PlayniteApi.Database.Platforms.Add(name.Trim());
        }

        /// <summary>Configured tags as database rows, creating any that don't exist yet.</summary>
        private List<Guid> ResolveConfiguredTagIds(LibraryMetadataOptions options)
        {
            var ids = new List<Guid>();
            if (options == null) return ids;

            foreach (var name in options.CleanTags())
            {
                var tag = PlayniteApi.Database.Tags.Add(name);
                if (tag != null && !ids.Contains(tag.Id)) ids.Add(tag.Id);
            }
            return ids;
        }

        private void RunManualResync()
        {
            var hosts = ActiveHosts().ToList();
            if (hosts.Count == 0) return;

            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.ProgressMaxValue = hosts.Count;
                var summary = syncService.SyncAllAsync(hosts, MetadataOptions(), progress.CancelToken).GetAwaiter().GetResult();
                ImportNewGames(summary);

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

        /// <summary>
        /// The empty-yield guard suppresses cleanup for a host, which is otherwise invisible
        /// outside the log — a user relying on AutoRemoveOrphanedGames would just see stale
        /// entries never go away. The notification id is per-host and stable, so a host that
        /// trips the guard on every sync replaces its own message instead of stacking.
        /// </summary>
        private void SurfaceEmptyYieldGuard(HostConfig host)
        {
            var mode = settingsVm.Settings?.NotificationMode ?? NotificationMode.Always;
            if (mode == NotificationMode.Never) return;

            var text = string.Format(ResourceProvider.GetString("LOC_SunshineLibrary_Sync_EmptyYieldGuard"), host.Label);
            PlayniteApi.Notifications.Add(new NotificationMessage(
                $"sunshine-empty-yield-{host.Id}", text, NotificationType.Info));
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
