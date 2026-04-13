using Microsoft.VisualStudio.TestTools.UnitTesting;
using SunshineLibrary.Models;
using SunshineLibrary.Services.Clients;
using System;
using System.Linq;

namespace SunshineLibrary.Tests
{
    [TestClass]
    public class BitrateCalculatorTests
    {
        // ─── Known reference points (from moonlight-qt source) ─────────────────

        [TestMethod]
        public void Calculate_1080p60_Returns20Mbps()
        {
            Assert.AreEqual(20_000, BitrateCalculator.Calculate(1920, 1080, 60));
        }

        [TestMethod]
        public void Calculate_1440p60_Returns40Mbps()
        {
            Assert.AreEqual(40_000, BitrateCalculator.Calculate(2560, 1440, 60));
        }

        [TestMethod]
        public void Calculate_4K60_Returns80Mbps()
        {
            Assert.AreEqual(80_000, BitrateCalculator.Calculate(3840, 2160, 60));
        }

        [TestMethod]
        public void Calculate_720p60_Returns10Mbps()
        {
            Assert.AreEqual(10_000, BitrateCalculator.Calculate(1280, 720, 60));
        }

        [TestMethod]
        public void Calculate_720p30_Returns5Mbps()
        {
            Assert.AreEqual(5_000, BitrateCalculator.Calculate(1280, 720, 30));
        }

        // ─── FPS factor (>60 uses sqrt curve) ─────────────────────────────────

        [TestMethod]
        public void Calculate_1080p120_AppliesSqrtFpsCurve()
        {
            // fpsFactor = sqrt(120/60) × 2 = sqrt(2) × 2 ≈ 2.828
            // resKbps(1080p) = 10_000
            // expected ≈ 10_000 × 2.828 = 28_284
            int result = BitrateCalculator.Calculate(1920, 1080, 120);
            Assert.IsTrue(Math.Abs(result - 28_284) <= 10, $"Expected ~28284, got {result}");
        }

        [TestMethod]
        public void Calculate_1080p144_AppliesSqrtFpsCurve()
        {
            // fpsFactor = sqrt(144/60) × 2 = sqrt(2.4) × 2 ≈ 3.098
            int result = BitrateCalculator.Calculate(1920, 1080, 144);
            Assert.IsTrue(result > 28_284 && result < 40_000, $"Expected between 28k–40k, got {result}");
        }

        [TestMethod]
        public void Calculate_FpsAt60_LinearFactorIs2()
        {
            // 1080p@60 = 20_000, 1080p@30 = 10_000 → linear 2× relationship holds at ≤60fps
            Assert.AreEqual(10_000, BitrateCalculator.Calculate(1920, 1080, 30));
            Assert.AreEqual(20_000, BitrateCalculator.Calculate(1920, 1080, 60));
        }

        // ─── Resolution interpolation ──────────────────────────────────────────

        [TestMethod]
        public void Calculate_BelowLowestTableEntry_ClampsToMinimum()
        {
            int result = BitrateCalculator.Calculate(320, 180, 60);
            // 320×180 is below 640×360 = 1000kbps × fps factor 2 = 2000
            Assert.AreEqual(2_000, result);
        }

        [TestMethod]
        public void Calculate_AboveHighestTableEntry_ClampsToMaximum()
        {
            // 8K is above the 4K entry → clamps to 4K factor
            int result = BitrateCalculator.Calculate(7680, 4320, 60);
            Assert.AreEqual(80_000, result);
        }

        [TestMethod]
        public void Calculate_IntermediateResolution_Interpolated()
        {
            // 2K (2048×1152) is between 1440p and 4K table entries
            int lo = BitrateCalculator.Calculate(1920, 1080, 60); // 20_000
            int hi = BitrateCalculator.Calculate(3840, 2160, 60); // 80_000
            int mid = BitrateCalculator.Calculate(2048, 1152, 60);
            Assert.IsTrue(mid > lo && mid < hi, $"Interpolated {mid} not between {lo} and {hi}");
        }

