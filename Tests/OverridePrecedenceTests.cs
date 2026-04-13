using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using SunshineLibrary.Models;

namespace SunshineLibrary.Tests
{
    /// <summary>
    /// Full coverage of the 4-layer streaming-override merge chain:
    ///   BuiltinDefault &lt; global &lt; host.Defaults &lt; per-game
    ///
    /// Each null field means "inherit from the layer below".
    /// A non-null value at any layer wins over all layers beneath it.
    /// </summary>
    [TestClass]
    public class OverridePrecedenceTests
    {
        // ─── Helper ────────────────────────────────────────────────────────────
        // Simulates the exact merge order used at launch time.
        private static StreamOverrides Merge(
            StreamOverrides global = null,
            StreamOverrides host = null,
            StreamOverrides game = null)
        {
            return StreamOverrides.BuiltinDefault
                .MergedWith(global)
                .MergedWith(host)
                .MergedWith(game);
        }

        // ══════════════════════════════════════════════════════════════════════
        // 1. BuiltinDefault — baseline values
        // ══════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void Builtin_HasExpectedDefaultValues()
        {
            var b = StreamOverrides.BuiltinDefault;
            Assert.AreEqual(ResolutionMode.Auto, b.ResolutionMode);
            Assert.AreEqual(FpsMode.Auto, b.FpsMode);
            Assert.AreEqual(HdrMode.Auto, b.Hdr);
        }

        [TestMethod]
        public void Builtin_NonSetFieldsAreNull()
        {
            var b = StreamOverrides.BuiltinDefault;
            Assert.IsNull(b.ResolutionStatic);
            Assert.IsNull(b.FpsStatic);
            Assert.IsNull(b.BitrateKbps);
            Assert.IsNull(b.VideoCodec);
            Assert.IsNull(b.DisplayMode);
            Assert.IsNull(b.AudioConfig);
            Assert.IsNull(b.Yuv444);
            Assert.IsNull(b.FramePacing);
            Assert.IsNull(b.GameOptimization);
            Assert.IsNull(b.ShowStats);
            Assert.IsNull(b.ExtraArgs);
        }

        [TestMethod]
        public void Builtin_AllLayersNull_ReturnsBuiltinValues()
        {
            var result = Merge();
            Assert.AreEqual(ResolutionMode.Auto, result.ResolutionMode);
            Assert.AreEqual(FpsMode.Auto, result.FpsMode);
            Assert.AreEqual(HdrMode.Auto, result.Hdr);
        }

        [TestMethod]
        public void Builtin_AllLayersNull_NonBuiltinFieldsRemainNull()
        {
            var result = Merge();
            Assert.IsNull(result.BitrateKbps);
            Assert.IsNull(result.VideoCodec);
            Assert.IsNull(result.DisplayMode);
            Assert.IsNull(result.AudioConfig);
            Assert.IsNull(result.Yuv444);
            Assert.IsNull(result.FramePacing);
            Assert.IsNull(result.GameOptimization);
            Assert.IsNull(result.ExtraArgs);
        }

        // ══════════════════════════════════════════════════════════════════════
        // 2. Layer precedence — each layer wins over the one below it
        // ══════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void Chain_AllFourLayers_PerGameWins()
        {
            var result = Merge(
                global: new StreamOverrides { BitrateKbps = 5_000 },
                host:   new StreamOverrides { BitrateKbps = 10_000 },
                game:   new StreamOverrides { BitrateKbps = 20_000 }
            );
            Assert.AreEqual(20_000, result.BitrateKbps);
        }

        [TestMethod]
        public void Chain_HostWinsOverGlobal_WhenGameNull()
        {
            var result = Merge(
                global: new StreamOverrides { BitrateKbps = 5_000 },
                host:   new StreamOverrides { BitrateKbps = 10_000 }
            );
            Assert.AreEqual(10_000, result.BitrateKbps);
        }

        [TestMethod]
        public void Chain_GlobalWins_WhenHostAndGameNull()
        {
            var result = Merge(global: new StreamOverrides { BitrateKbps = 5_000 });
            Assert.AreEqual(5_000, result.BitrateKbps);
        }

        [TestMethod]
        public void Chain_GameAlone_WinsOverBuiltin()
        {
            var result = Merge(game: new StreamOverrides { Hdr = HdrMode.Off });
            Assert.AreEqual(HdrMode.Off, result.Hdr);
        }

