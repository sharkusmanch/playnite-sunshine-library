# Architecture

Internal structure of SunshineLibrary. Audience: contributors and future maintainers.

## Core split

Two orthogonal abstractions, never cross-coupled:

- **`HostClient`** (`Services/Hosts/`) — talks to a streaming server's admin HTTP API. Lists apps, fetches covers, closes sessions.
- **`StreamClient`** (`Services/Clients/`) — spawns a local streaming-client process with argv and tracks its lifetime.

The plugin layer wires the two: `HostClient.ListAppsAsync` produces Playnite `GameMetadata`; `StreamClient.BuildLaunch` produces the `ClientLaunchSpec` that Playnite's `AutomaticPlayController` executes.

## Key types

| Type | Location | Role |
|---|---|---|
| `SunshineLibrary` | root | `LibraryPlugin` entrypoint; wires everything |
| `HostClient` (abstract) | `Services/Hosts/HostClient.cs` | Pinned-cert HttpClient lifecycle, typed `HostResult<T>`, shared GET/POST helpers |
| `SunshineHostClient` | `Services/Hosts/` | sha256 app IDs; CSRF token flow for POST |
| `ApolloHostClient` | `Services/Hosts/` | uuid app IDs; no CSRF |
| `HostClientFactory` | `Services/Hosts/` | `ProbeServerTypeAsync` via `/api/config`, constructs right subclass |
| `PinningWebRequestHandler` | `Services/Hosts/` | Instance-level SPKI SHA-256 pin; accepts `RemoteCertificateNameMismatch` when pin matches |
| `TestConnectionService` | `Services/Hosts/` | Step-by-step probe (DNS / TCP / Cert / PinMatch / ServerTypeDetect / ListApps) |
| `CertProbe` | `Services/Hosts/` | Raw TLS cert fetch without validation; only place validation is disabled |
| `PseudoAppFilter` | `Services/Hosts/` | Per-host exclude list with flavor-default seed |
| `StreamClient` (abstract) | `Services/Clients/` | `ProbeAvailability` + `BuildLaunch` |
| `MoonlightCompatibleClient` (abstract) | `Services/Clients/` | Shared impl for all moonlight-qt-lineage clients; houses argv composer |
| `MoonlightClient` | `Services/Clients/` | Thin subclass — Id, DisplayName, locator config |
| `ClientLocator` | `Services/Clients/` | Config-driven multi-exe PATH / Scoop / Winget / Program Files probe |
| `PasteArguments` | `Services/Clients/` | CoreFX-style argv quoting |
| `PreLaunchValidator` | `Services/Clients/` | Advisory warnings (HDR+H.264, bitrate/fps range) |
| `SyncService` | `Services/` | Parallel fan-out across hosts with `SemaphoreSlim(4)`; cache fallback on failure |
| `AppCache` | `Services/` | Per-host JSON cache of last-good RemoteApp list |
| `CredentialStore` | `Services/` | DPAPI-wrapped per-host credential blobs |
| `OverrideStore` | `Services/` | Per-game `StreamOverrides`, keyed by GameId |
| `DisplayProbe` | `Services/` | P/Invoke display detection (res / refresh / HDR) for `Auto` modes |
| `SafeLogging` | `Services/` | Redaction helpers for Authorization / Cookie / CSRF headers |
| `SunshineLibrarySettings` | `Settings/` | Persistent POCO (SettingsVersion, hosts, client, notification mode, global overrides, auto-remove flag) |
| `SunshineLibrarySettingsViewModel` | `Settings/` | `ISettings` editing-clone + credential hydration |
| `AddEditHostWindow` | `Settings/` | Modal — built via `PlayniteApi.Dialogs.CreateWindow` so chrome matches active theme |

## Data flow — sync

```
Playnite library update
  → SunshineLibrary.GetGames
    → SyncService.SyncAllAsync(hosts)       // parallel, SemaphoreSlim(4)
      → per host:
        HostClientFactory.Create(host)
        if ServerType.Unknown: ProbeServerTypeAsync → /api/config
        HostClient.ListAppsAsync → HostResult<IReadOnlyList<RemoteApp>>
        on success: AppCache.Save(hostId, apps)
        on failure: AppCache.TryLoad(hostId) → yield with `offline` tag
        PseudoAppFilter.Apply (hostid exclude list)
        for each app: HostClient.FetchCoverAsync → inline CoverImage
      → returns SyncSummary { Results[], AllGames }
    → ReconcileOrphansByName(summary)       // (hostId, name) match → GameId rewrite
    → MarkOrphansUninstalled(summary, hosts)
      - if AutoRemoveOrphanedGames: also delete + wipe overrides
    → return summary.AllGames to Playnite
```

