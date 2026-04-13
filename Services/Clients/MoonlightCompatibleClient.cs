using Playnite.SDK.Models;
using SunshineLibrary.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace SunshineLibrary.Services.Clients
{
    /// <summary>
    /// Shared base for moonlight-qt-lineage clients (Moonlight, Artemis, StreamLight,
    /// and any other fork that preserves the <c>stream &lt;host&gt; &lt;app&gt; --flag value …</c>
    /// CLI shape). Subclasses provide only identity + locator config; ProbeAvailability
    /// and BuildLaunch are handled here.
    ///
    /// Moonlight-incompatible clients (e.g. something that speaks a different protocol)
    /// should derive from <see cref="StreamClient"/> directly.
    /// </summary>
    public abstract class MoonlightCompatibleClient : StreamClient
    {
        /// <summary>Where to look for this client's executable (install dirs, scoop, winget).</summary>
        protected abstract ClientLocatorConfig LocatorConfig { get; }

        public override ClientAvailability ProbeAvailability(ClientSettings settings)
        {
            var explicitPath = settings?.GetPath(Id);
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                return ToAvailability(explicitPath);
            }

            var found = ClientLocator.Locate(LocatorConfig);
            if (found.Count > 0)
            {
                return ToAvailability(found[0]);
            }
            return new ClientAvailability
            {
                Installed = false,
                UnavailableReason = $"{DisplayName} not found in PATH, Scoop, Winget, or any standard install location.",
            };
        }

        public override ClientLaunchSpec BuildLaunch(
            HostConfig host,
            RemoteApp app,
            StreamOverrides merged,
            ClientDisplayInfo display,
            ClientSettings settings = null)
        {
            merged = StreamOverrides.BuiltinDefault.MergedWith(merged);
            var args = ComposeArgs(host, app, merged, display);

            var availability = ProbeAvailability(settings ?? new ClientSettings());
            var exe = availability.Installed ? availability.ExecutablePath : LocatorConfig.PrimaryExeName;
            var trackedName = Path.GetFileNameWithoutExtension(exe);

            return new ClientLaunchSpec
            {
                Executable = exe,
                Arguments = PasteArguments.Build(args),
                // Set working directory to the exe's own folder. Scoop-installed Moonlight
                // requires this — the Scoop shortcut sets "Start in" to the app directory
                // so Qt can find its bundled DLLs/plugins. Without it, the shim's directory
                // (~/scoop/shims/) is used and Moonlight fails to pair.
                WorkingDirectory = Path.GetDirectoryName(exe),
                TrackedProcessName = trackedName,
                TrackingMode = TrackingMode.Process,
            };
        }

        /// <summary>
        /// moonlight-qt-compatible argv composer. Lives here so every fork subclass
        /// gets identical flag-handling behavior (including clamping, auto-detection,
        /// and ExtraArgs sanitization) without duplicating the logic.
        /// </summary>
        internal static List<string> ComposeArgs(HostConfig host, RemoteApp app, StreamOverrides merged, ClientDisplayInfo display)
        {
            var args = new List<string> { "stream", host.Address, app.Name, "--quit-after" };

            // Determine effective streaming dimensions for bitrate auto-calculation.
            int effW = display.IsKnown ? display.Width : 0;
            int effH = display.IsKnown ? display.Height : 0;
            int effFps = display.IsKnown ? display.RefreshHz : 0;

            // Resolution: Auto uses detected display; Static emits as-is.
            if (merged.ResolutionMode == Models.ResolutionMode.Auto && display.IsKnown)
            {
                args.Add("--resolution");
                args.Add(display.AsResolution);
            }
            else if (merged.ResolutionMode == Models.ResolutionMode.Static && !string.IsNullOrWhiteSpace(merged.ResolutionStatic))
            {
                args.Add("--resolution");
                args.Add(merged.ResolutionStatic);
                if (TryParseResolution(merged.ResolutionStatic, out var sw, out var sh))
                {
                    effW = sw;
                    effH = sh;
                }
            }

            // FPS
            if (merged.FpsMode == Models.FpsMode.Auto && display.IsKnown && display.RefreshHz >= 10 && display.RefreshHz <= 480)
            {
                args.Add("--fps");
                args.Add(display.RefreshHz.ToString());
            }
            else if (merged.FpsMode == Models.FpsMode.Static && merged.FpsStatic.HasValue)
            {
                var v = Clamp(merged.FpsStatic.Value, 10, 480);
                args.Add("--fps");
                args.Add(v.ToString());
                effFps = merged.FpsStatic.Value;
            }

            // HDR
            if (merged.Hdr == HdrMode.Auto && display.IsKnown)
            {
                args.Add(display.HdrEnabled ? "--hdr" : "--no-hdr");
            }
            else if (merged.Hdr == HdrMode.On) args.Add("--hdr");
            else if (merged.Hdr == HdrMode.Off) args.Add("--no-hdr");

            // Bitrate: explicit override wins; otherwise auto-calculate from effective
            // resolution × fps (+ YUV 4:4:4 multiplier) so the value is predictable and
            // visible in Moonlight's OSD. HDR does not affect moonlight-qt's formula.
            // Falls back to Moonlight's own default only when display info is unavailable.
            bool effYuv444 = merged.Yuv444 == true;

            int? bitrateKbps = merged.BitrateKbps;
            if (!bitrateKbps.HasValue && effW > 0 && effH > 0 && effFps > 0)
            {
                bitrateKbps = Models.BitrateCalculator.Calculate(effW, effH, effFps, effYuv444);
            }
            if (bitrateKbps.HasValue)
            {
                var v = Clamp(bitrateKbps.Value, 500, 500_000);
                args.Add("--bitrate");
                args.Add(v.ToString());
            }
            if (!string.IsNullOrWhiteSpace(merged.VideoCodec))
            {
                args.Add("--video-codec");
                args.Add(merged.VideoCodec);
            }
            if (!string.IsNullOrWhiteSpace(merged.DisplayMode))
            {
                args.Add("--display-mode");
                args.Add(merged.DisplayMode);
            }
            if (!string.IsNullOrWhiteSpace(merged.AudioConfig))
            {
                args.Add("--audio-config");
                args.Add(merged.AudioConfig);
            }
            if (merged.Yuv444.HasValue) args.Add(merged.Yuv444.Value ? "--yuv444" : "--no-yuv444");
            if (merged.FramePacing.HasValue) args.Add(merged.FramePacing.Value ? "--frame-pacing" : "--no-frame-pacing");
            if (merged.GameOptimization.HasValue) args.Add(merged.GameOptimization.Value ? "--game-optimization" : "--no-game-optimization");
            if (merged.ShowStats.HasValue) args.Add(merged.ShowStats.Value ? "--show-stats" : "--no-show-stats");

            if (!string.IsNullOrWhiteSpace(merged.ExtraArgs))
            {
                // Sanitize: reject NUL/CR/LF which can't legitimately appear in a moonlight flag
                // and would break argv on some shells. Tabs are allowed as separators only.
                var cleaned = merged.ExtraArgs;
                if (cleaned.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
                {
                    var sb = new System.Text.StringBuilder(cleaned.Length);
                    foreach (var c in cleaned)
                    {
                        if (c != '\0' && c != '\r' && c != '\n') sb.Append(c);
                    }
                    cleaned = sb.ToString();
                }
                foreach (var tok in cleaned.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    args.Add(tok);
            }

            return args;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        internal static bool TryParseResolution(string s, out int w, out int h)
        {
            w = h = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var sep = s.IndexOfAny(new[] { 'x', 'X' });
            return sep > 0
                && int.TryParse(s.Substring(0, sep).Trim(), out w)
                && int.TryParse(s.Substring(sep + 1).Trim(), out h)
                && w > 0 && h > 0;
        }

        private static ClientAvailability ToAvailability(string path)
        {
            if (File.Exists(path))
            {
                return new ClientAvailability
                {
                    Installed = true,
                    ExecutablePath = path,
                };
            }
            return new ClientAvailability
            {
                Installed = false,
                UnavailableReason = $"Client executable not found at {path}.",
            };
        }
    }
}
