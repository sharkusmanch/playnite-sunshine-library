using Playnite.SDK;
using SunshineLibrary.Models;
using SunshineLibrary.Services.Clients;
using System.Collections.Generic;

namespace SunshineLibrary.Settings
{
    /// <summary>
    /// Builds the per-field provenance list shown in <see cref="EffectiveSettingsWindow"/>.
    /// Operates on the raw per-layer <see cref="StreamOverrides"/> objects (not the pre-merged
    /// result) so it can attribute each resolved value to the correct layer.
    /// </summary>
    internal static class EffectiveSettingsHelper
    {
        // Conservative FPS assumption used when static resolution is known but FPS
        // resolves to Auto with an unavailable display. Matches Moonlight's common default.
        private const int AutoFpsFallback = 60;

        /// <param name="builtin">Hard-coded defaults (Auto for res/fps/HDR).</param>
        /// <param name="global">Plugin-wide global overrides (may be null).</param>
        /// <param name="hostDefaults">Per-host streaming defaults (may be null).</param>
        /// <param name="perGame">Per-game overrides (may be null when none stored).</param>
        /// <param name="merged">Already-merged result: <c>builtin.MergedWith(global).MergedWith(hostDefaults).MergedWith(perGame)</c>.
        /// Used for Auto runtime expansion and bitrate auto-calculation.</param>
        /// <param name="display">Client display snapshot; used to expand Auto values.</param>
        internal static List<FieldProvenance> BuildProvenanceList(
            StreamOverrides builtin,
            StreamOverrides global,
            StreamOverrides hostDefaults,
            StreamOverrides perGame,
            StreamOverrides merged,
            ClientDisplayInfo display)
        {
            var g = global;
            var h = hostDefaults;
            var pg = perGame;

            var list = new List<FieldProvenance>();

            // ── Display ──────────────────────────────────────────────────────────
            list.Add(Section("LOC_SunshineLibrary_OverrideDialog_Group_Display"));

            // Resolution
            {
                var src = GetSourceEnum(pg?.ResolutionMode, h?.ResolutionMode, g?.ResolutionMode);
                string val = FormatResMode(merged.ResolutionMode, merged.ResolutionStatic);
                string note = null;
                if (merged.ResolutionMode == ResolutionMode.Auto)
                {
                    note = display.IsKnown
                        ? string.Format(L("LOC_SunshineLibrary_EffectiveSettings_AutoFromDisplay"), display.AsResolution)
                        : L("LOC_SunshineLibrary_EffectiveSettings_AutoUnavailable");
                }
                list.Add(Field("LOC_SunshineLibrary_OverrideField_Resolution", val, src, note));
            }

            // FPS
            {
                var src = GetSourceEnum(pg?.FpsMode, h?.FpsMode, g?.FpsMode);
                string val = FormatFpsMode(merged.FpsMode, merged.FpsStatic);
                string note = null;
                if (merged.FpsMode == FpsMode.Auto)
                {
                    note = display.IsKnown
                        ? string.Format(L("LOC_SunshineLibrary_EffectiveSettings_AutoFromDisplay"), display.RefreshHz + " Hz")
                        : L("LOC_SunshineLibrary_EffectiveSettings_AutoUnavailable");
                }
                list.Add(Field("LOC_SunshineLibrary_OverrideField_Fps", val, src, note));
            }

            // HDR
            {
                var src = GetSourceEnum(pg?.Hdr, h?.Hdr, g?.Hdr);
                string val = FormatHdr(merged.Hdr);
                string note = null;
                if (merged.Hdr == HdrMode.Auto)
                {
                    note = display.IsKnown
                        ? string.Format(L("LOC_SunshineLibrary_EffectiveSettings_AutoFromDisplay"), display.HdrEnabled ? "On" : "Off")
                        : L("LOC_SunshineLibrary_EffectiveSettings_AutoUnavailable");
                }
                list.Add(Field("LOC_SunshineLibrary_OverrideField_Hdr", val, src, note));
            }

            // ── Encoding ─────────────────────────────────────────────────────────
            list.Add(Section("LOC_SunshineLibrary_OverrideDialog_Group_Encoding"));

            // Bitrate (special-cased: auto-calculated when no layer sets it)
            {
                var src = GetSourceNullableInt(pg?.BitrateKbps, h?.BitrateKbps, g?.BitrateKbps);
                string val, note;
                if (merged.BitrateKbps.HasValue)
                {
                    val = string.Format("{0} Kbps", merged.BitrateKbps.Value);
                    note = null;
                }
                else
                {
                    val = L("LOC_SunshineLibrary_EffectiveSettings_BitrateAuto");

                    // Mirror ComposeArgs: resolve effective width/height/fps for auto-calc.
                    int effW = 0, effH = 0, effFps = 0;
                    if (merged.ResolutionMode == ResolutionMode.Auto && display.IsKnown)
                    {
                        effW = display.Width;
                        effH = display.Height;
                    }
                    else if (merged.ResolutionMode == ResolutionMode.Static)
                    {
                        MoonlightCompatibleClient.TryParseResolution(merged.ResolutionStatic, out effW, out effH);
                    }

                    if (merged.FpsMode == FpsMode.Auto && display.IsKnown)
                        effFps = display.RefreshHz;
                    else if (merged.FpsMode == FpsMode.Static && merged.FpsStatic.HasValue)
                        effFps = merged.FpsStatic.Value;

                    if (effW > 0 && effH > 0 && effFps > 0)
                    {
                        bool yuv = merged.Yuv444 == true;
                        int calc = BitrateCalculator.Calculate(effW, effH, effFps, yuv);
                        note = string.Format(L("LOC_SunshineLibrary_EffectiveSettings_BitrateAutoCalc"), calc);
                    }
                    else if (effW > 0 && effH > 0)
                    {
                        // Resolution is statically known but FPS is Auto with an unavailable display.
                        // Use AutoFpsFallback for a rough estimate rather than showing nothing.
                        bool yuv = merged.Yuv444 == true;
                        int calc = BitrateCalculator.Calculate(effW, effH, AutoFpsFallback, yuv);
                        note = string.Format(L("LOC_SunshineLibrary_EffectiveSettings_BitrateAutoCalcApprox"), calc);
                    }
                    else
                    {
                        note = L("LOC_SunshineLibrary_EffectiveSettings_AutoUnavailable");
                    }
                }
                list.Add(Field("LOC_SunshineLibrary_OverrideField_Bitrate", val, src, note));
            }

            list.Add(Field("LOC_SunshineLibrary_OverrideField_VideoCodec",
                merged.VideoCodec ?? Dash(), GetSourceStr(pg?.VideoCodec, h?.VideoCodec, g?.VideoCodec)));
            list.Add(Field("LOC_SunshineLibrary_OverrideField_VideoDecoder",
                merged.VideoDecoder ?? Dash(), GetSourceStr(pg?.VideoDecoder, h?.VideoDecoder, g?.VideoDecoder)));
            list.Add(Field("LOC_SunshineLibrary_OverrideField_Yuv444",
                FormatBool(merged.Yuv444), GetSourceEnum(pg?.Yuv444, h?.Yuv444, g?.Yuv444)));

            // ── Performance ───────────────────────────────────────────────────────
            list.Add(Section("LOC_SunshineLibrary_OverrideDialog_Group_Performance"));
            list.Add(Field("LOC_SunshineLibrary_OverrideField_VSync",
                FormatBool(merged.VSync), GetSourceEnum(pg?.VSync, h?.VSync, g?.VSync)));
            list.Add(Field("LOC_SunshineLibrary_OverrideField_FramePacing",
                FormatBool(merged.FramePacing), GetSourceEnum(pg?.FramePacing, h?.FramePacing, g?.FramePacing)));
            list.Add(Field("LOC_SunshineLibrary_OverrideField_GameOptimization",
                FormatBool(merged.GameOptimization), GetSourceEnum(pg?.GameOptimization, h?.GameOptimization, g?.GameOptimization)));
            list.Add(Field("LOC_SunshineLibrary_OverrideField_ShowStats",
                FormatBool(merged.PerformanceOverlay), GetSourceEnum(pg?.PerformanceOverlay, h?.PerformanceOverlay, g?.PerformanceOverlay)));

            // ── Output ────────────────────────────────────────────────────────────
            list.Add(Section("LOC_SunshineLibrary_OverrideDialog_Group_Output"));
            list.Add(Field("LOC_SunshineLibrary_OverrideField_DisplayMode",
                merged.DisplayMode ?? Dash(), GetSourceStr(pg?.DisplayMode, h?.DisplayMode, g?.DisplayMode)));
            list.Add(Field("LOC_SunshineLibrary_OverrideField_AudioConfig",
                merged.AudioConfig ?? Dash(), GetSourceStr(pg?.AudioConfig, h?.AudioConfig, g?.AudioConfig)));
            list.Add(Field("LOC_SunshineLibrary_OverrideField_AudioOnHost",
                FormatBool(merged.AudioOnHost), GetSourceEnum(pg?.AudioOnHost, h?.AudioOnHost, g?.AudioOnHost)));

            // ── Session ───────────────────────────────────────────────────────────
            list.Add(Section("LOC_SunshineLibrary_OverrideDialog_Group_Session"));
            list.Add(Field("LOC_SunshineLibrary_OverrideField_MuteOnFocusLoss",
                FormatBool(merged.MuteOnFocusLoss), GetSourceEnum(pg?.MuteOnFocusLoss, h?.MuteOnFocusLoss, g?.MuteOnFocusLoss)));
            list.Add(Field("LOC_SunshineLibrary_OverrideField_KeepAwake",
                FormatBool(merged.KeepAwake), GetSourceEnum(pg?.KeepAwake, h?.KeepAwake, g?.KeepAwake)));
            list.Add(Field("LOC_SunshineLibrary_OverrideField_CaptureSystemKeys",
                merged.CaptureSystemKeys ?? Dash(), GetSourceStr(pg?.CaptureSystemKeys, h?.CaptureSystemKeys, g?.CaptureSystemKeys)));

            // ── Advanced ──────────────────────────────────────────────────────────
            list.Add(Section("LOC_SunshineLibrary_OverrideDialog_Group_Advanced"));
            list.Add(Field("LOC_SunshineLibrary_OverrideField_ExtraArgs",
                string.IsNullOrEmpty(merged.ExtraArgs) ? L("LOC_SunshineLibrary_EffectiveSettings_None") : merged.ExtraArgs,
                GetSourceStr(pg?.ExtraArgs, h?.ExtraArgs, g?.ExtraArgs)));

            return list;
        }