## Data flow — launch

```
User clicks Play
  → SunshineLibrary.GetPlayActions
    → ResolveHostFromGame(game) via GameId prefix
    → DisplayProbe.Detect() for Auto overrides
    → overrides = builtin ∘ global ∘ host.Defaults ∘ OverrideStore[gameId]
    → PreLaunchValidator.Inspect(overrides, display) → notify warnings
    → StreamClientRegistry.Resolve(ClientSettings)
    → client.ProbeAvailability → ClientAvailability
    → if not installed: notify + yield break
    → client.BuildLaunch(host, app, merged, display) → ClientLaunchSpec
  → Playnite spawns via AutomaticPlayController(TrackingMode.Process)

Game exits
  → OnGameStopped
    → guard: args.Game.PluginId == Id
    → HostClient.CloseCurrentAppAsync (CSRF for Sunshine; plain for Apollo)
```

## Persistence layout

Root: `%AppData%\Playnite\ExtensionsData\{plugin-id}\`

```
config.json                        # SunshineLibrarySettings (JsonIgnore on AdminPassword)
creds/{hostId}.dat                 # DPAPI blob: {"user": "...", "password": "..."}, entropy = SID+hostId
cache/{hostId}.json                # Last-good RemoteApp[] per host
overrides.json                     # Dictionary<GameId, StreamOverrides>
covers/{hostId}/{appId}.png        # Cover art cache (future)
```

Settings JSON on disk never contains plaintext passwords. Legacy `MoonlightPath` field (pre-refactor) auto-migrates into `ClientPaths["moonlight-qt"]` on load.

## Security boundaries

| Concern | Mechanism |
|---|---|
| Self-signed cert trust | `PinningWebRequestHandler` per-host, SPKI SHA-256, TOFU confirmation dialog. Never `ServicePointManager.ServerCertificateValidationCallback` (process-global) |
| Name mismatch (Tailscale / MagicDNS / IP-literal) | Accepted when SPKI pin matches |
| Cert rotation / mismatch | Blocking `HostResult.CertMismatch`, re-pin dialog in settings flow — never silent re-pin |
| Credentials at rest | `Encryption.EncryptToFile` equivalent via `ProtectedData.Protect` + `DataProtectionScope.CurrentUser`; entropy = `SID + ":" + hostId` |
| Command injection | `PasteArguments` (CoreFX algo) around every argv element; `ExtraArgs` strips `\0`/`\r`/`\n` |
| Log leakage | `SafeLogging.Redact` regex strips Authorization / Cookie / Set-Cookie / X-CSRF-Token |
| Response DoS | `MaxResponseBytes = 4 MB`, `JsonTextReader.MaxDepth = 32` |
| Settings schema drift | `SettingsVersion` int; plugin refuses unknown future versions |

Validation-only surfaces (`Address` field rejects `://`, paths, shell chars) in `SunshineLibrarySettingsViewModel.VerifySettings`.

## Override merge

```
result = StreamOverrides.BuiltinDefault                 // Resolution=Auto, Fps=Auto, Hdr=Auto
         .MergedWith(settings.GlobalOverrides)
         .MergedWith(host.Defaults)
         .MergedWith(overrideStore.TryGet(gameId))      // per-game wins
```

`MergedWith(other)` returns a new `StreamOverrides` with `other ?? this` per field — null / `Inherit` at any layer falls through. Tri-state fields (`ResolutionMode`, `FpsMode`, `Hdr`) use enum nullable; scalar fields use nullable types. `JsonSerializerSettings` forces `NullValueHandling.Include` so `null` vs `false` round-trip distinctly.

## Flavor detection

`HostClientFactory.ProbeServerTypeAsync` calls `/api/config`:

1. Response contains `vdisplayStatus` field → **Apollo** (Windows builds only; Apollo-exclusive)
2. Response contains `platform` + `version` → **Sunshine** (standard shape; also catches non-Windows Apollo — safe default)
3. Otherwise → **Unknown**

