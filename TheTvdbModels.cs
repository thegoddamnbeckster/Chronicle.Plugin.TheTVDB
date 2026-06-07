using System.Text.Json.Serialization;

namespace Chronicle.Plugin.TheTVDB;

// ── Auth ─────────────────────────────────────────────────────────────────────

internal sealed record TvdbLoginResponse(
    [property: JsonPropertyName("data")] TvdbLoginData? Data,
    [property: JsonPropertyName("status")] string? Status);

internal sealed record TvdbLoginData(
    [property: JsonPropertyName("token")] string Token);

// ── Generic wrappers ──────────────────────────────────────────────────────────

internal sealed record TvdbResponse<T>(
    [property: JsonPropertyName("data")]   T?      Data,
    [property: JsonPropertyName("status")] string? Status);

internal sealed record TvdbPagedResponse<T>(
    [property: JsonPropertyName("data")]  T[]?     Data,
    [property: JsonPropertyName("links")] TvdbLinks? Links,
    [property: JsonPropertyName("status")] string? Status);

internal sealed record TvdbLinks(
    [property: JsonPropertyName("prev")]       string? Prev,
    [property: JsonPropertyName("next")]       string? Next,
    [property: JsonPropertyName("totalItems")] int     TotalItems,
    [property: JsonPropertyName("pageSize")]   int     PageSize);

// ── Search ────────────────────────────────────────────────────────────────────

internal sealed record TvdbSearchResult(
    [property: JsonPropertyName("tvdb_id")]    string? TvdbId,
    [property: JsonPropertyName("name")]       string? Name,
    [property: JsonPropertyName("first_air_time")] string? FirstAirTime,
    [property: JsonPropertyName("overview")]   string? Overview,
    [property: JsonPropertyName("image_url")]  string? ImageUrl,
    [property: JsonPropertyName("aliases")]    string[]? Aliases);

// ── Series ────────────────────────────────────────────────────────────────────

internal sealed record TvdbSeries(
    [property: JsonPropertyName("id")]              long          Id,
    [property: JsonPropertyName("name")]            string        Name,
    [property: JsonPropertyName("slug")]            string?       Slug,
    [property: JsonPropertyName("overview")]        string?       Overview,
    [property: JsonPropertyName("firstAired")]      string?       FirstAired,
    [property: JsonPropertyName("score")]           double?       Score,
    [property: JsonPropertyName("averageRuntime")]  int?          AverageRuntime,
    [property: JsonPropertyName("originalCountry")] string?       OriginalCountry,
    [property: JsonPropertyName("originalLanguage")]string?       OriginalLanguage,
    [property: JsonPropertyName("status")]          TvdbStatus?   Status,
    [property: JsonPropertyName("latestNetwork")]   TvdbNetwork?  LatestNetwork,
    [property: JsonPropertyName("genres")]          TvdbGenre[]?  Genres,
    [property: JsonPropertyName("artworks")]        TvdbArtwork[]? Artworks,
    [property: JsonPropertyName("characters")]      TvdbCharacter[]? Characters,
    [property: JsonPropertyName("remoteIds")]       TvdbRemoteId[]? RemoteIds,
    [property: JsonPropertyName("translations")]    TvdbTranslationContainer? Translations);

// ── Season ────────────────────────────────────────────────────────────────────

internal sealed record TvdbSeason(
    [property: JsonPropertyName("id")]       long          Id,
    [property: JsonPropertyName("number")]   int           Number,
    [property: JsonPropertyName("year")]     int?          Year,
    [property: JsonPropertyName("name")]     string?       Name,
    [property: JsonPropertyName("overview")] string?       Overview,
    [property: JsonPropertyName("image")]    string?       Image,
    [property: JsonPropertyName("artwork")]  TvdbArtwork[]? Artwork,
    [property: JsonPropertyName("translations")] TvdbTranslationContainer? Translations);

// ── Episode ───────────────────────────────────────────────────────────────────

internal sealed record TvdbEpisode(
    [property: JsonPropertyName("id")]              long     Id,
    [property: JsonPropertyName("seasonNumber")]    int?     SeasonNumber,
    [property: JsonPropertyName("number")]          int?     Number,
    [property: JsonPropertyName("name")]            string?  Name,
    [property: JsonPropertyName("overview")]        string?  Overview,
    [property: JsonPropertyName("aired")]           string?  Aired,
    [property: JsonPropertyName("runtime")]         int?     Runtime,
    [property: JsonPropertyName("score")]           double?  Score,
    [property: JsonPropertyName("image")]           string?  Image,
    [property: JsonPropertyName("characters")]      TvdbCharacter[]? Characters,
    [property: JsonPropertyName("translations")]    TvdbTranslationContainer? Translations);

// ── Series + season extended wrappers ────────────────────────────────────────

internal sealed record TvdbSeriesEpisodesData(
    [property: JsonPropertyName("series")]   TvdbSeries?   Series,
    [property: JsonPropertyName("episodes")] TvdbEpisode[]? Episodes);

// ── Shared sub-types ──────────────────────────────────────────────────────────

internal sealed record TvdbArtwork(
    [property: JsonPropertyName("id")]       long    Id,
    [property: JsonPropertyName("image")]    string? Image,
    [property: JsonPropertyName("thumbnail")]string? Thumbnail,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("type")]     long    Type,
    [property: JsonPropertyName("score")]    double? Score);

internal sealed record TvdbCharacter(
    [property: JsonPropertyName("id")]         long    Id,
    [property: JsonPropertyName("name")]       string? Name,
    [property: JsonPropertyName("personName")] string? PersonName,
    [property: JsonPropertyName("type")]       int     Type);
    // type 3 = Actor, 4 = Director, 7 = Writer

internal sealed record TvdbGenre(
    [property: JsonPropertyName("id")]   long   Id,
    [property: JsonPropertyName("name")] string Name);

internal sealed record TvdbStatus(
    [property: JsonPropertyName("name")] string Name);

internal sealed record TvdbNetwork(
    [property: JsonPropertyName("name")] string Name);

internal sealed record TvdbRemoteId(
    [property: JsonPropertyName("id")]         string Id,
    [property: JsonPropertyName("sourceName")] string SourceName);

internal sealed record TvdbTranslationContainer(
    [property: JsonPropertyName("nameTranslations")]     TvdbTranslation[]? NameTranslations,
    [property: JsonPropertyName("overviewTranslations")] TvdbTranslation[]? OverviewTranslations);

internal sealed record TvdbTranslation(
    [property: JsonPropertyName("name")]     string? Name,
    [property: JsonPropertyName("overview")] string? Overview,
    [property: JsonPropertyName("language")] string? Language);

// ── Artwork type IDs used by TVDB v4 ─────────────────────────────────────────

internal static class TvdbArtworkType
{
    public const long Banner         = 1;
    public const long Poster         = 2;
    public const long Background     = 3;
    public const long Icon           = 5;
    public const long SeasonPoster   = 7;
    public const long SeasonBanner   = 8;
    public const long ClearLogo      = 23;
    public const long ClearArt       = 24;
}
