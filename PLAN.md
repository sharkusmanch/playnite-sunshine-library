# SunshineLibrary — Playnite Library Provider for Sunshine/Apollo

Status: **draft plan, pre-implementation**
Author: sharkusmanch
Complements: [playnite-apollo-sync](../playnite-apollo-sync/) (which syncs the opposite direction)

## 1. Goal

A Playnite `LibraryPlugin` that imports apps from one or more remote Sunshine/Apollo hosts as Playnite library entries and launches `moonlight-qt` to stream them, with per-game overrides for resolution, framerate, bitrate, HDR, codec, audio, and display mode.

Daily-use promise: open Playnite → see my streaming PCs' games alongside local ones → click Play → Moonlight streams → exit game → Playnite logs playtime.

## 2. Scope

**In scope**
- Import apps from N Sunshine/Apollo hosts via their admin REST API
- Launch Moonlight with per-session overrides merged from global → per-host → per-game
- Per-host settings (address, port, credentials, pinned cert, Moonlight path, default overrides)
- Per-game override UI (resolution/fps/bitrate/HDR/codec/display-mode/audio/extra-flags)
- Metadata: name, source = host label, cover art via best-available path (IGDB default, optional fetch from host `image-path`)
- Playtime tracking via `TrackingMode.Process` on `moonlight-qt`
- Localization-ready
- Unit tests for arg merging, cache, settings migration