        // ── Provenance helpers ────────────────────────────────────────────────────

        private static OverrideSource GetSourceEnum<T>(T? pg, T? h, T? g) where T : struct
        {
            if (pg.HasValue) return OverrideSource.PerGame;
            if (h.HasValue) return OverrideSource.Host;
            if (g.HasValue) return OverrideSource.Global;
            return OverrideSource.BuiltIn;
        }

        private static OverrideSource GetSourceNullableInt(int? pg, int? h, int? g)
        {
            if (pg.HasValue) return OverrideSource.PerGame;
            if (h.HasValue) return OverrideSource.Host;
            if (g.HasValue) return OverrideSource.Global;
            return OverrideSource.BuiltIn;
        }

        private static OverrideSource GetSourceStr(string pg, string h, string g)
        {
            if (!string.IsNullOrEmpty(pg)) return OverrideSource.PerGame;
            if (!string.IsNullOrEmpty(h)) return OverrideSource.Host;
            if (!string.IsNullOrEmpty(g)) return OverrideSource.Global;
            return OverrideSource.BuiltIn;
        }

        // ── Value formatting helpers ──────────────────────────────────────────────

        private static string FormatResMode(ResolutionMode? mode, string staticVal)
        {
            if (mode == ResolutionMode.Auto) return L("LOC_SunshineLibrary_Override_Mode_Auto");
            if (mode == ResolutionMode.Static && !string.IsNullOrEmpty(staticVal)) return staticVal;
            return Dash();
        }

