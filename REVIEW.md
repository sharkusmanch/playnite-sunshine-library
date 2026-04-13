# SunshineLibrary Plan Review

Five-lens review of [PLAN.md](./PLAN.md). Each lens returned findings in a fixed format; this file consolidates them and proposes plan deltas.

## Reviewers
1. **Playnite SDK correctness** — API usage, ecosystem fit, ApolloSync style consistency
2. **Sunshine/Apollo/Moonlight domain** — protocol accuracy, CLI flags, Apollo-specific opportunities
3. **Security** — credential storage, cert pinning, command injection, log leakage
4. **.NET 4.6.2 / Windows engineering** — HttpClient, TLS, arg quoting, threading
5. **End-user UX & product** — onboarding, daily use, failure states, polish

---

## Correctness bugs in the plan (must fix before coding)

| # | Issue | Fix |
|---|-------|-----|
| C1 | Plan says `uuid` is on Sunshine apps. It's **Apollo-only**; upstream Sunshine identifies apps by **array index**, which is unstable under reorder. | `GameId = host.Id + ":" + (app.uuid ?? sha256(name + "\0" + cmd))`. Index is never part of the id. |
| C2 | Plan uses `HttpClientHandler.ServerCertificateCustomValidationCallback`. **That property does not exist on .NET Framework 4.6.2** — it was added in 4.7.1. | Subclass `WebRequestHandler`, set **instance-level** `ServerCertificateValidationCallback`. Never touch `ServicePointManager.ServerCertificateValidationCallback` (process-global; would compromise other plugins). |
| C3 | Plan bakes a static `GameAction` into `GameMetadata` at sync time. Playnite persists these; per-game override changes would require rewriting the DB. | Implement `GetPlayActions(GetPlayActionsArgs)` returning a custom `PlayController` (or `AutomaticPlayController`) that composes the arg list **at launch time** from the current override state. |
| C4 | `TrackingMode.Process` on `moonlight-qt` is fragile; if Playnite closes mid-stream or moonlight detaches a helper, playtime skews. | Use `TrackingMode.ProcessName` with `"moonlight-qt"`, document that `--quit-after` is mandatory (not an override), remove `QuitAfter` from `StreamOverrides`. |
| C5 | `OnGameStopped` fires for **every** Playnite game. Plan doesn't guard. | `if (args.Game.PluginId != Id) return;` at the top. Same for any lifecycle hook. |
| C6 | Plan never sets `IsInstalled = true` on `GameMetadata`. Default Playnite filter hides uninstalled games — users would see an empty library. | Set `IsInstalled = true` on every successful (fresh or cached) sync. Treat cert-mismatch / auth-broken / unreachable-with-no-cache as uninstalled. |
| C7 | Cert pin: plan doesn't say *what* is pinned (leaf vs SPKI vs full chain). Leaf pinning breaks on cert renewal. | Pin **SPKI SHA-256**. Document rotation path. |
| C8 | net462 default `SecurityProtocol` may not include TLS 1.2. | Set `ServicePointManager.SecurityProtocol = Tls12 | Tls13` (numeric cast `(SecurityProtocolType)3072 | (SecurityProtocolType)12288` for Tls13 since the enum isn't on 4.6.2). |
| C9 | On Tailscale MagicDNS / ZeroTier, .NET will raise `RemoteCertificateNameMismatch` even with the right pinned cert. | In the instance callback, accept if `SslPolicyErrors == RemoteCertificateNameMismatch` AND the SPKI fingerprint matches. Pin is the trust boundary, not the hostname. |
| C10 | Sunshine now requires **CSRF tokens** on mutating endpoints. Plan's `/api/apps/close` POST will fail on current builds. | Before any POST, `POST /api/csrf-token`, stash it, add it as a header on subsequent POSTs. |
| C11 | Pseudo-apps (`Desktop`, `Steam Big Picture`, `Terminate`) are auto-added by Sunshine and most users don't want them in Playnite. | Default-on filter that hides these by name; user-facing allowlist to unhide. |
| C12 | `Source = host.Label` — label is user-editable, renaming orphans the Source filter. | Keep a normalized stable Source derived from `host.Id` (e.g., `"Sunshine: {label}"` but keyed internally by id with a rename migration). |

---

## .NET / Windows engineering deltas

| # | Issue | Fix |
|---|-------|-----|
| N1 | `HttpClient` reuse is right, but net462 caches DNS forever in `ServicePoint`. | `ServicePointManager.FindServicePoint(uri).ConnectionLeaseTimeout = 5 * 60 * 1000` per host. |
| N2 | Default `ServicePointManager.DefaultConnectionLimit = 2`. | Bump to `max(hosts * 2, 8)` at plugin init. |
| N3 | `ProcessStartInfo.ArgumentList` is net5+. net462 has only `Arguments` (single string). | Implement CoreFX `PasteArguments` algorithm (backslash doubling, quote doubling, wrap-if-needed). Unit-test with space, `"`, trailing `\`, `\\"`, unicode, empty. |
| N4 | JSON inherit-semantics: `null` vs missing vs `false`. | `JsonSerializerSettings { NullValueHandling = Include, DefaultValueHandling = Include, MissingMemberHandling = Ignore }`. Round-trip test required. |
| N5 | Dispatcher hazard: `GetGames` runs on a background thread; surfacing a cert-pin dialog from inside it is a bug. | Cert-pin confirmation only happens from the **settings / Add-Host** flow, never from `GetGames`. `GetGames` on a new/unpinned host returns empty + notification. |
| N6 | Cancellation: `LibraryGetGamesArgs.CancelToken` is never threaded in the plan. | Pass through `HostClient` → `HttpClient.SendAsync` → `SemaphoreSlim.WaitAsync`. |
| N7 | DPAPI blobs don't roam. If Playnite settings sync via OneDrive/portable mode, decrypt fails silently. | Catch `CryptographicException`, notify, re-prompt for password; document "passwords are machine-local." |
| N8 | DPAPI `optionalEntropy`. | `optionalEntropy = SHA256(hostId.ToString())` so stolen settings can't have individual host creds unwrapped out of context by another same-user process. |
| N9 | ApolloSync references `PlayniteSDK 6.12.0` in `manifest.yaml` but has `PlayniteSDK.6.2.0` in `packages/` on disk. | Resolve in ApolloSync first; don't inherit the ambiguity. |
| N10 | `extension.yaml`, `manifest.yaml`, `icon.png`, `Localization/*.xaml` must be `<Content CopyToOutputDirectory="PreserveNewest">` for Toolbox pack to find them. | Already handled in ApolloSync.csproj; copy verbatim. |

---

## Security deltas

| # | Issue | Fix |
|---|-------|-----|
| S1 | `ExtraArgs` as a free-form escape hatch is trivial CLI injection into `moonlight-qt.exe` (not a network attack, but a tampered-settings-file attack). | Document as "trusted-input only"; sanitize against control chars and shell metacharacters; consider gating behind an "advanced mode" toggle off by default. |
| S2 | `MoonlightPath` user-provided, unvalidated. | Require the file exists, check Authenticode signature via `WinVerifyTrust` against the known Moonlight publisher, cache thumbprint, re-verify on change. |
| S3 | JSON response DOS (2 GB nested arrays). | `JsonTextReader.MaxDepth = 32`; stream-read with a hard byte cap (e.g., 4 MB is plenty for `/api/apps`). |
| S4 | Cache JSON tampering → injected `GameAction` at next launch. | Sign cache with HMAC over a key derived via DPAPI. On signature fail, discard, re-fetch. |
| S5 | `Address` field could contain `://`, spaces, shell chars. | Validate: hostname/IPv4/IPv6 literal only, no scheme, no path. Build URL internally. |
| S6 | Basic Auth header at Debug log level leaks password. | `HttpClient` handler must strip `Authorization` and `Set-Cookie` from any log sink the plugin owns. |
| S7 | No settings schema version. Future migrations silent-fail. | Add `SettingsVersion` int; refuse to load unknown versions. |
| S8 | No "revoke all credentials" button. | One-click DPAPI wipe + pin reset in settings. |

---

## Missing features (ranked by impact × effort)

### Tier 1 — significant user impact, moderate effort
1. **Apollo `/api/otp` pairing flow.** Plugin requests OTP from host, shows code; user pairs Moonlight remotely without host-side keyboard. Upstream Sunshine has no equivalent (falls back to the existing out-of-band pairing).
2. **Apollo `/api/clients/list` + `/api/clients/update` UI.** Per-client `always_use_virtual_display`, per-client `display_mode`. Fixes the #1 "why is it blurry" complaint (capture resolution ≠ client request without virtual display).
3. **Client-side profiles (Handheld / TV / Laptop).** Override merge becomes `global < host < client < game`. Fix the "same game, different profile per couch vs handheld" case. Client profile chosen at launch (remembered per Playnite install as "current profile").
4. **WoL magic-packet sender with per-host MAC.** Plan §11 currently yields from cache on offline; instead try WoL + 20–30s retry, then cache. Dominant daily-use win for handheld users.
5. **Bulk per-game override editor.** Multi-select in Playnite grid → "Set streaming settings for selection…". Users with 20+ games on a handheld cannot realistically click through them one by one.

### Tier 2 — meaningful, small effort
6. **Pseudo-app filter with allowlist** (correctness fix C11, surfaced as a user-visible feature).
7. **"Kill stuck session" main-menu action:** `POST /api/apps/close` + Apollo `/api/clients/disconnect`. Common recovery without opening the web UI.
8. **Pre-launch validity warnings in `OnGameStarting`:** HDR + H.264 (silently downgrades), AV1 @ 4K on old decoders, bitrate out of `500..500000` Kbps, packet-size vs MTU. Prevents most "stream looks wrong" reports.
9. **Host health indicator** in Source/filter row: online / cached / auth-broken / cert-mismatch. Lightweight `/api/config` background poll, no CSRF needed (GET).
10. **Sunshine vs Apollo feature detection** on first connect; settings UI greys out Apollo-only controls instead of silently 404ing.

### Tier 3 — nice-to-have, larger or speculative
11. **"Play with…" submenu** — one-shot override (not persisted) for a single launch.
12. **Sync-on-focus / background periodic sync** — not just manual Resync.
13. **Orphan override cleanup** — when an app disappears upstream, surface its overrides in a "pruneable" list.
14. **Import from Moonlight's paired-hosts list** on first run (Moonlight stores paired hosts on disk — pre-populates Address + MAC).
15. **Optional `LibraryMetadataProvider`** — cover art from Apollo uploaded-cover store (if a GET exists; otherwise IGDB is already fine).
16. **Artemis / embedded-client support** — answer the open question, probably v1.1+.

---

## Deltas to ApolloSync house style (must mirror)

- Generate plugin GUID now; bake into `SunshineLibrary.cs`, `extension.yaml`, `manifest.yaml` (ApolloSync: `f987343d-4168-4f44-9fb0-e3a21da314ad`).
- `ISettings` editing-clone pattern (`BeginEdit / EndEdit / CancelEdit / VerifySettings`) — ApolloSync does this correctly.
- Reuse `NotificationMode` enum values (`Always | OnUpdateOnly | Never`) so users see consistent behavior across both plugins.
- Copy `Taskfile.yaml` verbatim including the `bump` task that generates `manifest.yaml` changelog from `git log`.
- `ResourceProvider.GetString("LOC_…")` for every user-visible string from day one.

---

## Open questions after review

- **PlayniteSDK version pin**: resolve ApolloSync's 6.2.0-on-disk vs 6.12.0-declared before forking.
- **`HasCustomizedGameImport`**: decide yes/no. Yes → progress bar + cancel, but must implement `ImportGames` instead of `GetGames`. Recommended for N hosts.
- **Covers**: default to IGDB v1; Apollo uploaded-cover support if an endpoint is confirmed.
- **Game-details panel injection**: PlayniteSDK 6.12 support for injecting a "Streaming" section — needs verification before committing to the UX.
- **Fullscreen mode**: ship desktop-mode-only v1 or make the per-game dialog controller-navigable from the start.

---

## Ecosystem expansion backlog (researched 2026-04-13)

Two background scouting agents surveyed alternative streaming servers and clients. Findings logged here as the post-1.0 backlog; M5 priority order included.

### Servers — new candidates worth implementing

**Tier 2 — Vibepollo** ([Nonary/Vibepollo](https://github.com/Nonary/Vibepollo))
- Actively maintained Apollo fork (87 releases, v1.15.1 stable on 2026-04-12)
- Adds scoped API tokens, NIC/encoding extensions, first-class Playnite-side integration on the host
- Mapping: thin `VibepolloHostClient : ApolloHostClient` — likely overrides only `ServerType` and (eventually) token-scope auth
- **Detection caveat:** Vibepollo inherits Apollo's `vdisplayStatus` marker. Probe order in `HostClientFactory.ProbeServerTypeAsync` must become **Vibepollo → Apollo → Sunshine**. Unique discriminator key in `/api/config` UNVERIFIED — needs source read of `src/confighttp.cpp::getConfig`. Candidates: `video_max_batch_size_kb`, `nvenc_split_encode`, or a Vibepollo-specific version field.
- Effort: small

**Tier 3 — Foundation-Sunshine** ([qiin2333/Sunshine-Foundation](https://github.com/qiin2333/Sunshine-Foundation))
- Independent Sunshine fork with HDR full-chain + virtual display work; Sunshine HTTP intact
- Smaller user base than Apollo; significant in CN streaming community
- Mapping: probably no subclass needed — just a `ServerType.SunshineFoundation` label sharing `SunshineHostClient`
- Effort: tiny

### Servers — skip permanently

| Server | Reason |
|---|---|
| NVIDIA GameStream | Shut down Q1 2023, no API |
| Steam Remote Play | Valve-proprietary, no remote app-list API; in-process Steamworks IPC only |
| Rainway | Service shut down Oct 2022 |
| AMD Link | EOL Jan 2024 |
| Parsec | Admin Teams API exists but no per-host app-enumeration/launch surface |
| Apollo community re-uploads (hpqqph, Zelos0, sarinex, kjlcl, bhardwajRahul) | Near-vanilla forks; existing `ApolloHostClient` covers them |

### Clients — new candidates worth implementing

**Tier 1 — Artemis Qt** ([wjbeckett/artemis](https://github.com/wjbeckett/artemis))
- Apollo's recommended desktop client; moonlight-qt hard-fork that preserves the `stream <host> <app>` CLI surface (verified via `app/main.cpp::GlobalCommandLineParser`)
- Active dev (3,167 commits on develop, last build 0.6.7-dev.20250831). No stable release yet — dev-only Windows MSI/portable ZIP including ARM64
- Binary name UNVERIFIED — likely `artemis` or `artemis-qt`
- Mapping: with the locator/argv refactor below, becomes ~20 lines (`Id`, `DisplayName`, exe-name constant, locator pattern list)
- Effort: small after refactor

**Tier 3 — StreamLight** ([FoggyBytes/StreamLight](https://github.com/FoggyBytes/StreamLight))
- Independent moonlight-qt fork with NIC control + overlay metrics
- Smaller user base than Artemis; would also benefit from refactor below
- Effort: tiny once Artemis lands

### Clients — skip permanently

| Client | Reason |
|---|---|
| Moonlight Embedded | Linux/BSD/embedded only, no Windows build (latest v2.7.1 2025-11-30) |
| Moonlight Chrome | Browser extension, no programmatic launch path; upstream maintenance mode |
| moonlight-android / moonlight-ios / Artemis Android | Mobile-only |
| NVIDIA app / GeForce NOW | Proprietary cloud, no Sunshine compat, no scriptable host CLI |
| Parsec client | Speaks Parsec's protocol against Parsec hosts, not Sunshine/Moonlight |
| Steam Link / `streaming_client.exe` | Valve Remote Play protocol, not Sunshine |

### Cross-cutting refactors needed before adding multi-client support

These are quality-of-life improvements that don't block any one client but make adding several clean:

1. **`MoonlightLocator` parametrize** by `exeName` + install-dir name patterns (`"Artemis*"`, `"StreamLight*"`). Currently hardcoded to `"moonlight-qt.exe"`.
2. **`ClientLaunchSpec.TrackedProcessName`** — derive from `Path.GetFileNameWithoutExtension(availability.ExecutablePath)` in the base class, not a hardcoded literal. Artemis renames the binary; the literal `"moonlight-qt"` would mis-track.
3. **`MoonlightCompatibleClient` base class** — lift `ComposeArgs` to a protected helper. Artemis, StreamLight, and `qiin2333/moonlight-qt` will all want the same argv. Each subclass becomes ~20 lines.
4. **`ClientSettings.MoonlightPath` → `Dictionary<string, string> ClientPaths`** keyed by client `Id`. Scales as forks multiply; each entry is the resolved exe path for that specific client.
5. **`StreamClientRegistry`** — already in place. Once subclasses exist, settings UI gains an "Active client" picker.

### Recommended M5 implementation order

1. **Vibepollo `HostClient`** — small, biggest user-impact server win post-Apollo
2. **Locator/launch refactors (1, 2, 3, 4 above)** — prerequisites for clean multi-client
3. **Artemis Qt `StreamClient`** — Apollo ecosystem's preferred client
4. **Foundation-Sunshine label** — trivial follow-up
5. **StreamLight client** — trivial follow-up

Skip widening the `HostClient` abstraction (e.g., for OAuth, mDNS+NVHTTP) until a second non-Sunshine-lineage candidate emerges. None currently are.