        // ─── YUV 4:4:4 multiplier ─────────────────────────────────────────────

        [TestMethod]
        public void Calculate_Yuv444_DoublesBaseBitrate()
        {
            int without = BitrateCalculator.Calculate(1920, 1080, 60);
            int with444 = BitrateCalculator.Calculate(1920, 1080, 60, yuv444: true);
            Assert.AreEqual(without * 2, with444);
        }

        [TestMethod]
        public void Calculate_Yuv444_At4K60()
        {
            Assert.AreEqual(160_000, BitrateCalculator.Calculate(3840, 2160, 60, yuv444: true));
        }

        // ─── HDR has NO effect on bitrate (confirmed against moonlight-qt source) ──

        [TestMethod]
        public void Calculate_HdrOn_DoesNotChangeBitrate()
        {
            // moonlight-qt's bitrate formula does not include an HDR multiplier.
            // HDR controls the colour pipeline but not bandwidth. Users needing
            // more headroom for HDR should set an explicit bitrate override.
            int withoutHdr = BitrateCalculator.Calculate(1920, 1080, 60);
            // HDR state is not a parameter — formula is the same regardless of HDR setting.
            Assert.AreEqual(20_000, withoutHdr);
        }

        // ─── Combined flags ────────────────────────────────────────────────────

        [TestMethod]
        public void Calculate_Yuv444_OnlyYuv444Doubles()
        {
            int base_ = BitrateCalculator.Calculate(1920, 1080, 60);
            int with444 = BitrateCalculator.Calculate(1920, 1080, 60, yuv444: true);
            Assert.AreEqual(base_ * 2, with444);
        }

        // ─── Edge / guard cases ────────────────────────────────────────────────

        [TestMethod]
        public void Calculate_ZeroDimensions_ReturnsZero()
        {
            Assert.AreEqual(0, BitrateCalculator.Calculate(0, 1080, 60));
            Assert.AreEqual(0, BitrateCalculator.Calculate(1920, 0, 60));
            Assert.AreEqual(0, BitrateCalculator.Calculate(1920, 1080, 0));
        }

        [TestMethod]
        public void Calculate_NegativeDimensions_ReturnsZero()
        {
            Assert.AreEqual(0, BitrateCalculator.Calculate(-1, 1080, 60));
        }
    }

    /// <summary>
    /// Tests that ComposeArgs auto-calculates and passes --bitrate when no override is set
    /// and display info is available.
    /// </summary>
    [TestClass]
    public class AutoBitrateComposeArgsTests
    {
        private static HostConfig Host() => new HostConfig
        {
            Id = Guid.NewGuid(), Label = "T", Address = "192.168.1.5", Port = 47990,
            ServerType = ServerType.Sunshine,
        };
        private static RemoteApp App() => new RemoteApp { Name = "Game", StableId = "x" };

        [TestMethod]
        public void NoBitrateOverride_KnownDisplay_PassesCalculatedBitrate()
        {
            var display = new ClientDisplayInfo { Width = 1920, Height = 1080, RefreshHz = 60 };
            var merged = StreamOverrides.BuiltinDefault; // no BitrateKbps override

            var args = MoonlightCompatibleClient.ComposeArgs(Host(), App(), merged, display);

            int idx = args.IndexOf("--bitrate");
            Assert.IsTrue(idx >= 0, "--bitrate should be present");
            Assert.AreEqual("20000", args[idx + 1]);
        }

        [TestMethod]
        public void NoBitrateOverride_UnknownDisplay_NoBitrateArg()
        {
            var merged = StreamOverrides.BuiltinDefault;
            var args = MoonlightCompatibleClient.ComposeArgs(Host(), App(), merged, ClientDisplayInfo.Unknown);
            Assert.IsFalse(args.Contains("--bitrate"), "--bitrate should be absent when display is unknown");
        }

