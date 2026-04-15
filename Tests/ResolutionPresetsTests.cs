using Microsoft.VisualStudio.TestTools.UnitTesting;
using SunshineLibrary.Models;
using SunshineLibrary.Settings;

namespace SunshineLibrary.Tests
{
    [TestClass]
    public class ResolutionPresetsTests
    {
        private static string[] Presets(int w, int h) =>
            StreamOverridesEditor.ResolutionPresetsForDisplay(new ClientDisplayInfo { Width = w, Height = h });

        private static string[] Presets(ClientDisplayInfo display) =>
            StreamOverridesEditor.ResolutionPresetsForDisplay(display);

        // ─── 16:9 — standard named HD/4K resolutions ───────────────────────────

        [TestMethod]
        public void Presets_16x9_Returns720pThrough4K()
        {
            CollectionAssert.AreEqual(
                new[] { "1280x720", "1600x900", "1920x1080", "2560x1440", "3840x2160" },
                Presets(1920, 1080));
        }

        [TestMethod]
        public void Presets_16x9_SameForAnyNativeResolution()
        {
            CollectionAssert.AreEqual(Presets(1920, 1080), Presets(2560, 1440));
            CollectionAssert.AreEqual(Presets(1920, 1080), Presets(3840, 2160));
        }

        // ─── 32:9 — super-ultrawide equivalents ────────────────────────────────

        [TestMethod]
        public void Presets_32x9_ReturnsUltrawideEquivalents()
        {
            CollectionAssert.AreEqual(
                new[] { "2560x720", "3200x900", "3840x1080", "5120x1440", "7680x2160" },
                Presets(3840, 1080));
        }

        [TestMethod]
        public void Presets_32x9_SameForAnyNativeResolution()
        {
            CollectionAssert.AreEqual(Presets(3840, 1080), Presets(5120, 1440));
        }

        // ─── 3440×1440 (43:18) — common ultrawide ──────────────────────────────

        [TestMethod]
        public void Presets_3440x1440_ReturnsExact43x18Multiples()
        {
            // gcd(3440,1440)=80 → base 43:18. All 5 reference heights divide by 18.
            CollectionAssert.AreEqual(
                new[] { "1720x720", "2150x900", "2580x1080", "3440x1440", "5160x2160" },
                Presets(3440, 1440));
        }

        // ─── 16:10 and 4:3 — exact ratio, not historically-named ───────────────
        //
        // The reference heights (720, 900, 1080, 1440, 2160) are chosen to align with
        // 9-height displays. For 5-height (16:10) and 3-height (4:3) displays the
        // algorithm still produces exact-ratio multiples — just not the resolutions that
        // have traditional names (e.g. 1024x768, 1920x1200). The tests below document
        // this behaviour rather than assert user-visible "ideal" values.

        [TestMethod]
        public void Presets_16x10_ReturnsExactRatioMultiples()
        {
            // base 8:5 → widths are (h/5)*8 for each reference height
            CollectionAssert.AreEqual(
                new[] { "1152x720", "1440x900", "1728x1080", "2304x1440", "3456x2160" },
                Presets(1920, 1200));
        }

        [TestMethod]
        public void Presets_4x3_ReturnsExactRatioMultiples()
        {
            // base 4:3 → widths are (h/3)*4 for each reference height
            CollectionAssert.AreEqual(
                new[] { "960x720", "1200x900", "1440x1080", "1920x1440", "2880x2160" },
                Presets(1024, 768));
        }

        // ─── Unusual aspect ratio — fallback path ──────────────────────────────

        [TestMethod]
        public void Presets_2560x1080_FallsBackToRatioScaled()
        {
            // base 64:27 — only 1080 and 2160 of the reference heights divide by 27,
            // so the fallback path scales by pixel ratio and rounds to nearest even pixel.
            var presets = Presets(2560, 1080);
            Assert.AreEqual(5, presets.Length);
            CollectionAssert.Contains(presets, "2560x1080"); // exact at native height
            CollectionAssert.Contains(presets, "5120x2160"); // exact at 2× height
            foreach (var p in presets)
            {
                int w = int.Parse(p.Split('x')[0]);
                Assert.AreEqual(0, w % 2, $"Width in '{p}' is not even");
            }
        }

        // ─── Unknown display — defaults to 16:9 ────────────────────────────────

        [TestMethod]
        public void Presets_UnknownDisplay_DefaultsTo16x9()
        {
            CollectionAssert.AreEqual(
                new[] { "1280x720", "1600x900", "1920x1080", "2560x1440", "3840x2160" },
                Presets(ClientDisplayInfo.Unknown));
        }

        // ─── Output invariants ─────────────────────────────────────────────────

        [TestMethod]
        public void Presets_AlwaysAtLeastTwoEntries()
        {
            int[][] displays = {
                new[] { 1920, 1080 }, new[] { 2560, 1440 }, new[] { 3840, 2160 },
                new[] { 3440, 1440 }, new[] { 2560, 1080 }, new[] { 3840, 1080 },
                new[] { 1280, 800 },  new[] { 1024, 768 },
            };
            foreach (var d in displays)
            {
                var presets = Presets(d[0], d[1]);
                Assert.IsTrue(presets.Length >= 2,
                    $"Expected >= 2 presets for {d[0]}x{d[1]}, got {presets.Length}");
            }
        }

        [TestMethod]
        public void Presets_AllEntriesAreWxHFormat()
        {
            foreach (var p in Presets(1920, 1080))
            {
                var parts = p.Split('x');
                Assert.AreEqual(2, parts.Length, $"'{p}' is not WxH format");
                Assert.IsTrue(int.TryParse(parts[0], out int w) && w > 0, $"Bad width in '{p}'");
                Assert.IsTrue(int.TryParse(parts[1], out int h) && h > 0, $"Bad height in '{p}'");
            }
        }
    }
}