Cached on `HostConfig.ServerType`. Future Vibepollo detection inserts before Apollo (Vibepollo inherits Apollo's `vdisplayStatus`).

## Extension points

### Add a server type

1. New DTO in `Models/` if wire format differs.
2. New `HostClient` subclass overriding `ListAppsAsync` / `FetchCoverAsync` / `CloseCurrentAppAsync` and any flavor-specific endpoints.
3. Add `ServerType` enum value.
4. Update `HostClientFactory.Create` switch + `ProbeServerTypeAsync` discriminator.
5. Update `PseudoAppFilter.DefaultsFor` if different pseudo-apps apply.

### Add a moonlight-compatible client

1. New subclass of `MoonlightCompatibleClient` (~20 lines):
   ```csharp
   public override string Id => "artemis-qt";
   public override string DisplayName => "Artemis";
   protected override ClientLocatorConfig LocatorConfig => new ClientLocatorConfig {
       ExeNames = new[] { "artemis-qt.exe", "Artemis.exe" },
       InstallDirNames = new[] { "Artemis" },
       ScoopAppNames = new[] { "artemis" },
       WingetPackagePatterns = new[] { "Artemis*" },
   };
   ```
2. Register in `StreamClientRegistry` ctor.
3. Add localized display name if needed.

### Add a non-Moonlight-compatible client

Subclass `StreamClient` directly, implement `ProbeAvailability` + `BuildLaunch` from scratch. Defer until a real candidate emerges (none currently viable — see REVIEW.md).

## Threading

- `GetGames` runs on a Playnite worker thread. Async sync work wraps with `Task.Run(...).GetAwaiter().GetResult()`.
- `OnGameStopped` fires fire-and-forget via `Task.Run`. Safe because host close is idempotent.
- UI updates from background work use `dialog.Dispatcher.BeginInvoke`.
- `OverrideStore` is lock-protected (`lock(gate)`).
- Cert-pin confirmation dialogs only open from the **settings Add-Host flow**, never from sync paths.

## Build pipeline

```
task clean     rm -rf dist
task restore   dotnet restore
task format    dotnet format
task test      dotnet test Tests/... (MSTest, 64+ tests)
task build     dotnet build --configuration Release
task pack      Toolbox.exe pack → dist/SunshineLibrary_<guid>_<ver>.pext
task install   open .pext in Playnite
task all       all of the above
task logs      tail extensions.log
task bump      VERSION=x.y.z → release branch + changelog from git log
```

Target: `net462`, `PlayniteSDK 6.12.0`, `Newtonsoft.Json 10.0.3` (matches Playnite runtime).

## Tests

Unit tests only — no integration tests against a real host (would need a test server fixture).

| Test | Surface |
|---|---|
| `PasteArgumentsTests` | Quoting edge cases (whitespace, quotes, trailing backslash) |
| `MoonlightClientBuildLaunchTests` | argv composition across override combinations |
| `OverrideMergeTests` | Null / true / false round-trip; merge precedence |
| `OverridePrecedenceTests` | Full 4-layer chain |
| `OverrideStoreTests` | Persistence round-trip + corruption handling |
| `CredentialStoreTests` | DPAPI round-trip + delete |
| `AppCacheTests` | Cache round-trip + corruption handling |
| `SunshineCsrfTests` | CSRF token cache + 403 refresh-and-retry |
| `SafeLoggingTests` | Redaction of auth/cookie/CSRF headers |
| `SettingsVersionTests` | Forward-compat refusal, password JSON exclusion |

HTTP-level tests inject `HttpMessageHandler` via an `internal` ctor on `HostClient` and `InternalsVisibleTo`.

## Localization

All user-facing strings in `Localization/<locale>.xaml` as `<sys:String x:Key="LOC_SunshineLibrary_...">`. Access via `ResourceProvider.GetString(key)`.

- `en_US.xaml` is the source of truth
- 18 additional locales produced by translation pass; may drift when new keys are added — re-translate before release
- Key convention: `LOC_SunshineLibrary_<Category>_<Name>`

## Conventions

- Dialogs via `PlayniteApi.Dialogs.CreateWindow(WindowCreationOptions)` — inherits active theme. Never subclass `Window` directly in plugin code.
- Inheritable text color via `SetResourceReference(TextElement.ForegroundProperty, "TextBrush")` on content root; help text uses `"TextBrushDarker"`.
- All `LibraryPlugin` game-scoped hooks guard with `args.Game.PluginId == Id`.
- `HostResult` / `HostResult<T>` are the only typed error surface — no string-match error handling.
- No `Brushes.Gray` or `SystemColors.*` for theme-sensitive text; use `DynamicResource`.
