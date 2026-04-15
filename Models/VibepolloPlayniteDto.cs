using Newtonsoft.Json;
using System.Collections.Generic;

namespace SunshineLibrary.Models
{
    /// <summary>Wire format for Vibepollo GET /api/playnite/games — returns a JSON array directly.</summary>
    public class VibepolloPlayniteGameDto
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("categories")]
        public List<string> Categories { get; set; }

        [JsonProperty("installed")]
        public bool Installed { get; set; }

        [JsonProperty("pluginId")]
        public string PluginId { get; set; }

        /// <summary>Library source display name, e.g. "Steam", "GOG", "Epic Games".</summary>
        [JsonProperty("pluginName")]
        public string PluginName { get; set; }

        /// <summary>
        /// Total playtime in minutes. Not yet serialized by Vibepollo's API — field
        /// will be zero until a future release exposes it.
        /// </summary>
        [JsonProperty("playtimeMinutes")]
        public ulong PlaytimeMinutes { get; set; }
    }
}