**Out of scope (v1)**
- Moonlight pairing UX (user pairs via Moonlight's own UI — we only read/launch)
- Launching non-Moonlight clients (Artemis, embedded client)
- Host-side app editing (that's ApolloSync's or the web UI's job)
- Android/Linux variants — Windows-only first release
- WoL implementation (Moonlight already wakes paired hosts)

## 3. Tech stack

- `net462` WPF, matches ApolloSync exactly
- `PlayniteSDK` 6.12.0, `Newtonsoft.Json` 10.0.3
- Plugin type: `LibraryPlugin` (NOT `GenericPlugin`)
- Build: Task + Playnite Toolbox, same Taskfile shape as ApolloSync
- Package: `manifest.yaml` + `extension.yaml`, same release flow

## 4. Architecture

```
+--------------------------+        HTTPS (admin API, basic auth, pinned cert)
|  Sunshine/Apollo host(s) | <---------------------------------------------+
+--------------------------+                                               |
                                                                           |
+------------------------------------------------------------------+       |
| Playnite process                                                 |       |
|                                                                  |       |
|   SunshineLibrary (LibraryPlugin)                                |       |
|     |-- HostClient (one per host, pinned cert, basic auth) ------+       |
|     |-- SyncService  — parallel fan-out across hosts                     |
|     |-- AppCache     — per-host last-good snapshot                       |
|     |-- OverrideStore — global/host/game overrides, merged at launch     |
|     |-- ArgBuilder   — composes moonlight-qt argv                        |
|     |-- Settings UI + per-game UI (WPF)                                  |
|     |-- MetadataProvider (optional, image-path fetch)                    |
|                                                                          |
|   GetGames() -> IEnumerable<GameMetadata> with one GameAction per game   |
|                                                                          |
+------------------------------------------------------------------+
                  |
                  | spawns
                  v
            moonlight-qt.exe stream <host> "<app>" --resolution ... --fps ...
```

## 5. Data model

```csharp
// Persisted in plugin settings (Playnite-managed), passwords DPAPI-wrapped
enum HostFlavor { Unknown, Sunshine, Apollo }

class HostConfig {
    Guid         Id;                 // stable, generated once
    string       Label;              // "LivingRoom PC"
    string       Address;            // hostname / IP / Tailscale MagicDNS
    int          Port = 47990;
    string       AdminUser;
    string       AdminPasswordEnc;   // ProtectedData (CurrentUser scope, optionalEntropy = SHA256(Id))
    string       CertFingerprintSpkiSha256;
    HostFlavor   Flavor = Unknown;   // sniffed on first connect, cached
    string       FlavorVersion;      // e.g. "2024.7.0", re-checked on plugin start
    byte[]       MacAddress;         // for WoL, optional
    bool         Enabled = true;
    List<string> ExcludedAppNames;   // seeded from flavor default set at add-time; user-editable
    StreamOverrides Defaults;
}

// Tri-state for fields that can auto-detect from the client display
enum ResolutionMode { Inherit, Auto, Static }   // Auto = detect active monitor at launch
enum FpsMode        { Inherit, Auto, Static }
enum HdrMode        { Inherit, Auto, On, Off }  // Auto = match client display HDR state

class StreamOverrides {          // null / Inherit = take from next level up
    ResolutionMode?  ResolutionMode;
    string?          ResolutionStatic;   // "1920x1080" when Static
    FpsMode?         FpsMode;
    int?             FpsStatic;          // when Static
    HdrMode?         Hdr;
    int?             BitrateKbps;
    string?          VideoCodec;         // "auto" | "H.264" | "HEVC" | "AV1"
    string?          DisplayMode;        // "fullscreen" | "windowed" | "borderless"
    string?          AudioConfig;        // "stereo" | "5.1-surround" | "7.1-surround"
    bool?            Yuv444;
    bool?            FramePacing;
    bool?            GameOptimization;
    string?          ExtraArgs;          // escape hatch (advanced, off by default)
}

// GameId = "{host.Id}:{app.Uuid ?? sha256(app.Name|app.Cmd)}"
```

**Override merge order:** `builtin-default  <  global  <  host.Defaults  <  (optional client profile)  <  perGame`. Null / `Inherit` at any level falls through. **Built-in defaults for resolution, FPS, and HDR are `Auto`** (detect from the client's current display at launch); built-in defaults for other fields are "don't emit the flag, let Moonlight use its own configured value."

**Serialization.** `StreamOverrides` must round-trip `null` (inherit), `false`, and `true` as distinct values — a missing JSON key must mean the same thing as an explicit `null`, not `false`. Shared settings:

```csharp
static readonly JsonSerializerSettings OverrideSettings = new JsonSerializerSettings {
    NullValueHandling    = NullValueHandling.Include,     // keep explicit nulls on disk
    DefaultValueHandling = DefaultValueHandling.Include,
    MissingMemberHandling = MissingMemberHandling.Ignore, // tolerate forward-compat additions
};
```

Round-trip unit test in `OverrideMerge.Tests.cs`: for each `bool?` field, assert `null`, `false`, and `true` all survive save → load as themselves.

## 6. Host integration — flavor-subclass model

Sunshine and Apollo share a base admin API but have meaningfully different capabilities. Model this with a base class + two subclasses; never `if (flavor == Apollo)` in feature code.

```csharp
abstract class HostClient : IDisposable {
    public HostConfig          Config   { get; }
    public abstract HostFlavor Flavor   { get; }
    protected WebRequestHandler Handler { get; }   // instance-level cert pin
    protected HttpClient        Http    { get; }

    // Shared contract — implemented per flavor
    public abstract Task<HostResult<IReadOnlyList<RemoteApp>>> ListAppsAsync(CancellationToken ct);
    public abstract string  GetStableAppId(RemoteApp app);    // uuid or sha256 composite
    public abstract Task<HostResult<byte[]>> FetchCoverAsync(RemoteApp app, CancellationToken ct);
    public abstract Task<HostResult> CloseCurrentAppAsync(CancellationToken ct);
    public abstract Task<HostResult<HostInfo>> ProbeAsync(CancellationToken ct);
}

sealed class SunshineHostClient : HostClient {
    // CSRF token obtained before any POST, cached with short TTL
    // App IDs: sha256(name + "\0" + cmd) — Sunshine has no uuid, array index is unstable
    // Cover: GET {adminBase}/appasset/{index}/box.png   (verified per version at implementation)
}

sealed class ApolloHostClient : HostClient {
    // App IDs: app.uuid (always present on Apollo)
    // Cover:   GET {adminBase}/appasset/{uuid}/box.png or host-configured path
    // + Apollo-only surface:
    public Task<HostResult<string>> GenerateOtpAsync(CancellationToken ct);              // /api/otp
    public Task<HostResult<IReadOnlyList<PairedClient>>> ListClientsAsync(CancellationToken ct);
    public Task<HostResult> UpdateClientAsync(PairedClientUpdate u, CancellationToken ct);
    public Task<HostResult> DisconnectClientAsync(Guid clientId, CancellationToken ct);
    public Task<HostResult> UnpairClientAsync(Guid clientId, CancellationToken ct);
    public Task<HostResult> LaunchAppAsync(string uuid, CancellationToken ct);           // /api/apps/launch
    public Task<HostResult> ResetDisplayPersistenceAsync(CancellationToken ct);
}
```

**Flavor detection** runs in `ProbeAsync` during Add-Host and at plugin startup:
- `GET /api/config` → parse response
- Apollo-only keys (e.g. `client_permissions`, OTP-related fields) → Apollo
- Otherwise → Sunshine
- Persist `Flavor` and `FlavorVersion` in `HostConfig`; re-sniff on each startup is cheap
- On a flavor downgrade (Apollo → Sunshine, user reinstalled), clear Apollo-only state and surface a notification

**Shared conventions (both flavors):**
- Base URL: `https://{address}:{port}` (default 47990, per-host configurable)
- Auth: HTTP Basic, admin user/password
- Cert: self-signed; **SPKI** SHA-256 pinned per host via instance `WebRequestHandler.ServerCertificateValidationCallback`
- Accept `SslPolicyErrors.RemoteCertificateNameMismatch` iff pin matches (Tailscale/MagicDNS path)
- One `HttpClient` per host, reused; `ConnectionLeaseTimeout = 5min` on the `ServicePoint` (net462 DNS cache workaround); `DefaultConnectionLimit = max(hosts*2, 8)`
- `SecurityProtocol = Tls12 | Tls13` (numeric cast for Tls13 on net462)
- Request timeout: 5s connect, 10s total. Retry(2, expBackoff) on transient IO only. `CancellationToken` threaded end-to-end
- Concurrency cap across hosts: `SemaphoreSlim(4)`
- Sunshine-only: `POST /api/csrf-token` before any mutating call; cache and attach header
- Apollo-only: CSRF either absent or differently-shaped — subclass handles

## 7. Moonlight launch

- Executable path: user-provided (default probe `%ProgramFiles%\Moonlight Game Streaming\moonlight-qt.exe`).
- Verb: `stream "<host.Address>" "<app.Name>"` — use `Address` not `Label` (moonlight expects reachable hostname).
- `--quit-after` is **always** emitted (not an override) — Moonlight exits when the host app exits, which Playnite's playtime tracking requires.

### 7a. Auto-detect vs static defaults (resolution, FPS, HDR)

By default we match the **client's current display** at launch time so the stream looks right whether the user is docked at 4K, playing on a 1080p handheld, or on an HDR TV — with zero per-game fiddling.

Resolution / FPS / HDR are tri-state (`Auto | Static | Inherit`). Built-in default = `Auto`. User can pin `Static` values at global or per-host level. Per-game overrides take precedence over everything.

**Detection (runs in the `PlayController` at launch, never at sync):**
- Target monitor = the display currently showing the Playnite window (`PresentationSource.FromVisual(MainWindow).CompositionTarget.TransformToDevice` → map to a `Screen`). Fallback to primary.
- **Resolution:** `EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, &DEVMODE)` via P/Invoke → `dmPelsWidth` × `dmPelsHeight`. Use physical pixels (pre-DPI-scaling). Emit `--resolution WxH`.
- **Refresh rate:** same `DEVMODE.dmDisplayFrequency`. Emit `--fps N`. Clamp to Moonlight's 10–480 range.
- **HDR:** `DisplayConfigGetDeviceInfo` with `DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO` (Windows 10 1803+). If `advancedColorEnabled` → emit `--hdr`, else `--no-hdr`. On older Windows, skip the HDR flag and let Moonlight decide.
- All detection is best-effort: if a P/Invoke fails, log at Debug and omit the flag rather than aborting the launch.

### 7b. Flag set

Emitted from merged `StreamOverrides` after auto-resolution:
- `--resolution WxH` (always explicit — avoid the `--1080` shortcuts)
- `--fps N`, `--bitrate N`
- `--hdr` / `--no-hdr`
- `--video-codec`, `--display-mode`, `--audio-config`
- `--yuv444`/`--no-yuv444`, `--frame-pacing`/`--no-frame-pacing`, `--game-optimization`/`--no-game-optimization`
- `--quit-after` (always)
- `ExtraArgs` verbatim last (advanced, opt-in)

Arg quoting: CoreFX `PasteArguments` algorithm (backslash-doubling + quote-doubling). Unit-test with: space, `"`, trailing `\`, `\\"`, unicode, empty string.

### 7c. Pre-launch sanity checks (advisory, non-blocking)

Run before composing argv; show a one-shot toast if violated:
- `Hdr=On` with `VideoCodec=H.264` (HDR requires HEVC Main10 or AV1)
- `Bitrate` outside 500–500000 Kbps
- `Fps` outside 10–480
- AV1 + 4K on a client with no AV1 decoder (best-effort GPU probe; skip if unknown)

### 7d. StreamClient abstraction (for future non-Moonlight clients)

The launch target is always "a local streaming client process we spawn and track." Model this as `StreamClient` so Moonlight is one implementation among potentially many (Artemis, Moonlight Embedded, future forks). The host API and the client API are orthogonal and must not leak into each other.

```csharp
abstract class StreamClient {
    public abstract string Id          { get; }   // "moonlight-qt"
    public abstract string DisplayName { get; }   // "Moonlight"

    // Availability — called at plugin start, before each launch, and from settings "Test client"
    public abstract ClientAvailability ProbeAvailability(ClientSettings s);

    // Build a concrete launch from (host, app, merged overrides, detected display)
    public abstract ClientLaunchSpec BuildLaunch(
        HostConfig host, RemoteApp app,
        StreamOverrides merged, ClientDisplayInfo display);
}

class ClientAvailability {
    public bool   Installed;
    public string ExecutablePath;        // resolved absolute path
    public string Version;               // best-effort, may be null
    public string UnavailableReason;     // localized if !Installed
    public bool   SignatureValid;        // Authenticode verify, optional
}

class ClientLaunchSpec {
    public string        Executable;     // absolute path
    public string        Arguments;      // already quoted via PasteArguments
    public string        WorkingDirectory;
    public string        TrackedProcessName;  // e.g. "moonlight-qt" for Playnite tracker
    public TrackingMode  TrackingMode = TrackingMode.Process;
}

sealed class MoonlightClient : StreamClient {
    public override string Id          => "moonlight-qt";
    public override string DisplayName => "Moonlight";

    // Probe: %ProgramFiles%\Moonlight Game Streaming\moonlight-qt.exe,
    //        %LOCALAPPDATA%\Programs\Moonlight Game Streaming\moonlight-qt.exe,
    //        user-configured path override in ClientSettings.
    // Verify exists + (optional) Authenticode signature.

    // BuildLaunch: composes
    //   stream "<host>" "<app>" --quit-after [--resolution WxH] [--fps N]
    //                          [--bitrate N] [--hdr|--no-hdr] [--video-codec ...]
    //                          [--display-mode ...] [--audio-config ...]
    //                          [--yuv444|--no-yuv444] [--frame-pacing|--no-frame-pacing]
    //                          [--game-optimization|--no-game-optimization] [ExtraArgs]
}
```

**Registry & selection:**

```csharp
class StreamClientRegistry {
    public IReadOnlyList<StreamClient> All { get; }   // built-ins + future plug-in points
    public StreamClient GetById(string id);
    public StreamClient Resolve(ClientSettings s);    // user-chosen, default = moonlight-qt
}
```

Plugin settings hold `ClientSettings { ActiveClientId, ClientPaths[clientId] }`. v1 ships exactly one client (`MoonlightClient`) and the "active" picker is a single-item combo — but the abstraction is in place.

### 7e. Play action & tracking

- Override `GetPlayActions(GetPlayActionsArgs)` on the `LibraryPlugin` (not `GameMetadata.GameActions`) so argv is composed at launch from the current overrides and current display state.
- Return an `AutomaticPlayController` configured with the active client's `ClientLaunchSpec`:
  - `Path = spec.Executable`
  - `Arguments = spec.Arguments`
  - `WorkingDir = spec.WorkingDirectory`
  - `TrackingMode = TrackingMode.Process` — track the moonlight-qt process we actually spawned (including its child processes, since `--quit-after` triggers orderly shutdown). This gives accurate per-game playtime tied to the real client session, not a name-match that could pick up an unrelated Moonlight window.
- Playtime = moonlight-qt lifetime from Playnite's perspective. Paired with `--quit-after`, that's the host-app run duration.
- `OnGameStopped` guard: `if (args.Game.PluginId != Id) return;` before any host call.

## 8. Playnite plugin structure

Mirrors ApolloSync directory layout:

```
SunshineLibrary.csproj
SunshineLibrary.cs            // LibraryPlugin entrypoint
Services/
  Hosts/
    HostClient.cs               // abstract base: HTTP, SPKI pin, CSRF hook, retry
    SunshineHostClient.cs       // sha256 app ids, CSRF always, index-based cover path
    ApolloHostClient.cs         // uuid app ids, /api/otp, /api/clients/*, /api/apps/launch
    HostClientFactory.cs        // Probe -> flavor -> instance
  Clients/
    StreamClient.cs             // abstract base: ProbeAvailability, BuildLaunch
    MoonlightClient.cs          // moonlight-qt: path probe, argv, tracked process name
    StreamClientRegistry.cs     // lookup + resolve active client from settings
    PasteArguments.cs           // CoreFX-style arg quoting (used by any client)
  SyncService.cs                // parallel host fan-out, inline cover fetch
  DisplayProbe.cs               // EnumDisplaySettingsEx + DisplayConfigGetDeviceInfo P/Invoke
  AppCache.cs
  CoverCache.cs
  OverrideStore.cs
  Wol.cs                        // magic packet sender
Models/
  RemoteApp.cs                // flavor-agnostic app DTO consumed by library code
  SunshineAppDto.cs           // wire DTO for Sunshine /api/apps
  ApolloAppDto.cs             // wire DTO for Apollo /api/apps (has uuid, etc.)
  PairedClient.cs             // Apollo only
  HostConfig.cs
  HostFlavor.cs
  HostResult.cs               // typed result: Ok | AuthFailed | CertMismatch | Unreachable | Timeout | ServerError
  StreamOverrides.cs
Settings/
  SunshineLibrarySettings.cs           // POCO: ObservableObject, persistent fields only
  SunshineLibrarySettingsViewModel.cs  // ObservableObject + ISettings with editingClone
  SunshineLibrarySettingsView.xaml(.cs)
  GameOverridesView.xaml(.cs)          // per-game right-click dialog
Localization/
  en_US.xaml                  // authoritative during development
  cs_CZ.xaml de_DE.xaml es_ES.xaml fr_FR.xaml it_IT.xaml ja_JP.xaml
  ko_KR.xaml nl_NL.xaml pl_PL.xaml pt_BR.xaml pt_PT.xaml ro_RO.xaml
  ru_RU.xaml sv_SE.xaml tr_TR.xaml uk_UA.xaml zh_CN.xaml zh_TW.xaml
  // all added in the translation milestone; keys identical to en_US.xaml
Tests/
  PasteArguments.Tests.cs      // quoting corner cases (space, quote, trailing \, unicode, empty)
  MoonlightClient.BuildLaunch.Tests.cs  // argv composition for each override combination
  OverrideMerge.Tests.cs       // builtin < global < host < client-profile < game; Auto vs Static vs Inherit
  AppDto.Parse.Tests.cs        // Sunshine + Apollo wire formats
  FlavorDetection.Tests.cs     // /api/config payload -> HostFlavor
  HostClient.CertPinning.Tests.cs   // SPKI match, name-mismatch w/ pin, revoked
  StreamClientRegistry.Tests.cs     // resolve active, missing path, fallback
  SafeLogging.Tests.cs         // redaction + DPAPI round-trip (via Playnite.Common.Encryption)
extension.yaml
manifest.yaml
icon.png
Taskfile.yaml                 // copy from ApolloSync, rename csproj targets
```

Key `LibraryPlugin` overrides:
- `Id`, `Name` ("Sunshine / Apollo")
- `Properties = new LibraryPluginProperties { HasSettings = true, CanShutdownClient = false }`
- `Client` — a `LibraryClient` impl so Playnite shows a "Go to host web UI" and "Test" button
- `GetSettings`, `GetSettingsView` — follow ApolloSync's `ISettings` editing-clone pattern exactly (`ApolloSyncSettings.cs:101-216`):
  - POCO class (`SunshineLibrarySettings : ObservableObject`) holds persistent state only
  - ViewModel (`SunshineLibrarySettingsViewModel : ObservableObject, ISettings`) holds `editingClone` + plugin reference
  - `BeginEdit` → `editingClone = Serialization.GetClone(Settings)`
  - `CancelEdit` → `Settings = editingClone` (reverts mutations bound via WPF)
  - `EndEdit` → `plugin.SavePluginSettings(Settings)`, then trigger any post-save actions (e.g., resync if user enabled sync-on-settings-update, same toggle idea as ApolloSync)
  - `VerifySettings(out List<string> errors)` returns per-field error strings already localized via `ResourceProvider.GetString("LOC_SunshineLibrary_…")`; Playnite blocks the save on any error and shows the list
- `GetGames(LibraryGetGamesArgs)` — yields `GameMetadata { IsInstalled = true, CoverImage, Source, Platforms, Features }`. Does **not** set a static `GameAction`; see `GetPlayActions` below.
- `GetPlayActions(GetPlayActionsArgs)` — composes argv at launch via `StreamClientRegistry.Resolve(settings).BuildLaunch(...)`; returns an `AutomaticPlayController` with `TrackingMode.Process` on the spawned client.
- `GetGameMenuItems` — "Streaming settings…", "Override resolution for this game…", "Clear overrides"
- `GetMainMenuItems` — "Resync host…", "Add host…", "Test all hosts", "Test client" (runs `StreamClient.ProbeAvailability`)
- `OnGameStarting` — preflight: host reachable? active client available? if not, short-circuit with a clear message.
- `OnGameStopped` — guard `args.Game.PluginId == Id` first; then `POST /api/apps/close` on that game's host as cleanup.

## 9. Multi-host design

- Hosts keyed by `Guid`, not address. Renaming / re-IP must not orphan the library.
- Source string handled per §11a (stable across label rename).
- `(host.Id, stable app id)` is the unique `GameId` (stable app id = `uuid` on Apollo, `sha256(name|cmd)` on Sunshine — see §6). Same app on two hosts = two distinct Playnite games (correct — different hardware, different overrides, different playtime).
- `GetGames` queries hosts in parallel; one offline host surfaces a notification but does NOT fail the whole sync. Cached last-good apps still yield (tagged `offline`).
- Per-host "Test Connection" button runs: DNS → TCP → fetch cert → pin confirm (add flow only) → basic auth → `/api/apps` → report which step failed.

**Configuration vs sync boundary (locks in the cert-pin workflow):**
- Pinning happens only during **Add Host** or **Re-pin** in settings. Never inside `GetGames`.
- `GetGames` against a host with no stored pin returns empty + notification pointing to settings ("Configure this host in SunshineLibrary settings to continue syncing.").
- `GetGames` against a host with a mismatched pin returns cached-apps-marked-offline + `CertMismatch` notification. Silent re-pin never happens.
- All UI surfaces from sync paths (`PlayniteApi.Notifications.Add`, `Dialogs.ShowMessage`, window open) go through `Application.Current.Dispatcher` — but by construction no blocking dialog is ever raised from sync, only notifications.

## 10. Per-game overrides UX

- Right-click game → "SunshineLibrary: Streaming settings…" opens a dialog.
- Dialog fields: all `StreamOverrides` with an explicit "Inherit from host" null state per field. Host defaults shown greyed out as the effective fallback value.
- "Reset to host default" button per field. "Copy from another game" picker.
- Stored in `OverrideStore` (plugin JSON), keyed by `GameId`. Survives library resync because `GameId` is stable.

## 11. Sync strategy

- `GetGames` triggered by Playnite's normal library update. Also expose "Resync this host" / "Resync all hosts" menu items.
- Per-host cache written to `%AppData%\Playnite\ExtensionsData\{plugin-id}\cache\{hostId}.json` on every successful fetch.
- On host error: yield from cache with `offline` tag so user can still see / launch (Moonlight will fail with its own message; not our problem to duplicate).
- On app disappearance upstream: stop yielding that `GameId`. Playnite marks game uninstalled. Don't delete — user may still want history/playtime.
- **Pseudo-app / exclude filter** runs before cover fetch to avoid wasted requests. Each host has an `ExcludedAppNames` list:
  - Seeded at Add-Host time from flavor defaults:
    - Sunshine: `["Desktop", "Steam Big Picture", "Terminate"]`
    - Apollo:   `["Desktop", "Steam Big Picture", "Terminate"]` (confirm against Apollo's default `apps.json` at implementation; append any Apollo-specific auto-entries)
  - Fully user-editable in host settings: remove defaults the user wants to keep, add custom entries.
  - Match is case-insensitive exact-name by default; optional glob (`Steam*`) supported if trivial to add.
  - Exclusion is non-destructive — games already in Playnite from a previous sync become uninstalled when the filter starts hiding them, overrides survive (`GameId` keyed by `host.Id + app id`).

## 11a. Stable library Source across host rename

- `Source = new MetadataNameProperty(ResolveSourceName(host))` where `ResolveSourceName(h) => $"Sunshine: {h.Label}"` — single source of truth for display.
- On host-label change (detected in settings `EndEdit` by diffing old vs new `Label`), emit a Playnite-side Source rename before the next sync so existing games follow the new name instead of being re-categorized under a fresh Source and leaving an orphan.
- `host.Id` stays the internal key; the Source string is cosmetic.

## 12. Error handling

- Every host operation: typed result `HostResult<T>` with `Ok | AuthFailed | CertMismatch | Unreachable | Timeout | ServerError(code)`. No string-match error handling.
- Cert mismatch = blocking: stop, surface notification, prompt user to re-pin (§13a flow 3). Do NOT silently re-pin.
- Auth failed: clear stored password, surface notification.
- Logger: Playnite's `LogManager`, scope = host label, never log passwords or full cert (§13e).

### 12a. Notification verbosity — `NotificationMode` enum

Match ApolloSync's existing three-value setting so users of both plugins see one mental model:

```csharp
enum NotificationMode { Always, OnUpdateOnly, Never }
```

Mapping of plugin events to notification-mode gates:

| Event                                     | Always | OnUpdateOnly | Never |
|-------------------------------------------|:------:|:------------:|:-----:|
| Sync complete, N games added              |   ✓    |      ✓       |       |
| Sync complete, no changes                 |   ✓    |              |       |
| Host unreachable (first occurrence)       |   ✓    |      ✓       |       |
| Host unreachable (repeated, dedup'd)      |   ✓    |              |       |
| Cert mismatch                             |   ✓    |      ✓       |   ✓   |
| Auth failed                               |   ✓    |      ✓       |   ✓   |
| Missing pin (host not configured)         |   ✓    |      ✓       |   ✓   |
| Client unavailable at launch time         |   ✓    |      ✓       |   ✓   |

**Security- and launch-critical events always fire regardless of mode** — users who set `Never` are opting out of status chatter, not out of being told their creds are broken. Same spirit as ApolloSync.

Each notification uses a stable per-host per-category `Id` (`sunshine-host-{hostId}-unreachable`, etc.) so repeated events replace rather than stack.

## 13. Security

### 13a. Certificate pinning (TOFU, SPKI SHA-256)

Sunshine and Apollo both ship self-signed certificates — there's no CA to trust. The plugin treats the cert fingerprint recorded when the host was **added** as the source of truth forever after. This is the only line of defense against LAN impersonation (attacker on the same Wi-Fi, compromised router, Tailscale node takeover).

**What we pin:** SHA-256 of the server's **SubjectPublicKeyInfo (SPKI)**, not the leaf cert. SPKI pinning survives cert renewal (Sunshine regenerates the leaf on some upgrades but keeps the key) while still catching the "attacker presents a new keypair" case.

**Mechanism:** per-host instance `WebRequestHandler.ServerCertificateValidationCallback`. Never the process-global `ServicePointManager.ServerCertificateValidationCallback`. Never a `return true` anywhere in the plugin.

**Three flows — implemented explicitly, tested end-to-end:**

**(1) Add host — record the fingerprint**
1. User enters label / address / port / admin creds in "Add host" dialog.
2. Plugin opens a TLS connection (no HTTP fallback), captures the leaf cert without validating.
3. Compute SPKI SHA-256 of the presented cert.
4. Show a blocking confirmation dialog displaying:
   - Hostname the cert is bound to (or "self-signed, hostname: …")
   - SHA-256 fingerprint in `AA:BB:CC:…` 32-byte form
   - Guidance string: *"Verify this matches the fingerprint shown at `https://{host}:{port}/` in a browser. If you didn't add this host, say No."*
5. On user confirm, write `CertFingerprintSpkiSha256` into `HostConfig`. No network call proceeds before confirmation.
6. On decline, abort Add Host — no partial state persists.

**(2) Routine connection — verify the fingerprint**

Every request on this host's `HttpClient`:
1. Callback computes SPKI SHA-256 of the presented cert.
2. Compare to stored `CertFingerprintSpkiSha256` using `CryptographicOperations.FixedTimeEquals` (constant-time).
3. Match → proceed, regardless of `SslPolicyErrors` flags. In particular `RemoteCertificateNameMismatch` is expected on Tailscale/MagicDNS/IP-literal access and is accepted when the pin matches — the pin is the trust boundary, not the hostname.
4. Mismatch → return `HostResult.CertMismatch` with the new fingerprint; do **not** proceed with the request.

**(3) Mismatch recovery — never silent re-pin**
1. A mismatch surfaces a notification with a stable per-host ID: *"The certificate for `{host.Label}` changed. This can happen if Sunshine was reinstalled, or it can mean someone is intercepting the connection."*
2. Notification action → opens a dialog showing **old vs new** fingerprint, with two buttons: *Re-pin to new certificate* and *Cancel*.
3. Re-pin writes the new fingerprint after explicit user click. Nothing happens automatically.
4. While in mismatch state, `GetGames` yields the host's cached apps (marked offline) — the user's library is not wiped because a cert rotated.

**Rotation:** replacing a host (e.g., PC reinstall) flows through (3). No separate "rotate cert" UI needed.

**Export / import / roamed settings:** fingerprint travels with the settings JSON and is safe to be read by anyone — it's a public fingerprint, not a secret.

**Tests** (`HostClient.CertPinning.Tests.cs`):
- SPKI match → OK regardless of SslPolicyErrors
- SPKI mismatch → CertMismatch, no request issued
- Name-mismatch + SPKI match (Tailscale case) → OK
- Name-mismatch + SPKI mismatch → CertMismatch
- Missing pin in config → refuse to connect (forces the add flow)

### 13b. Credentials — Playnite house pattern

Match what JosefNemec's own library plugins do (Epic, Xbox, Amazon): use `Playnite.Common.Encryption.EncryptToFile` / `DecryptFromFile` with the current user's SID as DPAPI entropy. It's a thin wrapper around `ProtectedData.Protect` with the right defaults, and referencing the same helper keeps the plugin consistent with the ecosystem instead of hand-rolling crypto that future contributors would have to re-audit.

```csharp
// Reference idiom (EpicLibrary/Services/EpicAccountClient.cs:161-165,
//                  XboxLibrary/Services/XboxAccountClient.cs:97-105)
var entropy = WindowsIdentity.GetCurrent().User.Value + ":" + host.Id.ToString();
Encryption.EncryptToFile(credPath, json, Encoding.UTF8, entropy);
var json = Encryption.DecryptFromFile(credPath, Encoding.UTF8, entropy);
```

- Entropy is `WindowsIdentity.User SID + ":" + host.Id`. The User SID part is the Epic/Xbox/Amazon pattern; appending `host.Id` adds per-host isolation at zero cost (a stolen settings blob for host A can't be unwrapped as host B's).
- Per-host credential files live at `%AppData%\Playnite\ExtensionsData\{plugin-id}\creds\{hostId}.dat`. The host list itself (addresses, labels, pins) remains in the plugin's normal JSON settings.
- csproj must reference `Playnite.Common.dll` from the Playnite runtime dir:
  ```xml
  <Reference Include="Playnite.Common">
    <HintPath>$(PlayniteDir)\Playnite.Common.dll</HintPath>
    <Private>false</Private>
  </Reference>
  ```
- Catch `CryptographicException` on decrypt (roamed settings, different user, different machine) → clear the bad blob, notify, re-prompt through the Add-Host / Edit-Host flow. Never silent-fail with a broken password.
- "Revoke all credentials" button in settings: delete every `creds/*.dat` file and clear every `CertFingerprintSpkiSha256` — forces a fresh add flow for every host.

### 13c. Transport & input hygiene

- HTTPS only. `Address` field validated as hostname / IPv4 / IPv6 literal — reject `://`, path, whitespace, shell metacharacters. URL composed internally; user never supplies a full URL.
- `SecurityProtocol = Tls12 | Tls13` (numeric cast for Tls13 on net462).
- JSON parse: `MaxDepth = 32`, 4 MB response cap, streaming read (refuse 2 GB nested-array DOS).
- `ExtraArgs` advanced override gated behind a settings toggle; rejects control chars and shell metacharacters even when enabled.
- `MoonlightPath` (or any `StreamClient` executable path): must exist, must have a valid Authenticode signature matching the expected publisher; cache thumbprint and re-verify on change.

### 13d. At rest

- Settings file lives in Playnite's extension data dir — same protection as other plugins.
- Cache JSON files (apps, covers) are HMAC-signed with a DPAPI-derived key. Tampered cache is discarded and re-fetched.
- `SettingsVersion` integer; plugin refuses to load unknown future versions rather than silently downgrade.

### 13e. Logging hygiene

The sampled Playnite ecosystem (Epic, Xbox, Amazon, GOG, ApolloSync, HowLongToBeat) does **not** use `DelegatingHandler`s to scrub headers — the universal pattern is call-site discipline plus a small redaction helper for exception paths. We match that.

**Call-site rules (enforced by code review):**
- Never log `HttpRequestMessage` / `HttpResponseMessage` via `.ToString()` — it includes headers.
- Never log `HttpRequestHeaders.Authorization`, `.Cookie`, `Set-Cookie`, or CSRF-token headers at any level.
- Never log request or response bodies for any endpoint at any level above `Debug`. The login/auth path (`/api/apps` with Basic auth) never logs bodies at all; only `{method, scheme+host+path, status, elapsed, byte count}`.
- Never log URL **query strings** — they can contain tokens. Use `Uri.GetLeftPart(UriPartial.Path)`.
- Exception logging: `ex.Message` + status code only. If a body is needed for diagnosis, pipe through `SafeLogging.Redact` first.
- The `Debug` level is off in packaged builds.

**Model reference**: HLTB's `LogCookieSummary` (`playnite-howlongtobeat-plugin/source/Services/HowLongToBeatApi.cs:70-102`) logs counts + domains + expiry, never values. Adopt the same "summary over content" discipline.

**Helper** (`Services/SafeLogging.cs`) — defense in depth for exception paths that accidentally stringify a response:

```csharp
public static class SafeLogging {
    private static readonly Regex AuthHeader =
        new Regex(@"(?i)\b(Authorization|Cookie|Set-Cookie|X-CSRF[-_]?Token|X-XSRF-TOKEN)\s*[:=]\s*\S+",
                  RegexOptions.Compiled);
    private static readonly Regex BearerOrBasic =
        new Regex(@"(?i)(Basic|Bearer)\s+[A-Za-z0-9+/=._\-]+", RegexOptions.Compiled);

    public static string Redact(string s) =>
        s == null ? null :
        BearerOrBasic.Replace(AuthHeader.Replace(s, "$1: <redacted>"), "$1 <redacted>");

    public static string DescribeRequest(HttpRequestMessage r) =>
        $"{r.Method} {r.RequestUri?.GetLeftPart(UriPartial.Path)} (headers={r.Headers.Count()})";
}
```

**Tests** (`Tests/SafeLoggingTests.cs`):
- `"Authorization: Basic dXNlcjpwYXNz"` → output contains `<redacted>` and not `dXNlcjpwYXNz`.
- JSON blob `{"password":"hunter2","csrf":"abc"}` routed through the plugin's HTTP error-log helper → captured log sink contains neither literal.
- DPAPI round-trip: `EncryptToFile` → `DecryptFromFile` with same entropy round-trips identity; different entropy throws `CryptographicException`.
- UNVERIFIED in the research: `Playnite.Common.Encryption` surface availability in SDK 6.12 — confirmed used by Epic/Xbox/Amazon at runtime, but verify reference resolves at M1 scaffold time.

## 14. Metadata — inline cover at sync time

Set `GameMetadata.CoverImage` **inline during `GetGames`**, matching the itch.io and Uplay reference plugins (`PlayniteExtensions/source/Libraries/ItchioLibrary/ItchioLibrary.cs:271`, `UplayLibrary/UplayLibrary.cs:205`). No separate `LibraryMetadataProvider` class.

Rationale:
- Covers are one cheap HTTP call per game against the same host we already queried for `/api/apps`.
- Inline covers are visible immediately on first import — no "Download Metadata" action required.
- Steam/GOG only use `LibraryMetadataProvider` because they also fetch expensive richer metadata (descriptions, genres, ratings). We don't — the host has nothing richer than a cover.
- Playnite's metadata-source priority UI still treats the plugin's cover as the library-plugin source, so "prefer IGDB" users get IGDB without us doing anything special.

Each `HostClient` subclass exposes `FetchCoverAsync(RemoteApp)`; `SyncService` awaits covers in parallel per-host (same `SemaphoreSlim(4)` bound). If `FetchCoverAsync` fails or returns empty, leave `CoverImage = null` and let Playnite fall through to IGDB/SteamGridDB per user priority.

Cache: `%AppData%\Playnite\ExtensionsData\{plugin-id}\covers\{hostId}\{appId}.png`, re-fetched only when the host's per-app cover hash (ETag / content-length / host-reported path) changes — avoids redownloading every sync.

Hardening:
- 8 MB per-image size cap
- `BitmapDecoder` with `BitmapCreateOptions.IgnoreColorProfile` + metadata disabled (ICC/EXIF parser surface)
- `image-path` from the host is never followed by us — we only hit the admin HTTP cover endpoint on that host.

No `GetMetadataDownloader()` override in `LibraryPlugin`.

## 15. Localization

All user-facing strings go through Playnite's `ResourceProvider.GetString("LOC_…")` from day one — matching the author's other extensions (ApolloSync ships 19 locales). No string literals in code, XAML, notifications, menu items, tooltips, dialogs, or log lines that reach the user.

**Key convention:** `LOC_SunshineLibrary_<Category>_<Name>`
- `LOC_SunshineLibrary_Menu_ResyncAll`
- `LOC_SunshineLibrary_Settings_Tab_Hosts`
- `LOC_SunshineLibrary_Host_TestConnection`
- `LOC_SunshineLibrary_Error_CertMismatch_Body`
- `LOC_SunshineLibrary_Client_NotInstalled`

**Authoring rules during development:**
- Only `Localization/en_US.xaml` is edited during M1–M4. It is the source of truth.
- Every new user-facing string added to code is added to `en_US.xaml` in the same commit (no drift, enforced by a simple grep check in tests or CI: any `"LOC_SunshineLibrary_"` literal in source must exist as an `x:Key` in `en_US.xaml`).
- XAML: `Content="{DynamicResource LOC_SunshineLibrary_…}"` — never `Content="Test Connection"`.
- Code: `PlayniteApi.Resources.GetString("LOC_SunshineLibrary_…")` (ApolloSync uses `ResourceProvider.GetString` directly; either matches and should be consistent with its choice).
- Notification IDs stay in English as stable keys; only the `Text` is localized.

**Translation milestone** is deferred to a dedicated late-stage step (see §16 M4.5) — copy all 18 non-English locale files from ApolloSync's locale set, replace each value with a machine translation or empty placeholder keyed off `en_US.xaml`, then iterate. Community PRs for corrections are expected post-release, same pattern as ApolloSync.

## 16. Milestones

1. **M1 — Vertical slice (hardcoded host):** csproj scaffolding with `<Content CopyToOutputDirectory="PreserveNewest">` for `extension.yaml`, `manifest.yaml`, `icon.png`, `Localization/*.xaml` (copy block verbatim from ApolloSync.csproj:27-43). Plugin `Guid` generated and baked into `SunshineLibrary.cs`, `extension.yaml:Id`, `manifest.yaml:AddonId`. `Taskfile.yaml` copied from ApolloSync (including the `bump` task) with `ApolloSync` → `SunshineLibrary` rename. Base `HostClient` + `SunshineHostClient` + `ApolloHostClient`, `StreamClient` + `MoonlightClient`, one host in code, `GetGames` returns real apps with inline covers, `GetPlayActions` spawns Moonlight with auto-detected display. No settings UI. All user-facing strings already use `LOC_SunshineLibrary_*` keys in `en_US.xaml`.
2. **M2 — Multi-host + settings UI:** host list UI, DPAPI creds, cert pinning dialog, Test Connection button, Test Client button, parallel `GetGames`, per-host cache. Every new string added to `en_US.xaml`.
3. **M3 — Overrides:** global/host/game override stores, merge logic with unit tests, per-game override dialog, right-click menu, bulk multi-select editor.
4. **M4 — Polish & hardening:** `OnGameStopped` close, `TrackingMode.Process` end-to-end verification, sanity warnings, pseudo-app filter, host health indicator, `SettingsVersion` migration, README, manifest release flow.
5. **M4.5 — Translation pass:** copy locale XAML set from ApolloSync, translate all `LOC_SunshineLibrary_*` keys (machine-translate seed then human review), wire into csproj `<Content>`.
6. **M5 — Post-1.0:** Apollo-only surface (OTP pairing, client management, virtual-display UI), WoL, client profiles.

## 17. Review outcomes (see REVIEW.md)

A five-lens review produced [REVIEW.md](./REVIEW.md) with correctness bugs, .NET hazards, security deltas, and a ranked feature backlog. Key items folded into this plan already: tri-state resolution/fps/HDR with auto-detection, `GetPlayActions`/`PlayController` instead of baked-in `GameAction`, `TrackingMode.ProcessName`, SPKI cert pinning via instance `WebRequestHandler`, CSRF handling, pseudo-app filter, `IsInstalled` semantics. Apollo-specific features (OTP pairing, client display-mode control, WoL, bulk overrides, client profiles) tracked in REVIEW.md and slated per milestone.

## 18. Open questions

- Do we want a "shared app" mode (same app on multiple hosts → one Playnite entry with host picker) as a future setting, or keep strict "one entry per (host,app)"?
- Should we expose a "launch via Artemis" action in addition to Moonlight, or keep Moonlight-only v1?
- Auto-import on Playnite start — default on or off? (ApolloSync makes this configurable; match that pattern.)
- Exact cover-asset path per flavor — verify at implementation: Sunshine admin HTTP cover endpoint (historically `/appasset/{index}/box.png` pattern) and Apollo equivalent keyed by uuid.
