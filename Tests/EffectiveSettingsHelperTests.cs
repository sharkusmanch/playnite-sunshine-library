using Microsoft.VisualStudio.TestTools.UnitTesting;
using SunshineLibrary.Models;
using SunshineLibrary.Settings;
using System.Collections.Generic;

namespace SunshineLibrary.Tests
{
    /// <summary>
    /// Tests for <see cref="EffectiveSettingsHelper.BuildProvenanceList"/>:
    /// per-field source attribution and bitrate auto-calc note variants.
    ///
    /// <see cref="Playnite.SDK.ResourceProvider.GetString"/> wraps missing keys as "!KEY!" in
    /// test context. Since <c>string.Format("!KEY!", value)</c> has no format specifier, the
    /// format string is returned as-is. RuntimeNote values are therefore compared against the
    /// "!KEY!" strings rather than translated text.
    /// </summary>
    [TestClass]
    public class EffectiveSettingsHelperTests
    {
        // ─── List entry indices (must match BuildProvenanceList construction order) ───
        // Sections count as entries; non-section field positions are listed below.
        private const int IdxResolution   = 1;
        private const int IdxFps          = 2;
        private const int IdxHdr          = 3;
        // idx 4 = Section Encoding
        private const int IdxBitrate      = 5;
        private const int IdxVideoCodec   = 6;
        private const int IdxVideoDecoder = 7;
        private const int IdxYuv444       = 8;

        // ─── Expected RuntimeNote values in test context (ResourceProvider wraps missing keys) ─
        private const string NoteCalc    = "<!LOC_SunshineLibrary_EffectiveSettings_BitrateAutoCalc!>";
        private const string NoteApprox  = "<!LOC_SunshineLibrary_EffectiveSettings_BitrateAutoCalcApprox!>";
        private const string NoteUnavail = "<!LOC_SunshineLibrary_EffectiveSettings_AutoUnavailable!>";

        // ─── Helper ────────────────────────────────────────────────────────────────

