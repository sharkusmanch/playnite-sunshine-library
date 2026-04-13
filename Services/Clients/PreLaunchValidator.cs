using SunshineLibrary.Models;
using System.Collections.Generic;

namespace SunshineLibrary.Services.Clients
{
    /// <summary>
    /// Advisory validation on merged stream overrides (PLAN §7c). Non-blocking —
    /// returns a list of warnings for the caller to surface as toasts. Stream
    /// proceeds regardless; users who set a weird combo just get a heads-up so
    /// "why does the stream look wrong" doesn't become a support ticket.
    /// </summary>
    public static class PreLaunchValidator
    {
        public class Warning
        {
            /// <summary>LOC key for the message.</summary>
            public string MessageKey { get; set; }
            /// <summary>Format args for the message.</summary>
            public object[] FormatArgs { get; set; } = System.Array.Empty<object>();
        }

        public static IReadOnlyList<Warning> Inspect(StreamOverrides merged, ClientDisplayInfo display)
        {
            var warnings = new List<Warning>();
            if (merged == null) return warnings;

            // HDR with H.264: HDR requires HEVC Main10 or AV1. Moonlight silently downgrades on
            // some setups and outright fails on others; warn so the user can adjust the codec.
            var hdrOn = merged.Hdr == HdrMode.On
                     || (merged.Hdr == HdrMode.Auto && display != null && display.HdrEnabled);
            if (hdrOn && string.Equals(merged.VideoCodec, "H.264", System.StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new Warning { MessageKey = "LOC_SunshineLibrary_Warn_HdrWithH264" });
            }

            // Bitrate out of Moonlight's accepted range (500..500000).
            if (merged.BitrateKbps.HasValue &&
                (merged.BitrateKbps.Value < 500 || merged.BitrateKbps.Value > 500_000))
            {
                warnings.Add(new Warning
                {
                    MessageKey = "LOC_SunshineLibrary_Warn_BitrateOutOfRange",
                    FormatArgs = new object[] { merged.BitrateKbps.Value },
                });
            }

            // FPS out of Moonlight's accepted range (10..480).
            if (merged.FpsStatic.HasValue &&
                (merged.FpsStatic.Value < 10 || merged.FpsStatic.Value > 480))
            {
                warnings.Add(new Warning
                {
                    MessageKey = "LOC_SunshineLibrary_Warn_FpsOutOfRange",
                    FormatArgs = new object[] { merged.FpsStatic.Value },
                });
            }

            return warnings;
        }
    }
}
