using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    public class Category
    {
        // Strict on purpose: a category without a usable ID should fail loudly (ADR-010).
        [JsonPropertyName("category_id")]
        public int CategoryId { get; set; }

        // Tolerant converters on the properties rather than in the options, because category
        // lists are parsed from several places with different options, and one off-type value
        // fails the whole list (ADR-010).
        [JsonPropertyName("category_name")]
        [JsonConverter(typeof(TolerantStringConverter))]
        public string CategoryName { get; set; } = string.Empty;

        [JsonPropertyName("parent_id")]
        [JsonConverter(typeof(TolerantNullableIntConverter))]
        public int? ParentId { get; set; }
    }
}
