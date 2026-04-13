using Newtonsoft.Json;
using System.Collections.Generic;

namespace SunshineLibrary.Models
{
    /// <summary>
    /// Apollo uses the same admin API shape as Sunshine for /api/apps but guarantees `uuid`.
    /// Kept as a thin alias so flavor-specific extensions can attach here without bloating
    /// the Sunshine DTO.
    /// </summary>
    public class ApolloAppsResponse
    {
        [JsonProperty("apps")]
        public List<ApolloAppDto> Apps { get; set; }
    }

    public class ApolloAppDto : SunshineAppDto
    {
        // Apollo-only extensions land here as they are added (client_permissions, etc.)
    }
}
