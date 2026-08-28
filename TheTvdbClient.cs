using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Chronicle.Plugin.TheTVDB;

/// <summary>
/// HTTP wrapper for the TheTVDB v4 REST API.
/// Handles JWT token acquisition and proactive refresh (every 25 days out of 30).
/// All public methods return null on 404; throw on other non-success status codes.
/// </summary>
internal sealed class TheTvdbClient : IDisposable
{
    private const string BaseUrl         = "https://api4.thetvdb.com/v4";
    private const int    TokenLifetimeDays = 30;
    private const int    RefreshBeforeDays = 5;

    /// <summary>
    /// TheTVDB's real rate-limit model is tier-dependent (free vs. supporter/business account)
    /// and can't be confirmed from this codebase alone — there was previously no way to tell
    /// which regime a given API key falls under, so this is deliberately the safe-regardless-
    /// of-tier fix: a short bounded backoff on 429, not a SIMKL-style daily-quota cutoff (which
    /// would need a confirmed daily-cap number this plugin has no way to know).
    /// </summary>
    private static readonly TimeSpan MaxRetryAfterWait = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient         _http;
    private readonly string             _apiKey;
    private readonly ILogger            _logger;
    private readonly SemaphoreSlim      _tokenLock = new(1, 1);

    private string?         _token;
    private DateTimeOffset  _tokenExpiresAt;

    public TheTvdbClient(HttpClient http, string apiKey, ILogger logger)
    {
        _http   = http;
        _apiKey = apiKey;
        _logger = logger;
    }

    // ── Token ─────────────────────────────────────────────────────────────────

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
            return _token;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
                return _token;

            _logger.LogDebug("TheTVDB: acquiring new JWT token");
            var resp = await _http.PostAsJsonAsync($"{BaseUrl}/login",
                new { apikey = _apiKey }, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadFromJsonAsync<TvdbLoginResponse>(_json, ct)
                           .ConfigureAwait(false);
            _token          = body?.Data?.Token
                ?? throw new InvalidOperationException("TheTVDB login returned no token.");
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddDays(TokenLifetimeDays - RefreshBeforeDays);
            return _token;
        }
        finally { _tokenLock.Release(); }
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new("Bearer", token);
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        // On 401 clear token and retry once — handles revoked/expired tokens
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            _token = null;
            token  = await GetTokenAsync(ct).ConfigureAwait(false);
            using var req2 = new HttpRequestMessage(HttpMethod.Get, url);
            req2.Headers.Authorization = new("Bearer", token);
            resp = await _http.SendAsync(req2, ct).ConfigureAwait(false);
        }

        // On 429, honor Retry-After (capped) and retry once more — previously unhandled here,
        // a rate-limited response just fell through to EnsureSuccessStatusCode() and threw.
        // See MaxRetryAfterWait's own doc for why this is a short bounded backoff, not a
        // SIMKL-style multi-hour cutoff.
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
            var wait = retryAfter > MaxRetryAfterWait ? MaxRetryAfterWait : retryAfter;
            _logger.LogWarning("TheTVDB: rate-limited (429); waiting {Seconds}s before one retry", wait.TotalSeconds);

            await Task.Delay(wait, ct).ConfigureAwait(false);
            using var req3 = new HttpRequestMessage(HttpMethod.Get, url);
            req3.Headers.Authorization = new("Bearer", token);
            resp = await _http.SendAsync(req3, ct).ConfigureAwait(false);
        }

        return resp;
    }

    private async Task<T?> FetchAsync<T>(string url, CancellationToken ct) where T : class
    {
        var resp = await GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        var wrapper = await resp.Content
            .ReadFromJsonAsync<TvdbResponse<T>>(_json, ct).ConfigureAwait(false);
        return wrapper?.Data;
    }

    // ── Search ────────────────────────────────────────────────────────────────

    public async Task<TvdbSearchResult[]?> SearchSeriesAsync(string query, CancellationToken ct)
    {
        var url  = $"{BaseUrl}/search?query={Uri.EscapeDataString(query)}&type=series";
        var resp = await GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return [];
        resp.EnsureSuccessStatusCode();
        var wrapper = await resp.Content
            .ReadFromJsonAsync<TvdbResponse<TvdbSearchResult[]>>(_json, ct).ConfigureAwait(false);
        return wrapper?.Data ?? [];
    }

    // ── Series ────────────────────────────────────────────────────────────────

    public Task<TvdbSeries?> GetSeriesExtendedAsync(long seriesId, CancellationToken ct)
        => FetchAsync<TvdbSeries>(
            $"{BaseUrl}/series/{seriesId}/extended?meta=translations&short=false", ct);

    // ── Seasons ───────────────────────────────────────────────────────────────

    public async Task<TvdbSeason[]?> GetSeasonsAsync(long seriesId, CancellationToken ct)
    {
        var resp = await GetAsync($"{BaseUrl}/series/{seriesId}/seasons/official/extended", ct)
                       .ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        var wrapper = await resp.Content
            .ReadFromJsonAsync<TvdbResponse<TvdbSeason[]>>(_json, ct).ConfigureAwait(false);
        return wrapper?.Data;
    }

    public Task<TvdbSeason?> GetSeasonExtendedAsync(long seasonId, CancellationToken ct)
        => FetchAsync<TvdbSeason>($"{BaseUrl}/seasons/{seasonId}/extended?meta=translations", ct);

    // ── Episodes ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches all episodes for a given season across all pages (TVDB paginates at 100).
    /// </summary>
    public async Task<TvdbEpisode[]> GetEpisodesForSeasonAsync(
        long seriesId, int seasonNumber, CancellationToken ct)
    {
        var all  = new List<TvdbEpisode>();
        var page = 0;

        while (true)
        {
            var url  = $"{BaseUrl}/series/{seriesId}/episodes/official" +
                       $"?season={seasonNumber}&page={page}";
            var resp = await GetAsync(url, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound) break;
            resp.EnsureSuccessStatusCode();

            var paged = await resp.Content
                .ReadFromJsonAsync<TvdbPagedResponse<TvdbEpisode>>(_json, ct)
                .ConfigureAwait(false);

            if (paged?.Data is { Length: > 0 } data)
                all.AddRange(data);

            // Stop when there are no further pages
            if (paged?.Links?.Next is null) break;
            page++;
            await Task.Delay(150, ct).ConfigureAwait(false); // gentle pacing
        }

        return [.. all];
    }

    public Task<TvdbEpisode?> GetEpisodeExtendedAsync(long episodeId, CancellationToken ct)
        => FetchAsync<TvdbEpisode>(
            $"{BaseUrl}/episodes/{episodeId}/extended?meta=translations", ct);

    // ── Slug resolution (Fix Match URLs) ─────────────────────────────────────

    public async Task<long?> ResolveSlugAsync(string slug, CancellationToken ct)
    {
        var results = await SearchSeriesAsync(slug, ct).ConfigureAwait(false);
        var match   = results?.FirstOrDefault(r =>
            string.Equals(r.TvdbId, slug, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormaliseSlug(r.Name), NormaliseSlug(slug), StringComparison.OrdinalIgnoreCase));
        return match?.TvdbId is not null && long.TryParse(match.TvdbId, out var id) ? id : null;
    }

    // ── Health ────────────────────────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            // Breaking Bad — TVDB ID 76290
            var series = await GetSeriesExtendedAsync(76290, ct).ConfigureAwait(false);
            return series is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TheTVDB health check failed");
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string NormaliseSlug(string? s)
        => (s ?? string.Empty)
            .ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("'", "")
            .Replace(":", "");

    public void Dispose() => _tokenLock.Dispose();
}