        [TestMethod]
        public void Chain_GameNullField_HostValuePropagates()
        {
            var result = Merge(
                host: new StreamOverrides { VideoCodec = "HEVC" },
                game: new StreamOverrides { BitrateKbps = 20_000 } // VideoCodec unset
            );
            Assert.AreEqual("HEVC", result.VideoCodec);
            Assert.AreEqual(20_000, result.BitrateKbps);
        }

        [TestMethod]
        public void Chain_HostNullField_GlobalValuePropagates()
        {
            var result = Merge(
                global: new StreamOverrides { VideoCodec = "H.264" },
                host:   new StreamOverrides { BitrateKbps = 10_000 } // VideoCodec unset
            );
            Assert.AreEqual("H.264", result.VideoCodec);
            Assert.AreEqual(10_000, result.BitrateKbps);
        }

        // ══════════════════════════════════════════════════════════════════════
        // 3. Nullable enum fields
        // ══════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void ResolutionMode_HostOverridesBuiltin()
        {
            var result = Merge(host: new StreamOverrides { ResolutionMode = ResolutionMode.Static });
            Assert.AreEqual(ResolutionMode.Static, result.ResolutionMode);
        }

        [TestMethod]
        public void ResolutionMode_GameOverridesHost()
        {
            var result = Merge(
                host: new StreamOverrides { ResolutionMode = ResolutionMode.Static },
                game: new StreamOverrides { ResolutionMode = ResolutionMode.Auto }
            );
            Assert.AreEqual(ResolutionMode.Auto, result.ResolutionMode);
        }

        [TestMethod]
        public void ResolutionMode_GameNull_HostStaticSurvives()
        {
            var result = Merge(
                host: new StreamOverrides { ResolutionMode = ResolutionMode.Static },
                game: new StreamOverrides()
            );
            Assert.AreEqual(ResolutionMode.Static, result.ResolutionMode);
        }

        [TestMethod]
        public void FpsMode_HostOverridesBuiltin()
        {
            var result = Merge(host: new StreamOverrides { FpsMode = FpsMode.Static });
            Assert.AreEqual(FpsMode.Static, result.FpsMode);
        }

        [TestMethod]
        public void FpsMode_GameOverridesHost_BuiltinAutoDropped()
        {
            var result = Merge(
                host: new StreamOverrides { FpsMode = FpsMode.Static, FpsStatic = 120 },
                game: new StreamOverrides { FpsMode = FpsMode.Auto }
            );
            Assert.AreEqual(FpsMode.Auto, result.FpsMode);
        }

        [TestMethod]
        public void HdrMode_FullChain_EachLayerCanWin()
        {
            // game wins
            Assert.AreEqual(HdrMode.Auto, Merge(
                global: new StreamOverrides { Hdr = HdrMode.On },
                host:   new StreamOverrides { Hdr = HdrMode.Off },
                game:   new StreamOverrides { Hdr = HdrMode.Auto }
            ).Hdr);

            // host wins when game null
            Assert.AreEqual(HdrMode.Off, Merge(
                global: new StreamOverrides { Hdr = HdrMode.On },
                host:   new StreamOverrides { Hdr = HdrMode.Off }
            ).Hdr);

            // global wins when host+game null
            Assert.AreEqual(HdrMode.On, Merge(
                global: new StreamOverrides { Hdr = HdrMode.On }
            ).Hdr);
        }

        [TestMethod]
        public void HdrMode_GameExplicitlyOff_WinsOverHostOn()
        {
            var result = Merge(
                host: new StreamOverrides { Hdr = HdrMode.On },
                game: new StreamOverrides { Hdr = HdrMode.Off }
            );
            Assert.AreEqual(HdrMode.Off, result.Hdr);
        }

        [TestMethod]
        public void HdrMode_GameNull_HostOnSurvives()
        {
            var result = Merge(
                host: new StreamOverrides { Hdr = HdrMode.On },
                game: new StreamOverrides()
            );
            Assert.AreEqual(HdrMode.On, result.Hdr);
        }

