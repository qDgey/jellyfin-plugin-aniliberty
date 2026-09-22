using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AniLiberty.Api;

// Only the fields the plugin uses; AniLiberty API v1 (snake_case).

public class ValueDescription
{
    public string? Value { get; set; }
    public string? Description { get; set; }
}

public class ReleaseName
{
    public string? Main { get; set; }
    public string? English { get; set; }
    public string? Alternative { get; set; }
}

public class ExternalRef
{
    public long? Id { get; set; }
    public double? Rating { get; set; }
}

public class ImageSet
{
    public string? Src { get; set; }
    public string? Preview { get; set; }
    public string? Thumbnail { get; set; }
}

public class AgeRating
{
    public string? Value { get; set; }
    public string? Label { get; set; }
}

public class Genre
{
    public long Id { get; set; }
    public string? Name { get; set; }
}

public class Member
{
    public ValueDescription? Role { get; set; }
    public string? Nickname { get; set; }
}

public class ReleaseEpisode
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    [JsonPropertyName("name_english")]
    public string? NameEnglish { get; set; }
    public double? Ordinal { get; set; }
    public int? Duration { get; set; }
    public ImageSet? Preview { get; set; }
    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class Release
{
    public long Id { get; set; }
    public ValueDescription? Type { get; set; }
    public int? Year { get; set; }
    public ReleaseName? Name { get; set; }
    public string? Alias { get; set; }
    public ExternalRef? Shikimori { get; set; }
    public ExternalRef? Mal { get; set; }
    public ImageSet? Poster { get; set; }
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }
    [JsonPropertyName("is_ongoing")]
    public bool IsOngoing { get; set; }
    [JsonPropertyName("age_rating")]
    public AgeRating? AgeRating { get; set; }
    public string? Description { get; set; }
    [JsonPropertyName("episodes_total")]
    public int? EpisodesTotal { get; set; }
    [JsonPropertyName("average_duration_of_episode")]
    public int? AverageDurationOfEpisode { get; set; }
    public List<Genre>? Genres { get; set; }
    public List<Member>? Members { get; set; }
    public List<ReleaseEpisode>? Episodes { get; set; }
}

public class Torrent
{
    public long Id { get; set; }
    public string? Hash { get; set; }
    public string? Magnet { get; set; }
    public string? Label { get; set; }
    public string? Filename { get; set; }
    public Release? Release { get; set; }
}

public class Pagination
{
    public int Total { get; set; }
    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }
    [JsonPropertyName("current_page")]
    public int CurrentPage { get; set; }
}

public class PageMeta
{
    public Pagination? Pagination { get; set; }
}

public class Paged<T>
{
    public List<T> Data { get; set; } = new();
    public PageMeta? Meta { get; set; }
}

// Shikimori API

public class ShikiImage
{
    public string? Original { get; set; }
    public string? Preview { get; set; }
}

public class ShikiGenre
{
    public string? Name { get; set; }
    public string? Russian { get; set; }
    public string? Kind { get; set; }
}

public class ShikiAnime
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Russian { get; set; }
    public ShikiImage? Image { get; set; }
    public string? Kind { get; set; }
    public string? Score { get; set; }
    public string? Status { get; set; }
    public int? Episodes { get; set; }
    [JsonPropertyName("aired_on")]
    public string? AiredOn { get; set; }
    [JsonPropertyName("released_on")]
    public string? ReleasedOn { get; set; }
    public string? Rating { get; set; }
    public List<string?>? English { get; set; }
    public List<string?>? Japanese { get; set; }
    public List<string?>? Synonyms { get; set; }
    public int? Duration { get; set; }
    public string? Description { get; set; }
    [JsonPropertyName("myanimelist_id")]
    public long? MyAnimeListId { get; set; }
    public List<ShikiGenre>? Genres { get; set; }
}
