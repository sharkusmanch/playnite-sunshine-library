using Newtonsoft.Json;

namespace SunshineLibrary.Models
{
    public enum ResolutionMode { Inherit, Auto, Static }
    public enum FpsMode { Inherit, Auto, Static }
    public enum HdrMode { Inherit, Auto, On, Off }

    public class StreamOverrides
    {
        // Null at any level means "inherit from next level up"; see PLAN §5.
        public ResolutionMode? ResolutionMode { get; set; }
        public string ResolutionStatic { get; set; }   // "1920x1080" when Static
        public FpsMode? FpsMode { get; set; }
        public int? FpsStatic { get; set; }
        public HdrMode? Hdr { get; set; }
        public int? BitrateKbps { get; set; }
        public string VideoCodec { get; set; }         // "auto" | "H.264" | "HEVC" | "AV1"
        public string DisplayMode { get; set; }        // "fullscreen" | "windowed" | "borderless"
        public string AudioConfig { get; set; }        // "stereo" | "5.1-surround" | "7.1-surround"
        public bool? Yuv444 { get; set; }
        public bool? FramePacing { get; set; }
        public bool? GameOptimization { get; set; }
        public bool? ShowStats { get; set; }
        public string ExtraArgs { get; set; }          // advanced, opt-in

        public static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Include,
            DefaultValueHandling = DefaultValueHandling.Include,
            MissingMemberHandling = MissingMemberHandling.Ignore,
        };

        // Merge: `other` wins where set; null/Inherit defers to `this`.
        public StreamOverrides MergedWith(StreamOverrides other)
        {
            if (other == null) return this;
            return new StreamOverrides
            {
                ResolutionMode = other.ResolutionMode ?? ResolutionMode,
                ResolutionStatic = other.ResolutionStatic ?? ResolutionStatic,
                FpsMode = other.FpsMode ?? FpsMode,
                FpsStatic = other.FpsStatic ?? FpsStatic,
                Hdr = other.Hdr ?? Hdr,
                BitrateKbps = other.BitrateKbps ?? BitrateKbps,
                VideoCodec = other.VideoCodec ?? VideoCodec,
                DisplayMode = other.DisplayMode ?? DisplayMode,
                AudioConfig = other.AudioConfig ?? AudioConfig,
                Yuv444 = other.Yuv444 ?? Yuv444,
                FramePacing = other.FramePacing ?? FramePacing,
                GameOptimization = other.GameOptimization ?? GameOptimization,
                ShowStats = other.ShowStats ?? ShowStats,
                ExtraArgs = other.ExtraArgs ?? ExtraArgs,
            };
        }

        public static StreamOverrides BuiltinDefault => new StreamOverrides
        {
            ResolutionMode = Models.ResolutionMode.Auto,
            FpsMode = Models.FpsMode.Auto,
            Hdr = HdrMode.Auto,
        };
    }
}