        // ══════════════════════════════════════════════════════════════════════
        // 4. Nullable int fields
        // ══════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void FpsStatic_FullChain()
        {
            Assert.AreEqual(120, Merge(
                global: new StreamOverrides { FpsStatic = 30 },
                host:   new StreamOverrides { FpsStatic = 60 },
                game:   new StreamOverrides { FpsStatic = 120 }
            ).FpsStatic);

            Assert.AreEqual(60, Merge(
                global: new StreamOverrides { FpsStatic = 30 },
                host:   new StreamOverrides { FpsStatic = 60 },
                game:   new StreamOverrides()
            ).FpsStatic);

            Assert.AreEqual(30, Merge(
                global: new StreamOverrides { FpsStatic = 30 }
            ).FpsStatic);

            Assert.IsNull(Merge().FpsStatic);
        }

        [TestMethod]
        public void BitrateKbps_FullChain()
        {
            Assert.AreEqual(20_000, Merge(
                global: new StreamOverrides { BitrateKbps = 5_000 },
                host:   new StreamOverrides { BitrateKbps = 10_000 },
                game:   new StreamOverrides { BitrateKbps = 20_000 }
            ).BitrateKbps);

            Assert.AreEqual(10_000, Merge(
                global: new StreamOverrides { BitrateKbps = 5_000 },
                host:   new StreamOverrides { BitrateKbps = 10_000 }
            ).BitrateKbps);

            Assert.IsNull(Merge().BitrateKbps);
        }

        // ══════════════════════════════════════════════════════════════════════
        // 5. Nullable string fields — one test per field for complete coverage
        // ══════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void ResolutionStatic_FullChain()
        {
            Assert.AreEqual("3840x2160", Merge(
                global: new StreamOverrides { ResolutionStatic = "1920x1080" },
                host:   new StreamOverrides { ResolutionStatic = "2560x1440" },
                game:   new StreamOverrides { ResolutionStatic = "3840x2160" }
            ).ResolutionStatic);

            Assert.AreEqual("2560x1440", Merge(
                global: new StreamOverrides { ResolutionStatic = "1920x1080" },
                host:   new StreamOverrides { ResolutionStatic = "2560x1440" },
                game:   new StreamOverrides()
            ).ResolutionStatic);

            Assert.IsNull(Merge().ResolutionStatic);
        }

        [TestMethod]
        public void VideoCodec_FullChain()
        {
            Assert.AreEqual("AV1", Merge(
                global: new StreamOverrides { VideoCodec = "H.264" },
                host:   new StreamOverrides { VideoCodec = "HEVC" },
                game:   new StreamOverrides { VideoCodec = "AV1" }
            ).VideoCodec);

            Assert.AreEqual("HEVC", Merge(
                global: new StreamOverrides { VideoCodec = "H.264" },
                host:   new StreamOverrides { VideoCodec = "HEVC" }
            ).VideoCodec);

            Assert.IsNull(Merge().VideoCodec);
        }

        [TestMethod]
        public void DisplayMode_FullChain()
        {
            Assert.AreEqual("windowed", Merge(
                global: new StreamOverrides { DisplayMode = "fullscreen" },
                host:   new StreamOverrides { DisplayMode = "borderless" },
                game:   new StreamOverrides { DisplayMode = "windowed" }
            ).DisplayMode);

            Assert.AreEqual("borderless", Merge(
                global: new StreamOverrides { DisplayMode = "fullscreen" },
                host:   new StreamOverrides { DisplayMode = "borderless" }
            ).DisplayMode);
        }

        [TestMethod]
        public void AudioConfig_FullChain()
        {
            Assert.AreEqual("7.1-surround", Merge(
                global: new StreamOverrides { AudioConfig = "stereo" },
                host:   new StreamOverrides { AudioConfig = "5.1-surround" },
                game:   new StreamOverrides { AudioConfig = "7.1-surround" }
            ).AudioConfig);
        }

        [TestMethod]
        public void ExtraArgs_FullChain()
        {
            Assert.AreEqual("--game-arg", Merge(
                global: new StreamOverrides { ExtraArgs = "--global-arg" },
                host:   new StreamOverrides { ExtraArgs = "--host-arg" },
                game:   new StreamOverrides { ExtraArgs = "--game-arg" }
            ).ExtraArgs);

            Assert.IsNull(Merge().ExtraArgs);
        }

