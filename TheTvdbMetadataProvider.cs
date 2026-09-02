using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Chronicle.Plugin.TheTVDB;

/// <summary>
/// Chronicle metadata provider for TheTVDB v4.
///
/// Search strategy by hierarchy level:
///   0 (Show)    — KnownExternalIds["tvdb"] → direct fetch; else text search + year scoring
///   1 (Season)  — derive series ID + season number from KnownExternalIds; fetch seasons list
///   2 (Episode) — derive series ID + season; fetch episode list; match by S+E number,
///                 fall back to title match when numbering systems diverge (TVDB vs TMDB)
///
/// External ID formats stored (source = "thetvdb"):
///   series:{tvdbSeriesId}              — TV show
///   series:{tvdbSeriesId}/season:{n}   — season
///   episode:{tvdbEpisodeId}            — episode
///
/// Cross-reference IDs read from KnownExternalIds:
///   "tvdb"         — raw numeric string stored by Trakt/SIMKL ("76290")
///   "parent_tvdb"  — parent show's raw TVDB ID (injected by enrichment pipeline)
///   "thetvdb"      — own stored ID in formats above
/// </summary>
public sealed class TheTvdbMetadataProvider : IMetadataProvider, IDisposable
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public string PluginId => "chronicle.plugin.thetvdb";
    public string Name     => "TheTVDB";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    // ── Settings keys ─────────────────────────────────────────────────────────

    private const string KeyApiKey           = "api_key";
    private const string KeyLanguage         = "language";
    private const string KeyFallbackLanguage = "fallback_language";

    // ── Live state ────────────────────────────────────────────────────────────

    private TheTvdbClient? _client;
    private HttpClient?    _ownedHttp;
    private string         _language         = "eng";
    private string         _fallbackLanguage = "eng";
    private readonly ILogger _logger;

    public TheTvdbMetadataProvider()
        : this(NullLogger.Instance) { }

    public TheTvdbMetadataProvider(ILogger logger) => _logger = logger;

    internal TheTvdbMetadataProvider(TheTvdbClient client, string language = "eng")
        : this(NullLogger.Instance)
    {
        _client   = client;
        _language = language;
    }

    // ── Supported types ───────────────────────────────────────────────────────

    private static readonly MediaTypeSupport[] _supportedTypes =
    [
        new MediaTypeSupport
        {
            MediaTypeName   = "tv",
            DisplayName     = "TV",
            HierarchyLevels = 3,
            HierarchyLabels = ["Show", "Season", "Episode"],
            DefaultPriority = 15,
            SupportedFields = ["title", "overview", "year", "poster_url", "backdrop_url",
                               "banner_url", "genres", "cast", "crew", "rating", "tags"],
            LevelFields = new Dictionary<int, List<string>>
            {
                [1] = ["title", "overview", "year", "poster_url", "banner_url"],
                [2] = ["title", "overview", "year", "runtime_minutes", "rating", "crew", "cast"],
            },
        },
        new MediaTypeSupport
        {
            MediaTypeName   = "anime",
            DisplayName     = "Anime",
            HierarchyLevels = 3,
            HierarchyLabels = ["Show", "Season", "Episode"],
            DefaultPriority = 15,
            SupportedFields = ["title", "overview", "year", "poster_url", "backdrop_url",
                               "banner_url", "genres", "cast", "crew", "rating", "tags"],
            LevelFields = new Dictionary<int, List<string>>
            {
                [1] = ["title", "overview", "year", "poster_url", "banner_url"],
                [2] = ["title", "overview", "year", "runtime_minutes", "rating", "crew", "cast"],
            },
        },
    ];

    public MediaTypeSupport[] GetSupportedMediaTypes() => _supportedTypes;

    // ── Settings schema ───────────────────────────────────────────────────────

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key         = KeyApiKey,
                Label       = "TheTVDB API Key",
                Description = "Your TheTVDB API key. Get one free at https://thetvdb.com/dashboard.",
                Type        = SettingType.Password,
                Required    = true,
            },
            new SettingDefinition
            {
                Key          = KeyLanguage,
                Label        = "Preferred Language",
                Description  = "ISO 639-2 three-letter code (e.g. eng, fra, deu, jpn). " +
                               "Note: TVDB uses 3-letter codes unlike TMDB's 2-letter codes.",
                Type         = SettingType.Text,
                Required     = false,
                DefaultValue = "eng",
            },
            new SettingDefinition
            {
                Key          = KeyFallbackLanguage,
                Label        = "Fallback Language",
                Description  = "Language used when no translation exists in the preferred language.",
                Type         = SettingType.Text,
                Required     = false,
                DefaultValue = "eng",
            },
        ],
    };

    // ── Configuration ─────────────────────────────────────────────────────────

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        settings.TryGetValue(KeyApiKey,           out var apiKey);
        settings.TryGetValue(KeyLanguage,         out var lang);
        settings.TryGetValue(KeyFallbackLanguage, out var fallback);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("TheTVDB plugin is not configured: 'api_key' is missing. Searches will be unavailable until the key is set in Settings → Plugins.");
            _client = null;
            return;
        }

        _ownedHttp?.Dispose();
        var http = new HttpClient { DefaultRequestHeaders = { { "User-Agent", "Chronicle/1.0" } } };
        _ownedHttp        = http;
        _client           = new TheTvdbClient(http, apiKey, _logger);
        _language         = string.IsNullOrWhiteSpace(lang)     ? "eng" : lang.Trim();
        _fallbackLanguage = string.IsNullOrWhiteSpace(fallback) ? "eng" : fallback.Trim();

        _logger.LogInformation("TheTVDB plugin configured (language: {Lang})", _language);
    }

    // ── SearchAsync ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();

        return context.HierarchyLevel switch
        {
            0 => await SearchShowAsync(context, ct).ConfigureAwait(false),
            1 => await SearchSeasonAsync(context, ct).ConfigureAwait(false),
            2 => await SearchEpisodeAsync(context, ct).ConfigureAwait(false),
            _ => [],
        };
    }

    // Level 0 — TV show ───────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ScoredCandidate>> SearchShowAsync(
        MediaSearchContext context, CancellationToken ct)
    {
        // 1. Direct fetch if we have a known TVDB ID (from Trakt/SIMKL or own stored ID)
        var seriesId = ExtractSeriesId(context.KnownExternalIds);
        if (seriesId.HasValue)
        {
            var series = await _client!.GetSeriesExtendedAsync(seriesId.Value, ct)
                             .ConfigureAwait(false);
            if (series is not null)
                return [new ScoredCandidate(MapSeries(series), 100, "known TVDB ID")];
        }

        // 2. Text search
        var results = await _client!.SearchSeriesAsync(context.Name, ct).ConfigureAwait(false);
        if (results is null or { Length: 0 }) return [];

        var candidates = new List<ScoredCandidate>();
        foreach (var r in results.Take(5))
        {
            if (!long.TryParse(r.TvdbId, out var id)) continue;

            var score = ScoreSearchResult(r, context);
            if (score < 40) continue;

            var meta = new MediaMetadata
            {
                ExternalId  = $"series:{id}",
                Source      = "thetvdb",
                Title       = r.Name ?? context.Name,
                Overview    = r.Overview,
                Year        = ParseYear(r.FirstAirTime),
                PosterUrl   = r.ImageUrl,
            };
            candidates.Add(new ScoredCandidate(meta, score, "text search"));
        }

        return candidates.OrderByDescending(c => c.Score).ToList();
    }

    // Level 1 — Season ────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ScoredCandidate>> SearchSeasonAsync(
        MediaSearchContext context, CancellationToken ct)
    {
        var seriesId = ExtractSeriesId(context.KnownExternalIds);
        if (!seriesId.HasValue)
        {
            _logger.LogDebug("TheTVDB: no series ID available for season '{Name}' — skipping", context.Name);
            return [];
        }

        var seasonNumber = context.ItemNumber;
        if (!seasonNumber.HasValue)
        {
            _logger.LogDebug("TheTVDB: no season number in context for series {Id}", seriesId);
            return [];
        }

        var seasons = await _client!.GetSeasonsAsync(seriesId.Value, ct).ConfigureAwait(false);
        var season  = seasons?.FirstOrDefault(s => s.Number == seasonNumber.Value);
        if (season is null)
        {
            _logger.LogDebug("TheTVDB: season {N} not found for series {Id}", seasonNumber, seriesId);
            return [];
        }

        // Fetch extended data for translations and artwork
        var extended = await _client.GetSeasonExtendedAsync(season.Id, ct).ConfigureAwait(false)
                       ?? season;

        return [new ScoredCandidate(MapSeason(extended, seriesId.Value), 100, "season number match")];
    }

    // Level 2 — Episode ───────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ScoredCandidate>> SearchEpisodeAsync(
        MediaSearchContext context, CancellationToken ct)
    {
        var seriesId = ExtractSeriesId(context.KnownExternalIds);
        if (!seriesId.HasValue)
        {
            _logger.LogDebug("TheTVDB: no series ID for episode '{Name}' — skipping", context.Name);
            return [];
        }

        // Derive season number: from parent_thetvdb ("series:N/season:M") or parent_tvdb context
        var seasonNumber = ExtractParentSeasonNumber(context.KnownExternalIds);
        if (!seasonNumber.HasValue)
        {
            _logger.LogDebug("TheTVDB: no season number for episode '{Name}' (series {Id})",
                context.Name, seriesId);
            return [];
        }

        var episodes = await _client!
            .GetEpisodesForSeasonAsync(seriesId.Value, seasonNumber.Value, ct)
            .ConfigureAwait(false);

        if (episodes.Length == 0) return [];

        // Primary: match by episode number
        TvdbEpisode? match = null;
        var scoreReason    = string.Empty;

        if (context.ItemNumber.HasValue)
        {
            match = episodes.FirstOrDefault(e => e.Number == context.ItemNumber.Value
                                                 && e.SeasonNumber == seasonNumber.Value);
            if (match is not null)
                scoreReason = $"S{seasonNumber:D2}E{context.ItemNumber:D2} match";
        }

        // Fallback: title match (handles TVDB/TMDB episode numbering divergence)
        if (match is null && !string.IsNullOrWhiteSpace(context.Name))
        {
            match = episodes.FirstOrDefault(e =>
                NormaliseName(e.Name).Contains(NormaliseName(context.Name),
                    StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                _logger.LogWarning(
                    "TheTVDB: episode S{S:D2}E{E:D2} '{Name}' not found by number for series {Id} — " +
                    "matched by title (TVDB/TMDB numbering may differ). Matched: '{Matched}'",
                    seasonNumber, context.ItemNumber, context.Name, seriesId, match.Name);
                scoreReason = "title fallback (numbering mismatch)";
            }
        }

        if (match is null)
        {
            _logger.LogDebug("TheTVDB: no episode match for '{Name}' in S{S} series {Id}",
                context.Name, seasonNumber, seriesId);
            return [];
        }

        // Fetch extended episode for full translations/cast
        var extended = await _client.GetEpisodeExtendedAsync(match.Id, ct).ConfigureAwait(false)
                       ?? match;

        return [new ScoredCandidate(MapEpisode(extended), 100, scoreReason)];
    }

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();
        externalId = NormaliseTvdbUrl(externalId);

        // episode:{id}
        if (externalId.StartsWith("episode:", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(externalId[8..], out var epId))
        {
            var ep = await _client!.GetEpisodeExtendedAsync(epId, ct).ConfigureAwait(false);
            return ep is not null ? MapEpisode(ep) : EmptyResult(externalId);
        }

        // series:{id}/season:{n}
        var seasonMatch = _seasonIdRe.Match(externalId);
        if (seasonMatch.Success
            && long.TryParse(seasonMatch.Groups[1].Value, out var sid)
            && int.TryParse(seasonMatch.Groups[2].Value, out var sn))
        {
            var seasons = await _client!.GetSeasonsAsync(sid, ct).ConfigureAwait(false);
            var season  = seasons?.FirstOrDefault(s => s.Number == sn);
            if (season is null) return EmptyResult(externalId);
            var ext = await _client.GetSeasonExtendedAsync(season.Id, ct).ConfigureAwait(false)
                      ?? season;
            return MapSeason(ext, sid);
        }

        // series:{id}  or bare numeric
        var seriesIdStr = externalId.StartsWith("series:", StringComparison.OrdinalIgnoreCase)
            ? externalId[7..] : externalId;

        if (!long.TryParse(seriesIdStr, out var seriesId))
        {
            // Treat as slug — resolve to numeric ID
            var resolved = await _client!.ResolveSlugAsync(seriesIdStr, ct).ConfigureAwait(false);
            if (!resolved.HasValue) return EmptyResult(externalId);
            seriesId = resolved.Value;
        }

        var series = await _client!.GetSeriesExtendedAsync(seriesId, ct).ConfigureAwait(false);
        return series is not null ? MapSeries(series) : EmptyResult(externalId);
    }

    private static MediaMetadata EmptyResult(string externalId) =>
        new() { ExternalId = externalId, Source = "thetvdb" };

    // ── Image + health ────────────────────────────────────────────────────────

    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
        => throw new NotSupportedException(
            "TheTVDB provides direct image URLs; use PosterUrl/BackdropUrl etc. directly.");

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null)
        {
            _logger.LogWarning("TheTVDB health check skipped — plugin not configured");
            return false;
        }
        return await _client.HealthCheckAsync(ct).ConfigureAwait(false);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _client?.Dispose();
        _ownedHttp?.Dispose();
        _ownedHttp = null;
        _client    = null;
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private MediaMetadata MapSeries(TvdbSeries s)
    {
        var translation = PickTranslation(s.Translations?.NameTranslations)
                       ?? PickTranslation(s.Translations?.OverviewTranslations);

        var extra = new Dictionary<string, object?>();
        if (s.LatestNetwork?.Name is not null)  extra["network"]         = s.LatestNetwork.Name;
        if (s.Status?.Name is not null)          extra["status"]          = s.Status.Name;
        if (s.OriginalCountry is not null)       extra["country"]         = s.OriginalCountry;

        var imdb   = s.RemoteIds?.FirstOrDefault(r => r.SourceName == "IMDB")?.Id;
        var zap2it = s.RemoteIds?.FirstOrDefault(r => r.SourceName == "Zap2It")?.Id;
        if (imdb   is not null) extra["imdb"]   = imdb;
        if (zap2it is not null) extra["zap2it"] = zap2it;

        return new MediaMetadata
        {
            ExternalId   = $"series:{s.Id}",
            Source       = "thetvdb",
            Title        = translation?.Name ?? s.Name,
            Overview     = translation?.Overview ?? s.Overview,
            Year         = ParseYear(s.FirstAired),
            PosterUrl    = BestArtwork(s.Artworks, TvdbArtworkType.Poster),
            BackdropUrl  = BestArtwork(s.Artworks, TvdbArtworkType.Background),
            BannerUrl    = BestArtwork(s.Artworks, TvdbArtworkType.Banner),
            Genres       = s.Genres?.Select(g => g.Name).ToList() ?? [],
            Cast         = ExtractCast(s.Characters, 3),   // 3 = Actor
            Crew         = ExtractCrew(s.Characters),
            Rating       = s.Score,
            RuntimeMinutes = s.AverageRuntime,
            ExtendedData = extra.Count > 0
                ? JsonSerializer.SerializeToElement(extra)
                : null,
        };
    }

    private MediaMetadata MapSeason(TvdbSeason s, long seriesId)
    {
        var translation = PickTranslation(s.Translations?.NameTranslations)
                       ?? PickTranslation(s.Translations?.OverviewTranslations);

        return new MediaMetadata
        {
            ExternalId  = $"series:{seriesId}/season:{s.Number}",
            Source      = "thetvdb",
            Title       = translation?.Name ?? s.Name ?? $"Season {s.Number}",
            Overview    = translation?.Overview ?? s.Overview,
            Year        = s.Year,
            PosterUrl   = BestArtwork(s.Artwork, TvdbArtworkType.SeasonPoster)
                          ?? EnsureHttps(s.Image),
            BannerUrl   = BestArtwork(s.Artwork, TvdbArtworkType.SeasonBanner),
        };
    }

    private MediaMetadata MapEpisode(TvdbEpisode e)
    {
        var translation = PickTranslation(e.Translations?.NameTranslations)
                       ?? PickTranslation(e.Translations?.OverviewTranslations);

        return new MediaMetadata
        {
            ExternalId     = $"episode:{e.Id}",
            Source         = "thetvdb",
            Title          = translation?.Name ?? e.Name ?? string.Empty,
            Overview       = translation?.Overview ?? e.Overview,
            Year           = ParseYear(e.Aired),
            RuntimeMinutes = e.Runtime,
            Rating         = e.Score,
            Cast           = ExtractCast(e.Characters, 3),
            Crew           = ExtractCrew(e.Characters),
            PosterUrl      = EnsureHttps(e.Image),
        };
    }

    // ── Artwork selection ─────────────────────────────────────────────────────

    private string? BestArtwork(TvdbArtwork[]? artworks, long typeId)
    {
        if (artworks is null or { Length: 0 }) return null;

        var typed = artworks.Where(a => a.Type == typeId && a.Image is not null).ToList();
        if (typed.Count == 0) return null;

        // Prefer: configured language > fallback language > null/eng > highest score
        var preferred = typed.Where(a => string.Equals(a.Language, _language, StringComparison.OrdinalIgnoreCase));
        var fallback  = typed.Where(a => string.Equals(a.Language, _fallbackLanguage, StringComparison.OrdinalIgnoreCase));
        var neutral   = typed.Where(a => a.Language is null or "eng");

        var source = preferred.MaxBy(a => a.Score)
                  ?? fallback.MaxBy(a => a.Score)
                  ?? neutral.MaxBy(a => a.Score)
                  ?? typed.MaxBy(a => a.Score);

        return EnsureHttps(source?.Image);
    }

    // ── Translation selection ─────────────────────────────────────────────────

    private TvdbTranslation? PickTranslation(TvdbTranslation[]? list)
    {
        if (list is null or { Length: 0 }) return null;
        return list.FirstOrDefault(t => string.Equals(t.Language, _language, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(t => string.Equals(t.Language, _fallbackLanguage, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(t => string.Equals(t.Language, "eng", StringComparison.OrdinalIgnoreCase));
    }

    // ── ID extraction ─────────────────────────────────────────────────────────

    private static long? ExtractSeriesId(IReadOnlyDictionary<string, string>? ids)
    {
        if (ids is null) return null;

        // Raw numeric from Trakt/SIMKL ("76290") — source "tvdb"
        if (ids.TryGetValue("tvdb", out var raw) && long.TryParse(raw, out var n1))
            return n1;

        // Own stored ID: "series:76290" or "series:76290/season:2"
        if (ids.TryGetValue("thetvdb", out var own))
        {
            var str = own.StartsWith("series:", StringComparison.OrdinalIgnoreCase) ? own[7..] : own;
            var slash = str.IndexOf('/');
            if (slash > 0) str = str[..slash];
            if (long.TryParse(str, out var n2)) return n2;
        }

        // Parent show's TVDB ID (injected by enrichment pipeline as "parent_tvdb")
        if (ids.TryGetValue("parent_tvdb", out var parentRaw) && long.TryParse(parentRaw, out var n3))
            return n3;

        // Parent's own stored ID ("parent_thetvdb" = "series:76290" or "series:76290/season:2")
        if (ids.TryGetValue("parent_thetvdb", out var parentOwn))
        {
            var str = parentOwn.StartsWith("series:", StringComparison.OrdinalIgnoreCase)
                ? parentOwn[7..] : parentOwn;
            var slash = str.IndexOf('/');
            if (slash > 0) str = str[..slash];
            if (long.TryParse(str, out var n4)) return n4;
        }

        return null;
    }

    private static int? ExtractParentSeasonNumber(IReadOnlyDictionary<string, string>? ids)
    {
        if (ids is null) return null;

        // parent_thetvdb = "series:76290/season:2"
        if (ids.TryGetValue("parent_thetvdb", out var parentOwn))
        {
            var m = _seasonIdRe.Match(parentOwn);
            if (m.Success && int.TryParse(m.Groups[2].Value, out var sn)) return sn;
        }

        return null;
    }

    // ── Scoring ───────────────────────────────────────────────────────────────

    private static int ScoreSearchResult(TvdbSearchResult result, MediaSearchContext context)
    {
        var score = 0;
        var name  = result.Name ?? string.Empty;

        if (string.Equals(NormaliseName(name), NormaliseName(context.Name),
                StringComparison.OrdinalIgnoreCase))
            score += 60;
        else if (NormaliseName(name).Contains(NormaliseName(context.Name),
                     StringComparison.OrdinalIgnoreCase))
            score += 35;

        if (context.Year.HasValue)
        {
            var year = ParseYear(result.FirstAirTime);
            if (year.HasValue && Math.Abs(year.Value - context.Year.Value) <= 1)
                score += 30;
        }
        else
        {
            score += 10; // no year to mismatch on
        }

        return Math.Min(score, 99); // 100 is reserved for direct ID matches
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static readonly Regex _seasonIdRe =
        new(@"series:(\d+)/season:(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _tvdbSeriesUrlRe =
        new(@"thetvdb\.com/series/([^/?#]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _tvdbEpisodeUrlRe =
        new(@"thetvdb\.com/.*?/episodes/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string NormaliseTvdbUrl(string id)
    {
        if (!id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return id;

        // https://thetvdb.com/series/breaking-bad       → breaking-bad (slug, resolved later)
        // https://thetvdb.com/series/76290              → series:76290
        // https://thetvdb.com/.../episodes/308662       → episode:308662
        var epMatch = _tvdbEpisodeUrlRe.Match(id);
        if (epMatch.Success) return $"episode:{epMatch.Groups[1].Value}";

        var seriesMatch = _tvdbSeriesUrlRe.Match(id);
        if (seriesMatch.Success)
        {
            var slug = seriesMatch.Groups[1].Value;
            return long.TryParse(slug, out _) ? $"series:{slug}" : slug;
        }

        return id;
    }

    private static List<CastMember> ExtractCast(TvdbCharacter[]? chars, int type)
        => chars?
            .Where(c => c.Type == type && c.PersonName is not null)
            .GroupBy(c => c.PersonName!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CastMember(g.Key, g.First().Name))
            .ToList()
           ?? [];

    // TVDB character "type" codes for non-actor credits it also surfaces via the same
    // characters array. Only Director(4) and Writer(7) are documented/observed here;
    // any other type code is left uncaptured rather than guessed at.
    private static readonly Dictionary<int, string> _crewJobByType = new() { [4] = "Director", [7] = "Writer" };

    private static List<CrewMember> ExtractCrew(TvdbCharacter[]? chars)
        => chars?
            .Where(c => _crewJobByType.ContainsKey(c.Type) && c.PersonName is not null)
            .GroupBy(c => (c.PersonName!, c.Type))
            .Select(g => new CrewMember(g.Key.Item1, _crewJobByType[g.Key.Type]))
            .ToList()
           ?? [];

    private static int? ParseYear(string? dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr)) return null;
        return DateTime.TryParse(dateStr, out var d) ? d.Year : null;
    }

    private static string? EnsureHttps(string? url)
    {
        if (url is null) return null;
        return url.StartsWith("//") ? $"https:{url}" : url;
    }

    private static string NormaliseName(string? s)
        => (s ?? string.Empty)
            .ToLowerInvariant()
            .Replace("'", "")
            .Replace("\"", "")
            .Replace(":", "")
            .Trim();

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new InvalidOperationException(
                "TheTVDB plugin is not configured. Call Configure() first.");
    }
}