        [TestMethod]
        public void ExplicitBitrateOverride_WinsOverCalculated()
        {
            var display = new ClientDisplayInfo { Width = 1920, Height = 1080, RefreshHz = 60 };
            var merged = StreamOverrides.BuiltinDefault.MergedWith(
                new StreamOverrides { BitrateKbps = 50_000 });

            var args = MoonlightCompatibleClient.ComposeArgs(Host(), App(), merged, display);

            int idx = args.IndexOf("--bitrate");
            Assert.IsTrue(idx >= 0);
            Assert.AreEqual("50000", args[idx + 1]);
        }

        [TestMethod]
        public void Yuv444Enabled_DoublesAutoCalculatedBitrate()
        {
            var display = new ClientDisplayInfo { Width = 1920, Height = 1080, RefreshHz = 60 };
            var merged = StreamOverrides.BuiltinDefault.MergedWith(
                new StreamOverrides { Yuv444 = true });

            var args = MoonlightCompatibleClient.ComposeArgs(Host(), App(), merged, display);

            int idx = args.IndexOf("--bitrate");
            Assert.IsTrue(idx >= 0);
            Assert.AreEqual("40000", args[idx + 1]); // 20_000 × 2
        }

        [TestMethod]
        public void HdrOn_DoesNotAffectAutoCalculatedBitrate()
        {
            // HDR does not factor into moonlight-qt's bitrate formula.
            var display = new ClientDisplayInfo { Width = 1920, Height = 1080, RefreshHz = 60 };
            var withHdr    = MoonlightCompatibleClient.ComposeArgs(Host(), App(),
                StreamOverrides.BuiltinDefault.MergedWith(new StreamOverrides { Hdr = HdrMode.On }), display);
            var withoutHdr = MoonlightCompatibleClient.ComposeArgs(Host(), App(),
                StreamOverrides.BuiltinDefault.MergedWith(new StreamOverrides { Hdr = HdrMode.Off }), display);

            int idxOn  = withHdr.IndexOf("--bitrate");
            int idxOff = withoutHdr.IndexOf("--bitrate");
            Assert.IsTrue(idxOn >= 0 && idxOff >= 0);
            Assert.AreEqual(withoutHdr[idxOff + 1], withHdr[idxOn + 1],
                "Bitrate should be identical regardless of HDR mode");
        }

        [TestMethod]
        public void StaticResolution_UsedForBitrateCalculation()
        {
            var display = new ClientDisplayInfo { Width = 1920, Height = 1080, RefreshHz = 60 };
            var merged = StreamOverrides.BuiltinDefault.MergedWith(new StreamOverrides
            {
                ResolutionMode = ResolutionMode.Static,
                ResolutionStatic = "2560x1440",
            });

            var args = MoonlightCompatibleClient.ComposeArgs(Host(), App(), merged, display);

            int idx = args.IndexOf("--bitrate");
            Assert.IsTrue(idx >= 0);
            Assert.AreEqual("40000", args[idx + 1]); // 1440p@60fps
        }

        [TestMethod]
        public void StaticFps_UsedForBitrateCalculation()
        {
            var display = new ClientDisplayInfo { Width = 1920, Height = 1080, RefreshHz = 60 };
            var merged = StreamOverrides.BuiltinDefault.MergedWith(new StreamOverrides
            {
                FpsMode = FpsMode.Static,
                FpsStatic = 120,
            });

            var args = MoonlightCompatibleClient.ComposeArgs(Host(), App(), merged, display);

            int idx = args.IndexOf("--bitrate");
            Assert.IsTrue(idx >= 0);
            // 1080p@120fps ≈ 28_284
            int actual = int.Parse(args[idx + 1]);
            Assert.IsTrue(Math.Abs(actual - 28_284) <= 10, $"Expected ~28284, got {actual}");
        }
    }
}
