using System;

namespace SunshineLibrary.Models
{
    /// <summary>
    /// Replicates moonlight-qt's default bitrate formula so we can pass an explicit
    /// <c>--bitrate</c> value that matches what Moonlight would auto-select, making
    /// the effective bitrate transparent and predictable.
    ///
    /// Base formula (streamingpreferences.cpp):
    ///   bitrate_kbps = resolutionFactor(pixels) × fpsFactor(fps)
    ///
    ///   fpsFactor = fps ≤ 60  →  fps / 30
    ///               fps &gt; 60  →  sqrt(fps / 60) × 2
    ///
    ///   resolutionFactor is linearly interpolated from a lookup table.
    ///
    /// Additional multipliers applied after the base calculation:
    ///   YUV 4:4:4  — ×2.0  (full chroma vs. 4:2:0 halved chroma; per moonlight-qt source)
    ///
    /// Codec (H.264 / HEVC / AV1) does not affect the auto-bitrate: moonlight-qt applies
    /// the same formula regardless of codec, leaving bitrate/quality trade-offs to the user.
    ///
    /// HDR does NOT affect the auto-bitrate in moonlight-qt; the HDR flag controls the colour
    /// pipeline but not the bandwidth formula. Users who need more headroom for HDR should set
    /// an explicit override.
    /// </summary>
    public static class BitrateCalculator
    {
        // (total pixels, base kbps) lookup — matches moonlight-qt's table.
        private static readonly (int Pixels, int Kbps)[] Table =
        {
            (640  * 360,  1_000),
            (854  * 480,  2_000),
            (1280 * 720,  5_000),
            (1920 * 1080, 10_000),
            (2560 * 1440, 20_000),
            (3840 * 2160, 40_000),
        };

        /// <summary>
        /// Returns the auto-calculated bitrate in kbps.
        /// </summary>
        /// <param name="width">Streaming width in pixels.</param>
        /// <param name="height">Streaming height in pixels.</param>
        /// <param name="fps">Target frame rate.</param>
        /// <param name="yuv444">True when YUV 4:4:4 chroma subsampling is enabled (doubles bitrate).</param>
        public static int Calculate(int width, int height, int fps, bool yuv444 = false)
        {
            if (width <= 0 || height <= 0 || fps <= 0) return 0;

            int pixels = width * height;
            double resKbps = InterpolateResolution(pixels);
            double fpsFactor = fps <= 60
                ? fps / 30.0
                : Math.Sqrt(fps / 60.0) * 2.0;

            double bitrate = resKbps * fpsFactor;
            if (yuv444) bitrate *= 2.0;

            return (int)Math.Round(bitrate);
        }

        private static double InterpolateResolution(int pixels)
        {
            if (pixels <= Table[0].Pixels) return Table[0].Kbps;
            for (int i = 1; i < Table.Length; i++)
            {
                if (pixels <= Table[i].Pixels)
                {
                    var lo = Table[i - 1];
                    var hi = Table[i];
                    double t = (double)(pixels - lo.Pixels) / (hi.Pixels - lo.Pixels);
                    return lo.Kbps + t * (hi.Kbps - lo.Kbps);
                }
            }
            return Table[Table.Length - 1].Kbps;
        }
    }
}
