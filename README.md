# SunshineLibrary

Playnite library provider for [Sunshine](https://github.com/LizardByte/Sunshine) and [Apollo](https://github.com/ClassicOldSong/Apollo) streaming hosts. Imports apps from remote hosts as Playnite library entries and launches [Moonlight](https://moonlight-stream.org) to stream them.

Companion to [playnite-apollo-sync](https://github.com/sharkusmanch/playnite-apollo-sync) (which syncs in the opposite direction).

## Features

- Multi-host sync with parallel fan-out (up to 4 concurrent)
- Auto-detects Sunshine vs Apollo via `/api/config`
- Per-host SPKI certificate pinning with TOFU confirmation dialog
- DPAPI-encrypted admin passwords (never touch plain JSON)
- Cache fallback: offline hosts serve last-good apps tagged `offline` — library survives network blips
- Inline cover art from host
- Auto-detects client display (resolution / refresh / HDR) at launch
- Global → host → per-game override chain (tri-state `Inherit | Auto | Static`)
- Bulk per-game overrides via multi-select right-click
- Orphan reconciliation: app re-added to host → Playnite entry rematched by name, playtime preserved
- Optional orphan deletion (off by default — preserves history)
- Moonlight locator probes PATH / Scoop / Winget / standard installers
- Pre-launch sanity warnings (HDR + H.264, bitrate / fps range)
- `--quit-after` always passed; `TrackingMode.Process` for accurate playtime
- 19 localizations

## Apollo sync companion

[playnite-apollo-sync](https://github.com/sharkusmanch/playnite-apollo-sync) is the server-side complement to this plugin. Where this plugin pulls apps **from** an Apollo host into Playnite, playnite-apollo-sync pushes your Playnite library **back to** Apollo — so every game you own shows up as a streamable app on the host without manually adding it.

Together they form a bidirectional loop:

```
Apollo host  ──(SunshineLibrary)──▶  Playnite library
Apollo host  ◀──(playnite-apollo-sync)──  Playnite library
```

Key benefits when games are registered on the host via playnite-apollo-sync:

- **Stream exit on game close** — the host terminates the stream session when the game process exits. Combined with this plugin's `TrackingMode.Process` playtime tracking, Moonlight quits as soon as the game closes server-side, and Playnite records accurate playtime with no manual stream teardown needed.
- **Virtual display** (Apollo only) — Apollo auto-matches the client's resolution per-stream, so games stream at the correct resolution automatically rather than at the host's fixed desktop resolution.

## Apollo Profile Manager

[ApolloProfileManager](https://github.com/ClassicOldSong/ApolloProfileManager) runs on the Apollo host and swaps per-client profiles — save files, game configs, and mod sets — based on which Moonlight client is connecting.

When you launch a game from Playnite via this plugin, ApolloProfileManager ensures the correct profile is active for your specific device: each client gets its own independent saves and settings without any manual file juggling.

## Install

Download the latest `.pext` from [Releases](https://github.com/sharkusmanch/playnite-sunshine-library/releases) and open it, or install via Playnite's add-on browser.

## Setup

1. In Sunshine/Apollo, note the admin URL (default `https://<host>:47990`) and credentials.
2. Pair Moonlight with the host via Moonlight's own UI. (This plugin does not handle pairing.)
3. Playnite → Extensions → SunshineLibrary → Settings → **Hosts** → **Add host…**
   Fill in Label, Address, Port, Admin user/password.
4. **Test connection** — probes DNS → TCP → Certificate → PinMatch → ServerTypeDetect → ListApps with a progress overlay.
5. Confirm the SHA-256 fingerprint in the pin dialog. Verify it against `https://<host>:<port>/` in a browser before trusting.
6. Save — Playnite runs a library update and the apps appear.

## Settings

**Hosts tab** — DataGrid of configured hosts. Add / Edit / Remove. Columns: Label, Address, Port, Server type, Enabled.

**Streaming Client tab** — Moonlight executable path. **Browse…** for manual, **Auto-detect** to probe PATH / Scoop / Winget / standard installers. **Test streaming client** verifies the configured path.

**General tab** — NotificationMode (`Always` / `On changes only` / `Never` — security events always fire regardless), Auto-remove orphaned games toggle (off by default), Revoke all stored credentials button.

## Per-game overrides

Right-click a game:
- **Streaming settings…** (single) — override resolution, FPS, HDR, bitrate, codec, display mode, audio, YUV 4:4:4, frame pacing, game optimization, extra Moonlight args
- **Edit streaming settings for N games…** (multi-select) — bulk apply; tick only fields to push
- **Clear streaming overrides** — remove per-game overrides

Field states:
- **Inherit** — fall through to host / global / built-in
- **Auto** (Resolution / FPS / HDR only) — detect client display at launch
- **Static** — explicit value

Precedence: `builtin < global < host < per-game`.

## Streaming override precedence

Settings are resolved in four layers, each overriding the one below it:

| Layer | Where it's configured | Purpose |
|---|---|---|
| **Built-in defaults** | Hard-coded | Safe starting point — Auto for resolution/FPS/HDR, no bitrate/codec/extras |
| **Global overrides** | Settings → streaming tab | Apply to every game on every host unless overridden lower |
| **Host defaults** | Edit host → Streaming defaults | Apply to every game on that host only |
| **Per-game overrides** | Right-click game → Streaming settings | Apply to one game only |

**How fields are merged:** Each field is independently nullable. A `null` value means "inherit from the layer below." The first non-null value wins, scanning from the top layer downward. Setting a field back to `null` (Inherit) restores inherited behavior without affecting any other field.

**Resolution / FPS / HDR field states:**
- `Inherit` — null; falls through to the next layer
- `Auto` — detect client display at launch (resolution probed from Windows Display API, HDR from `AdvancedColorInfo`)
- `Static` — explicit value; the companion `*Static` field holds the literal string/value

**Bitrate:** If not explicitly overridden at any layer, the bitrate is auto-calculated from the effective streaming resolution × FPS using the same formula as Moonlight itself (`resolutionFactor × fpsFactor`, where fps ≤ 60 uses a linear curve and fps > 60 uses a sqrt curve). YUV 4:4:4 doubles the result. HDR does **not** change the bitrate formula. The calculated value is passed as `--bitrate` so Moonlight's OSD always shows the exact rate.

**Example cascade:**

```
Global:    FpsMode=Static, FpsStatic=60   (cap all games to 60fps)
Host ALLY: FpsMode=null (Inherit)          (inherits 60fps from global)
Game FFXVI: FpsMode=Static, FpsStatic=120 (override to 120fps for this game only)
```

The merged result for FFXVI on ALLY is 120fps. All other games on ALLY get 60fps from global. A game on a different host also gets 60fps.

**Debug logging:** Each launch emits two `Debug` lines to `extensions.log`:
1. Which layers are non-null (global / host defaults / per-game)
2. The full merged values and detected display info

Then a third line with the exact Moonlight command line being run.

## Main menu

**Extensions → SunshineLibrary**:
- **Resync all hosts** — immediate sync with progress dialog
- **Test streaming client** — probe Moonlight availability
- **Host status…** — reachability check across all hosts
- **Remove orphaned games…** — one-shot delete of confirmed orphans (with confirmation)
- **Clean up orphaned overrides** — remove per-game overrides whose games no longer exist

## Orphan behavior

When an app is removed from a host, or the host is removed from settings:

| Case | Default | With AutoRemoveOrphanedGames on |
|---|---|---|
| Host synced live, app gone | `IsInstalled = false` | Deleted + override wiped |
| Host removed from settings | `IsInstalled = false` | Deleted + override wiped |
| Host offline / auth broken | Left alone (cache fallback) | Left alone |

Reconciliation: if an app re-appears on the host (even with a new Apollo uuid or new Sunshine hash), the existing Playnite entry is re-matched by name — playtime and per-game overrides are preserved.

## Troubleshooting

**Test connection fails at Certificate** — host unreachable on `<port>`. Confirm the host is awake and Sunshine/Apollo is running.

**Test connection succeeds but sync shows "no pin stored"** — you cancelled the pin dialog. Edit the host, run Test again.

**Stream launches blurry** — Sunshine captures at the host's physical desktop resolution. Either match the client resolution on the host, or use Apollo with virtual-display integration (auto-matches client resolution).

**"Moonlight is not installed or the configured path is invalid"** — click **Auto-detect** on the Streaming Client tab or Browse manually. Probe order is PATH → Scoop user → Scoop global → Winget → Program Files → LocalAppData\Programs.

**"The certificate for {host} changed"** — Sunshine reinstalled, or someone is intercepting. Edit host, run Test, compare new fingerprint to the old one in the re-pin dialog.

**Games I excluded still show up** — exclude list is per-host (edit the host). Defaults: `Desktop`, `Steam Big Picture`, `Terminate`.

**Playtime inflates** — the plugin tracks the moonlight-qt process. If the host app is marked detached in Sunshine (Steam Big Picture on some configs), Moonlight stays open until manually closed.

**Scoop-installed Moonlight not detected** — fixed in 0.1.0; the locator probes `moonlight-qt.exe`, `Moonlight.exe`, and `moonlight.exe` under scoop user/global roots.

## Logs

`~/AppData/Roaming/Playnite/extensions.log` — plugin log entries prefixed with `[SunshineLibrary]` or a host label. Credential headers (Authorization / Cookie / CSRF) are redacted by `SafeLogging`.

## Building

Requires .NET Framework 4.6.2 SDK and [Task](https://taskfile.dev).

```
git clone https://github.com/sharkusmanch/playnite-sunshine-library.git
cd playnite-sunshine-library
task all           # clean + restore + format + test + build + pack + install
```

Individual targets: `task build`, `task test`, `task pack`, `task install`, `task logs` (tail extensions.log).

See [ARCHITECTURE.md](./ARCHITECTURE.md) for internal structure and extension points.

## License

[MIT](LICENSE)
