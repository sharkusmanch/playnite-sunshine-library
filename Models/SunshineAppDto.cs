using Newtonsoft.Json;
using System.Collections.Generic;

namespace SunshineLibrary.Models
{
    /// <summary>Wire format for Sunshine GET /api/apps response.</summary>
    public class SunshineAppsResponse
    {
        [JsonProperty("apps")]
        public List<SunshineAppDto> Apps { get; set; }
    }

    public class SunshineAppDto
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("cmd")]
        public string Cmd { get; set; }

        [JsonProperty("output")]
        public string Output { get; set; }

        [JsonProperty("image-path")]
        public string ImagePath { get; set; }

        [JsonProperty("uuid")]
        public string Uuid { get; set; }       // Apollo sets this; upstream Sunshine usually null

        [JsonProperty("index")]
        public int? Index { get; set; }
    }
}
