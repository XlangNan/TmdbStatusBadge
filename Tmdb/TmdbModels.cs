using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TmdbStatusBadge.Tmdb
{
    public class TmdbTvDetail
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Returning Series / Ended / Canceled / In Production / Planned
        /// </summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("number_of_episodes")]
        public int NumberOfEpisodes { get; set; }

        [JsonPropertyName("number_of_seasons")]
        public int NumberOfSeasons { get; set; }

        [JsonPropertyName("next_episode_to_air")]
        public TmdbEpisodeRef? NextEpisodeToAir { get; set; }

        [JsonPropertyName("seasons")]
        public List<TmdbSeasonRef> Seasons { get; set; } = new();

        public bool IsEnded => Status is "Ended" or "Canceled";
    }

    public class TmdbEpisodeRef
    {
        [JsonPropertyName("air_date")]
        public string? AirDate { get; set; }

        [JsonPropertyName("season_number")]
        public int SeasonNumber { get; set; }

        [JsonPropertyName("episode_number")]
        public int EpisodeNumber { get; set; }
    }

    public class TmdbSeasonRef
    {
        [JsonPropertyName("season_number")]
        public int SeasonNumber { get; set; }

        [JsonPropertyName("episode_count")]
        public int EpisodeCount { get; set; }
    }
}
