using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    public class SeriesInfo
    {
        [JsonPropertyName("series_id")]
        public int SeriesId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("cover")]
        public string Cover { get; set; } = string.Empty;

        [JsonPropertyName("plot")]
        public string Plot { get; set; } = string.Empty;

        [JsonPropertyName("cast")]
        public string Cast { get; set; } = string.Empty;

        [JsonPropertyName("director")]
        public string Director { get; set; } = string.Empty;

        [JsonPropertyName("genre")]
        public string Genre { get; set; } = string.Empty;

        [JsonPropertyName("releaseDate")]
        public string ReleaseDate { get; set; } = string.Empty;

        [JsonPropertyName("rating")]
        public string Rating { get; set; } = string.Empty;

        [JsonPropertyName("category_id")]
        public int? CategoryId { get; set; }

        [JsonPropertyName("category_name")]
        public string CategoryName { get; set; } = string.Empty;

        [JsonPropertyName("last_modified")]
        public string LastModified { get; set; } = string.Empty;

        private string _tmdb = string.Empty;
        private string _tmdbIdField = string.Empty;

        /// <summary>
        /// The series' TMDB id from the listing. FORK: Xtream panels name the field <c>tmdb</c>,
        /// Dispatcharr names it <c>tmdb_id</c>; reading only the first is why series looked like
        /// they carried no TMDB id on the list payload at all (ADR-F009). Either is used, the
        /// panel's own name first.
        /// </summary>
        [JsonPropertyName("tmdb")]
        public string TmdbId
        {
            get { return !string.IsNullOrWhiteSpace(_tmdb) ? _tmdb : (_tmdbIdField ?? string.Empty); }
            set { _tmdb = value ?? string.Empty; }
        }

        /// <summary>Dispatcharr's name for the field; read through <see cref="TmdbId"/>.</summary>
        [JsonPropertyName("tmdb_id")]
        public string TmdbIdField
        {
            get { return _tmdbIdField; }
            set { _tmdbIdField = value ?? string.Empty; }
        }
    }
}