        // ══════════════════════════════════════════════════════════════════════
        // 6. Nullable bool fields — true/false/null are three distinct states
        // ══════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void Yuv444_FalseAtGame_WinsOverTrueAtHost()
        {
            Assert.AreEqual(false, Merge(
                host: new StreamOverrides { Yuv444 = true },
                game: new StreamOverrides { Yuv444 = false }
            ).Yuv444);
        }

        [TestMethod]
        public void Yuv444_TrueAtGame_WinsOverFalseAtHost()
        {
            Assert.AreEqual(true, Merge(
                host: new StreamOverrides { Yuv444 = false },
                game: new StreamOverrides { Yuv444 = true }
            ).Yuv444);
        }

        [TestMethod]
        public void Yuv444_NullAtGame_TrueFromHostSurvives()
        {
            Assert.AreEqual(true, Merge(
                host: new StreamOverrides { Yuv444 = true },
                game: new StreamOverrides()
            ).Yuv444);
        }

        [TestMethod]
        public void Yuv444_NullAtGame_FalseFromHostSurvives()
        {
            // false ≠ null; a false at the host layer must not be treated as "inherit"
            Assert.AreEqual(false, Merge(
                host: new StreamOverrides { Yuv444 = false },
                game: new StreamOverrides()
            ).Yuv444);
        }

        [TestMethod]
        public void Yuv444_FullChain_AllThreeStates()
        {
            // host=false overrides global=true; game null → host survives
            Assert.AreEqual(false, Merge(
                global: new StreamOverrides { Yuv444 = true },
                host:   new StreamOverrides { Yuv444 = false },
                game:   new StreamOverrides()
            ).Yuv444);

            // game=true overrides host=false
            Assert.AreEqual(true, Merge(
                global: new StreamOverrides { Yuv444 = false },
                host:   new StreamOverrides { Yuv444 = false },
                game:   new StreamOverrides { Yuv444 = true }
            ).Yuv444);

            Assert.IsNull(Merge().Yuv444);
        }

        [TestMethod]
        public void FramePacing_FullChain()
        {
            Assert.AreEqual(true, Merge(
                global: new StreamOverrides { FramePacing = false },
                host:   new StreamOverrides { FramePacing = true }
            ).FramePacing);

            Assert.AreEqual(false, Merge(
                global: new StreamOverrides { FramePacing = true },
                host:   new StreamOverrides { FramePacing = false }
            ).FramePacing);

            Assert.IsNull(Merge().FramePacing);
        }

        [TestMethod]
        public void GameOptimization_FullChain()
        {
            Assert.AreEqual(true, Merge(
                global: new StreamOverrides { GameOptimization = false },
                host:   new StreamOverrides { GameOptimization = true }
            ).GameOptimization);

            Assert.AreEqual(false, Merge(
                host: new StreamOverrides { GameOptimization = true },
                game: new StreamOverrides { GameOptimization = false }
            ).GameOptimization);

            Assert.IsNull(Merge().GameOptimization);
        }

        [TestMethod]
        public void ShowStats_FullChain()
        {
            Assert.AreEqual(true, Merge(
                global: new StreamOverrides { ShowStats = false },
                host:   new StreamOverrides { ShowStats = true }
            ).ShowStats);

            Assert.AreEqual(false, Merge(
                host: new StreamOverrides { ShowStats = true },
                game: new StreamOverrides { ShowStats = false }
            ).ShowStats);

            Assert.IsNull(Merge().ShowStats);
        }

        // ══════════════════════════════════════════════════════════════════════
        // 7. Null / inherit semantics
        // ══════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void MergedWithNull_ReturnsSelf()
        {
            var a = new StreamOverrides { BitrateKbps = 1234 };
            var result = a.MergedWith(null);
            Assert.AreSame(a, result);
        }

        [TestMethod]
        public void NullLayer_DoesNotWipeLowerValue()
        {
            // Passing null as the game layer (no per-game record) must not wipe host values
            var host = new StreamOverrides { BitrateKbps = 40_000 };
            var result = StreamOverrides.BuiltinDefault.MergedWith(host).MergedWith(null);
            Assert.AreEqual(40_000, result.BitrateKbps);
        }