        private static List<FieldProvenance> Build(
            StreamOverrides global = null,
            StreamOverrides host = null,
            StreamOverrides perGame = null,
            ClientDisplayInfo display = null)
        {
            var builtin = StreamOverrides.BuiltinDefault;
            var merged = builtin.MergedWith(global).MergedWith(host).MergedWith(perGame);
            return EffectiveSettingsHelper.BuildProvenanceList(
                builtin, global, host, perGame, merged,
                display ?? ClientDisplayInfo.Unknown);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // 1. Source attribution — nullable enum fields
        // ══════════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void Source_AllLayersNull_ReturnsBuiltIn()
        {
            var list = Build();
            Assert.AreEqual(OverrideSource.BuiltIn, list[IdxResolution].Source);
            Assert.AreEqual(OverrideSource.BuiltIn, list[IdxFps].Source);
            Assert.AreEqual(OverrideSource.BuiltIn, list[IdxHdr].Source);
        }

        [TestMethod]
        public void Source_GlobalSetsResolution_ReturnsGlobal()
        {
            var list = Build(global: new StreamOverrides { ResolutionMode = ResolutionMode.Static });
            Assert.AreEqual(OverrideSource.Global, list[IdxResolution].Source);
        }

        [TestMethod]
        public void Source_HostSetsResolution_ReturnsHost()
        {
            var list = Build(host: new StreamOverrides { ResolutionMode = ResolutionMode.Static });
            Assert.AreEqual(OverrideSource.Host, list[IdxResolution].Source);
        }

        [TestMethod]
        public void Source_PerGameSetsResolution_ReturnsPerGame()
        {
            var list = Build(perGame: new StreamOverrides { ResolutionMode = ResolutionMode.Static });
            Assert.AreEqual(OverrideSource.PerGame, list[IdxResolution].Source);
        }

        [TestMethod]
        public void Source_PerGameWinsOverHost_ForEnumField()
        {
            var list = Build(
                host:    new StreamOverrides { FpsMode = FpsMode.Static, FpsStatic = 60  },
                perGame: new StreamOverrides { FpsMode = FpsMode.Static, FpsStatic = 120 });
            Assert.AreEqual(OverrideSource.PerGame, list[IdxFps].Source);
        }

        [TestMethod]
        public void Source_HostWinsOverGlobal_WhenPerGameNull()
        {
            var list = Build(
                global: new StreamOverrides { Hdr = HdrMode.Off },
                host:   new StreamOverrides { Hdr = HdrMode.On  });
            Assert.AreEqual(OverrideSource.Host, list[IdxHdr].Source);
        }

        [TestMethod]
        public void Source_GlobalWins_WhenHostAndPerGameNull()
        {
            var list = Build(global: new StreamOverrides { Hdr = HdrMode.On });
            Assert.AreEqual(OverrideSource.Global, list[IdxHdr].Source);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // 2. Source attribution — nullable int (BitrateKbps)
        // ══════════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void Source_Bitrate_AllLayersNull_ReturnsBuiltIn()
        {
            var list = Build();
            Assert.AreEqual(OverrideSource.BuiltIn, list[IdxBitrate].Source);
        }

        [TestMethod]
        public void Source_Bitrate_GlobalSets_ReturnsGlobal()
        {
            var list = Build(global: new StreamOverrides { BitrateKbps = 10_000 });
            Assert.AreEqual(OverrideSource.Global, list[IdxBitrate].Source);
        }

        [TestMethod]
        public void Source_Bitrate_HostSets_ReturnsHost()
        {
            var list = Build(host: new StreamOverrides { BitrateKbps = 15_000 });
            Assert.AreEqual(OverrideSource.Host, list[IdxBitrate].Source);
        }

        [TestMethod]
        public void Source_Bitrate_PerGameSets_ReturnsPerGame()
        {
            var list = Build(perGame: new StreamOverrides { BitrateKbps = 20_000 });
            Assert.AreEqual(OverrideSource.PerGame, list[IdxBitrate].Source);
        }

        [TestMethod]
        public void Source_Bitrate_PerGameWinsOverHost()
        {
            var list = Build(
                host:    new StreamOverrides { BitrateKbps = 15_000 },
                perGame: new StreamOverrides { BitrateKbps = 20_000 });
            Assert.AreEqual(OverrideSource.PerGame, list[IdxBitrate].Source);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // 3. Source attribution — string fields (VideoCodec)
        // ══════════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void Source_VideoCodec_AllLayersNull_ReturnsBuiltIn()
        {
            var list = Build();
            Assert.AreEqual(OverrideSource.BuiltIn, list[IdxVideoCodec].Source);
        }

        [TestMethod]
        public void Source_VideoCodec_HostSets_ReturnsHost()
        {
            var list = Build(host: new StreamOverrides { VideoCodec = "HEVC" });
            Assert.AreEqual(OverrideSource.Host, list[IdxVideoCodec].Source);
        }

        [TestMethod]
        public void Source_VideoCodec_PerGameWinsOverHost()
        {
            var list = Build(
                host:    new StreamOverrides { VideoCodec = "HEVC" },
                perGame: new StreamOverrides { VideoCodec = "AV1"  });
            Assert.AreEqual(OverrideSource.PerGame, list[IdxVideoCodec].Source);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // 4. Source attribution — nullable bool (Yuv444)
        // ══════════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void Source_Yuv444_HostSets_ReturnsHost()
        {
            var list = Build(host: new StreamOverrides { Yuv444 = true });
            Assert.AreEqual(OverrideSource.Host, list[IdxYuv444].Source);
        }

        [TestMethod]
        public void Source_Yuv444_FalseAtHost_IsNotTreatedAsNull()
        {
            // false != null — an explicit false at the host layer is a real override
            var list = Build(
                global: new StreamOverrides { Yuv444 = true  },
                host:   new StreamOverrides { Yuv444 = false });
            Assert.AreEqual(OverrideSource.Host, list[IdxYuv444].Source);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // 5. Mixed fields — each from a different layer
        // ══════════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void Source_MixedFields_EachFromCorrectLayer()
        {
            var list = Build(
                global:  new StreamOverrides { BitrateKbps = 5_000, VideoCodec = "H.264"         },
                host:    new StreamOverrides { FpsMode = FpsMode.Static, FpsStatic = 120          },
                perGame: new StreamOverrides { ResolutionMode = ResolutionMode.Static,
                                               ResolutionStatic = "1920x1080"                     });

            Assert.AreEqual(OverrideSource.PerGame, list[IdxResolution].Source);
            Assert.AreEqual(OverrideSource.Host,    list[IdxFps].Source);
            Assert.AreEqual(OverrideSource.BuiltIn, list[IdxHdr].Source);          // no layer sets it
            Assert.AreEqual(OverrideSource.Global,  list[IdxBitrate].Source);
            Assert.AreEqual(OverrideSource.Global,  list[IdxVideoCodec].Source);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // 6. Bitrate RuntimeNote — auto-calculation scenarios
        //
        // In test context L(key) returns the key string itself, so RuntimeNote values
        // equal the localization key names when the string was produced by L() or
        // string.Format(L(key), value) where the key has no {0} placeholder.
        // ══════════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void Bitrate_ExplicitlySet_HasNullNote()
        {
            var list = Build(perGame: new StreamOverrides { BitrateKbps = 20_000 });
            Assert.IsNull(list[IdxBitrate].RuntimeNote);
        }

        [TestMethod]
        public void Bitrate_AutoAll_KnownDisplay_ShowsCalcNote()
        {
            // All Auto + known display → full calculation is possible
            var display = new ClientDisplayInfo { Width = 1920, Height = 1080, RefreshHz = 60 };
            var list = Build(display: display);
            Assert.AreEqual(NoteCalc, list[IdxBitrate].RuntimeNote);
        }

        [TestMethod]
        public void Bitrate_StaticResAndFps_UnknownDisplay_ShowsCalcNote()
        {
            // Static resolution + static FPS → display not needed for the calculation
            var list = Build(perGame: new StreamOverrides
            {
                ResolutionMode   = ResolutionMode.Static,
                ResolutionStatic = "1920x1080",
                FpsMode          = FpsMode.Static,
                FpsStatic        = 60,
            });
            Assert.AreEqual(NoteCalc, list[IdxBitrate].RuntimeNote);
        }

        [TestMethod]
        public void Bitrate_StaticRes_AutoFps_UnknownDisplay_ShowsApproxNote()
        {
            // THE FIXED BUG: static resolution is known but FPS is Auto with unknown display.
            // Before the fix: shows AutoUnavailable even though width/height are set.
            // After the fix: shows BitrateAutoCalcApprox using the 60 fps fallback.
            var list = Build(perGame: new StreamOverrides
            {
                ResolutionMode   = ResolutionMode.Static,
                ResolutionStatic = "1920x1080",
                FpsMode          = FpsMode.Auto,
            });
            Assert.AreEqual(NoteApprox, list[IdxBitrate].RuntimeNote);
        }

        [TestMethod]
        public void Bitrate_AutoRes_UnknownDisplay_ShowsUnavailableNote()
        {
            // Auto resolution + unknown display → cannot calculate anything
            var list = Build();
            Assert.AreEqual(NoteUnavail, list[IdxBitrate].RuntimeNote);
        }

        [TestMethod]
        public void Bitrate_StaticRes_UnparsableString_ShowsUnavailableNote()
        {
            // TryParseResolution fails → effW/effH stay 0 → falls through to unavailable
            var list = Build(perGame: new StreamOverrides
            {
                ResolutionMode   = ResolutionMode.Static,
                ResolutionStatic = "not-a-resolution",
                FpsMode          = FpsMode.Static,
                FpsStatic        = 60,
            });
            Assert.AreEqual(NoteUnavail, list[IdxBitrate].RuntimeNote);
        }

        [TestMethod]
        public void Bitrate_AutoResFps_KnownDisplay_Yuv444_ShowsCalcNote()
        {
            // Yuv444 doubles the bitrate but should not affect which note key is used
            var display = new ClientDisplayInfo { Width = 1920, Height = 1080, RefreshHz = 60 };
            var list = Build(
                host:    new StreamOverrides { Yuv444 = true },
                display: display);
            Assert.AreEqual(NoteCalc, list[IdxBitrate].RuntimeNote);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // 7. RefreshPreview merge formula equivalence
        //
        // GameOverridesWindow.RefreshPreview uses:
        //   BuiltinDefault.MergedWith(effectiveFallback.MergedWith(working))
        // where effectiveFallback = BuiltinDefault.MergedWith(global).MergedWith(host).
        //
        // These tests verify that formula produces the same result as the full
        // 4-layer chain, covering the correctness of the preview computation.
        // ══════════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void RefreshPreviewMerge_EquivalentToFourLayerChain()
        {
            var global  = new StreamOverrides { BitrateKbps = 5_000 };
            var host    = new StreamOverrides { VideoCodec  = "HEVC" };
            var working = new StreamOverrides { FpsMode = FpsMode.Static, FpsStatic = 120 };

            var fullChain = StreamOverrides.BuiltinDefault
                .MergedWith(global)
                .MergedWith(host)
                .MergedWith(working);

            var effectiveFallback = StreamOverrides.BuiltinDefault.MergedWith(global).MergedWith(host);
            var preview = StreamOverrides.BuiltinDefault.MergedWith(effectiveFallback.MergedWith(working));

            Assert.AreEqual(fullChain.BitrateKbps,    preview.BitrateKbps);
            Assert.AreEqual(fullChain.VideoCodec,     preview.VideoCodec);
            Assert.AreEqual(fullChain.FpsMode,        preview.FpsMode);
            Assert.AreEqual(fullChain.FpsStatic,      preview.FpsStatic);
            Assert.AreEqual(fullChain.ResolutionMode, preview.ResolutionMode);
            Assert.AreEqual(fullChain.Hdr,            preview.Hdr);
        }

        [TestMethod]
        public void RefreshPreviewMerge_WorkingOverridesPropagates()
        {
            var host    = new StreamOverrides { VideoCodec  = "HEVC" };
            var working = new StreamOverrides { VideoCodec = "AV1", BitrateKbps = 50_000 };

            var effectiveFallback = StreamOverrides.BuiltinDefault.MergedWith(host);
            var preview = StreamOverrides.BuiltinDefault.MergedWith(effectiveFallback.MergedWith(working));

            Assert.AreEqual("AV1",    preview.VideoCodec);
            Assert.AreEqual(50_000,   preview.BitrateKbps);
        }

        [TestMethod]
        public void RefreshPreviewMerge_FallbackSurvivesWhenWorkingEmpty()
        {
            var global  = new StreamOverrides { BitrateKbps = 10_000 };
            var host    = new StreamOverrides { VideoCodec  = "HEVC"  };
            var working = new StreamOverrides(); // nothing set

            var effectiveFallback = StreamOverrides.BuiltinDefault.MergedWith(global).MergedWith(host);
            var preview = StreamOverrides.BuiltinDefault.MergedWith(effectiveFallback.MergedWith(working));

            Assert.AreEqual(10_000, preview.BitrateKbps); // global survives via effectiveFallback
            Assert.AreEqual("HEVC", preview.VideoCodec);  // host survives via effectiveFallback
        }

        [TestMethod]
        public void RefreshPreviewMerge_WorkingCanResetToBuiltinMode()
        {
            // Host overrides FPS to static; working resets it back to Auto.
            var host    = new StreamOverrides { FpsMode = FpsMode.Static, FpsStatic = 120 };
            var working = new StreamOverrides { FpsMode = FpsMode.Auto };

            var effectiveFallback = StreamOverrides.BuiltinDefault.MergedWith(host);
            var preview = StreamOverrides.BuiltinDefault.MergedWith(effectiveFallback.MergedWith(working));

            Assert.AreEqual(FpsMode.Auto, preview.FpsMode);
        }
    }
}