        private static string FormatFpsMode(FpsMode? mode, int? staticVal)
        {
            if (mode == FpsMode.Auto) return L("LOC_SunshineLibrary_Override_Mode_Auto");
            if (mode == FpsMode.Static && staticVal.HasValue) return staticVal.Value.ToString();
            return Dash();
        }

        private static string FormatHdr(HdrMode? mode)
        {
            switch (mode)
            {
                case HdrMode.Auto: return L("LOC_SunshineLibrary_Override_Mode_Auto");
                case HdrMode.On: return L("LOC_SunshineLibrary_Override_Hdr_On");
                case HdrMode.Off: return L("LOC_SunshineLibrary_Override_Hdr_Off");
                default: return Dash();
            }
        }

        private static string FormatBool(bool? val)
        {
            if (val == true) return "On";
            if (val == false) return "Off";
            return Dash();
        }

        private static string Dash() => "—";

        // ── List-entry constructors ───────────────────────────────────────────────

        private static FieldProvenance Section(string key)
            => new FieldProvenance { IsSection = true, Label = L(key) };

        private static FieldProvenance Field(string labelKey, string val, OverrideSource src, string note = null)
            => new FieldProvenance { Label = L(labelKey), ResolvedValue = val, Source = src, RuntimeNote = note };

        private static string L(string key)
        {
            var s = ResourceProvider.GetString(key);
            return string.IsNullOrEmpty(s) ? key : s;
        }
    }
}