        [TestMethod]
        public void EmptyOverridesAtGame_PreservesAllHostValues()
        {
            var host = new StreamOverrides
            {
                ResolutionMode  = ResolutionMode.Static,
                ResolutionStatic = "2560x1440",
                FpsMode         = FpsMode.Static,
                FpsStatic       = 144,
                BitrateKbps     = 30_000,
                VideoCodec      = "HEVC",
                Yuv444          = true,
                FramePacing     = false,
            };
            var game = new StreamOverrides(); // every field null
            var result = StreamOverrides.BuiltinDefault.MergedWith(host).MergedWith(game);

            Assert.AreEqual(ResolutionMode.Static, result.ResolutionMode);
            Assert.AreEqual("2560x1440", result.ResolutionStatic);
            Assert.AreEqual(FpsMode.Static, result.FpsMode);
            Assert.AreEqual(144, result.FpsStatic);
            Assert.AreEqual(30_000, result.BitrateKbps);
            Assert.AreEqual("HEVC", result.VideoCodec);
            Assert.AreEqual(true, result.Yuv444);
            Assert.AreEqual(false, result.FramePacing);
        }

        // ══════════════════════════════════════════════════════════════════════
        // 8. Mixed field sources — different fields from different layers
        // ══════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void Mixed_EachFieldFromDifferentLayer()
        {
            var global = new StreamOverrides { BitrateKbps = 5_000, AudioConfig = "stereo" };
            var host   = new StreamOverrides { VideoCodec  = "HEVC", FramePacing = true };
            var game   = new StreamOverrides { FpsStatic   = 60,     DisplayMode = "windowed" };

            var result = StreamOverrides.BuiltinDefault.MergedWith(global).MergedWith(host).MergedWith(game);

            Assert.AreEqual(60, result.FpsStatic);            // game
            Assert.AreEqual("windowed", result.DisplayMode);  // game
            Assert.AreEqual("HEVC", result.VideoCodec);       // host
            Assert.AreEqual(true, result.FramePacing);        // host
            Assert.AreEqual(5_000, result.BitrateKbps);       // global
            Assert.AreEqual("stereo", result.AudioConfig);    // global
            Assert.AreEqual(ResolutionMode.Auto, result.ResolutionMode); // builtin
            Assert.AreEqual(HdrMode.Auto, result.Hdr);        // builtin
            Assert.AreEqual(FpsMode.Auto, result.FpsMode);    // builtin
        }

        [TestMethod]
        public void Mixed_GameOverridesSomeHostFields_HostSurvivesForRest()
        {
            var host = new StreamOverrides
            {
                ResolutionMode  = ResolutionMode.Static,
                ResolutionStatic = "1920x1080",
                BitrateKbps     = 30_000,
                VideoCodec      = "HEVC",
            };
            var game = new StreamOverrides
            {
                VideoCodec  = "AV1",
                BitrateKbps = 50_000,
                // ResolutionMode / ResolutionStatic intentionally not set
            };

            var result = StreamOverrides.BuiltinDefault.MergedWith(host).MergedWith(game);

            Assert.AreEqual("AV1", result.VideoCodec);                    // game wins
            Assert.AreEqual(50_000, result.BitrateKbps);                  // game wins
            Assert.AreEqual(ResolutionMode.Static, result.ResolutionMode); // host survives
            Assert.AreEqual("1920x1080", result.ResolutionStatic);         // host survives
        }

        [TestMethod]
        public void Mixed_GlobalSetHostClearsGame_CorrectLayerWins()
        {
            // Global sets codec; host overrides to different codec; game leaves it null
            var result = Merge(
                global: new StreamOverrides { VideoCodec = "H.264", BitrateKbps = 5_000 },
                host:   new StreamOverrides { VideoCodec = "HEVC"  },
                game:   new StreamOverrides { BitrateKbps = 20_000 }
            );
            Assert.AreEqual("HEVC", result.VideoCodec);     // host wins over global
            Assert.AreEqual(20_000, result.BitrateKbps);    // game wins over global
        }

        // ══════════════════════════════════════════════════════════════════════
        // 9. Non-commutativity — order of operands determines which layer wins
        // ══════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void MergedWith_IsNotCommutative()
        {
            var lower  = new StreamOverrides { BitrateKbps = 10_000, VideoCodec = "H.264" };
            var higher = new StreamOverrides { BitrateKbps = 20_000 }; // VideoCodec null

            // lower.MergedWith(higher) → higher wins where set; VideoCodec falls through
            var result = lower.MergedWith(higher);
            Assert.AreEqual(20_000, result.BitrateKbps);
            Assert.AreEqual("H.264", result.VideoCodec); // higher.VideoCodec is null → lower survives

            // higher.MergedWith(lower) → lower is now the "override" layer
            var reversed = higher.MergedWith(lower);
            Assert.AreEqual(10_000, reversed.BitrateKbps); // lower wins as the override
            Assert.AreEqual("H.264", reversed.VideoCodec);
        }

        // ══════════════════════════════════════════════════════════════════════
        // 10. Serialization round-trips — null/false/true are three distinct states
        //     and must survive JSON serialize → deserialize without collapsing.
        // ══════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void Serialization_NullFields_PreservedAsNull()
        {
            var original = new StreamOverrides(); // all fields null
            var json = JsonConvert.SerializeObject(original, StreamOverrides.JsonSettings);
            var restored = JsonConvert.DeserializeObject<StreamOverrides>(json, StreamOverrides.JsonSettings);

            Assert.IsNull(restored.ResolutionMode);
            Assert.IsNull(restored.FpsMode);
            Assert.IsNull(restored.Hdr);
            Assert.IsNull(restored.BitrateKbps);
            Assert.IsNull(restored.Yuv444);
            Assert.IsNull(restored.FramePacing);
            Assert.IsNull(restored.GameOptimization);
        }

        [TestMethod]
        public void Serialization_BoolTrue_RoundTrips()
        {
            var original = new StreamOverrides { Yuv444 = true, FramePacing = true };
            var json = JsonConvert.SerializeObject(original, StreamOverrides.JsonSettings);
            var restored = JsonConvert.DeserializeObject<StreamOverrides>(json, StreamOverrides.JsonSettings);

            Assert.AreEqual(true, restored.Yuv444);
            Assert.AreEqual(true, restored.FramePacing);
        }

        [TestMethod]
        public void Serialization_BoolFalse_NotLostAsNull()
        {
            // false must survive as false — not silently promoted to null (inherit)
            var original = new StreamOverrides
            {
                Yuv444          = false,
                FramePacing     = false,
                GameOptimization = false,
            };
            var json = JsonConvert.SerializeObject(original, StreamOverrides.JsonSettings);
            var restored = JsonConvert.DeserializeObject<StreamOverrides>(json, StreamOverrides.JsonSettings);

            Assert.AreEqual(false, restored.Yuv444);
            Assert.AreEqual(false, restored.FramePacing);
            Assert.AreEqual(false, restored.GameOptimization);
        }

        [TestMethod]
        public void Serialization_EnumValues_RoundTrip()
        {
            var original = new StreamOverrides
            {
                ResolutionMode = ResolutionMode.Static,
                FpsMode        = FpsMode.Static,
                Hdr            = HdrMode.On,
            };
            var json = JsonConvert.SerializeObject(original, StreamOverrides.JsonSettings);
            var restored = JsonConvert.DeserializeObject<StreamOverrides>(json, StreamOverrides.JsonSettings);

            Assert.AreEqual(ResolutionMode.Static, restored.ResolutionMode);
            Assert.AreEqual(FpsMode.Static, restored.FpsMode);
            Assert.AreEqual(HdrMode.On, restored.Hdr);
        }

        [TestMethod]
        public void Serialization_IntAndStringFields_RoundTrip()
        {
            var original = new StreamOverrides
            {
                BitrateKbps     = 30_000,
                FpsStatic       = 144,
                VideoCodec      = "HEVC",
                DisplayMode     = "fullscreen",
                AudioConfig     = "7.1-surround",
                ExtraArgs       = "--some-arg",
                ResolutionStatic = "2560x1440",
            };
            var json = JsonConvert.SerializeObject(original, StreamOverrides.JsonSettings);
            var restored = JsonConvert.DeserializeObject<StreamOverrides>(json, StreamOverrides.JsonSettings);

            Assert.AreEqual(30_000, restored.BitrateKbps);
            Assert.AreEqual(144, restored.FpsStatic);
            Assert.AreEqual("HEVC", restored.VideoCodec);
            Assert.AreEqual("fullscreen", restored.DisplayMode);
            Assert.AreEqual("7.1-surround", restored.AudioConfig);
            Assert.AreEqual("--some-arg", restored.ExtraArgs);
            Assert.AreEqual("2560x1440", restored.ResolutionStatic);
        }

        [TestMethod]
        public void Serialization_MergedResult_ValuesPreservedAfterRoundTrip()
        {
            var merged = Merge(
                global: new StreamOverrides { BitrateKbps = 20_000, Yuv444 = false },
                host:   new StreamOverrides { VideoCodec = "HEVC" },
                game:   new StreamOverrides { Hdr = HdrMode.On }
            );
            var json = JsonConvert.SerializeObject(merged, StreamOverrides.JsonSettings);
            var restored = JsonConvert.DeserializeObject<StreamOverrides>(json, StreamOverrides.JsonSettings);

            Assert.AreEqual(20_000, restored.BitrateKbps);
            Assert.AreEqual(false, restored.Yuv444);   // false must survive
            Assert.AreEqual("HEVC", restored.VideoCodec);
            Assert.AreEqual(HdrMode.On, restored.Hdr);
            Assert.AreEqual(ResolutionMode.Auto, restored.ResolutionMode); // from builtin
        }

        // ══════════════════════════════════════════════════════════════════════
        // 11. Original tests (kept verbatim for regression coverage)
        // ══════════════════════════════════════════════════════════════════════

        [TestMethod]
        public void Chain_BuiltinGlobalHostGame_GameWins()
        {
            var builtin = StreamOverrides.BuiltinDefault;
            var global  = new StreamOverrides { Hdr = HdrMode.Off, BitrateKbps = 15_000 };
            var host    = new StreamOverrides { BitrateKbps = 25_000, VideoCodec = "HEVC" };
            var perGame = new StreamOverrides { Hdr = HdrMode.On };

            var merged = builtin.MergedWith(global).MergedWith(host).MergedWith(perGame);

            Assert.AreEqual(HdrMode.On, merged.Hdr);              // game wins
            Assert.AreEqual(25_000, merged.BitrateKbps);           // host wins over global
            Assert.AreEqual("HEVC", merged.VideoCodec);            // host only
            Assert.AreEqual(ResolutionMode.Auto, merged.ResolutionMode); // builtin untouched
        }

        [TestMethod]
        public void Chain_GameInheritsAllButOne_HostWinsForUnset()
        {
            var builtin = StreamOverrides.BuiltinDefault;
            var host = new StreamOverrides
            {
                ResolutionMode  = ResolutionMode.Static,
                ResolutionStatic = "2560x1440",
                FpsMode         = FpsMode.Static,
                FpsStatic       = 144,
            };
            var perGame = new StreamOverrides { FpsStatic = 60, FpsMode = FpsMode.Static };

            var merged = builtin.MergedWith(host).MergedWith(perGame);

            Assert.AreEqual(ResolutionMode.Static, merged.ResolutionMode);
            Assert.AreEqual("2560x1440", merged.ResolutionStatic);
            Assert.AreEqual(60, merged.FpsStatic);
        }

        [TestMethod]
        public void Null_Game_FallsThroughToHost()
        {
            var host = new StreamOverrides { BitrateKbps = 40_000 };
            var merged = StreamOverrides.BuiltinDefault.MergedWith(host).MergedWith(null);
            Assert.AreEqual(40_000, merged.BitrateKbps);
        }

        [TestMethod]
        public void GameSetsInheritMode_DoesNotWipe()
        {
            var host    = new StreamOverrides { ResolutionMode = ResolutionMode.Static, ResolutionStatic = "1920x1080" };
            var perGame = new StreamOverrides(); // everything null
            var merged  = StreamOverrides.BuiltinDefault.MergedWith(host).MergedWith(perGame);

            Assert.AreEqual(ResolutionMode.Static, merged.ResolutionMode);
            Assert.AreEqual("1920x1080", merged.ResolutionStatic);
        }

        [TestMethod]
        public void BoolTriState_FalseVsNullDistinct()
        {
            var host = new StreamOverrides { Hdr = HdrMode.On };
            var game = new StreamOverrides { Hdr = HdrMode.Off };
            Assert.AreEqual(HdrMode.Off, host.MergedWith(game).Hdr);

            var gameInherit = new StreamOverrides();
            Assert.AreEqual(HdrMode.On, host.MergedWith(gameInherit).Hdr);
        }
    }
}
