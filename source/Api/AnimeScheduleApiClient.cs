using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;

namespace Shoko.Plugin.AnimeSchedule.Api;

/// <summary>
/// Thin HTTP wrapper around the AnimeSchedule.net v3 API: Bearer
/// authentication with the user's own app token, and the two endpoints this
/// plugin needs.
/// </summary>
public sealed class AnimeScheduleApiClient
{
    private static readonly Uri BaseUri = new("https://animeschedule.net/api/v3/");

    // 0 = never logged, 1 = already warned once. Shared across instances so
    // a token that stays unset for the life of the process only warns once,
    // regardless of how many typed HttpClient instances are handed out.
    private static int _hasWarnedMissingToken;

    private readonly HttpClient _http;
    private readonly ILogger<AnimeScheduleApiClient> _logger;
    private readonly AnimeScheduleRateLimiter _rateLimiter;
    private readonly ConfigurationProvider<Configuration> _configurationProvider;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeScheduleApiClient"/>
    /// class.
    /// </summary>
    public AnimeScheduleApiClient(
        HttpClient http,
        ILogger<AnimeScheduleApiClient> logger,
        AnimeScheduleRateLimiter rateLimiter,
        ConfigurationProvider<Configuration> configurationProvider
    )
    {
        _http = http;
        _logger = logger;
        _rateLimiter = rateLimiter;
        _configurationProvider = configurationProvider;
        _http.BaseAddress ??= BaseUri;
    }

    /// <summary>
    /// Looks up the AnimeSchedule.net anime linked to the given AniDB anime
    /// ID via <c>GET /anime?anidb-ids=</c>.
    /// </summary>
    /// <returns>
    /// The matching anime, or <c>null</c> when AnimeSchedule.net does not know
    /// it (or the request failed).
    /// </returns>
    public async Task<AnimeScheduleAnimeInfo?> FindAnimeByAnidbIdAsync(int anidbAnimeId, CancellationToken cancellationToken = default)
    {
        var page = await GetAsync<AnimeScheduleAnimePage>($"anime?anidb-ids={anidbAnimeId}", cancellationToken).ConfigureAwait(false);
        if (page is null || page.Anime.Count == 0)
            return null;

        return page.Anime[0];
    }

    /// <summary>
    /// Fetches one week's timetable for the given air type via
    /// <c>GET /timetables/{raw|sub|dub}?year=&amp;week=</c>, in UTC.
    /// </summary>
    /// <returns>The week's entries, or an empty list on failure.</returns>
    public async Task<IReadOnlyList<AnimeScheduleTimetableEntry>> GetTimetableAsync(
        AnimeScheduleAirType airType,
        int year,
        int week,
        CancellationToken cancellationToken = default
    )
    {
        var segment = airType switch
        {
            AnimeScheduleAirType.Raw => "raw",
            AnimeScheduleAirType.Sub => "sub",
            AnimeScheduleAirType.Dub => "dub",
            _ => throw new ArgumentOutOfRangeException(nameof(airType), airType, null),
        };

        // AnimeSchedule.net does not reject an out-of-range week: 2026 week
        // 54 answers with week 53's payload byte for byte, so a caller that
        // walked off the end of a year would ingest the same week twice under
        // two numbers. ISO 8601 never numbers a week past 53, so anything
        // else is a bug on this side and is dropped instead of fetched.
        if (week is < 1 or > 53)
        {
            _logger.LogDebug("AnimeSchedule.net timetable request skipped: week {Week} of {Year} is not an ISO 8601 week.", week, year);
            return [];
        }

        var entries = await GetAsync<List<AnimeScheduleTimetableEntry>>(
            $"timetables/{segment}?year={year}&week={week}&tz=UTC",
            cancellationToken
        ).ConfigureAwait(false);

        return entries ?? [];
    }

    private async Task<T?> GetAsync<T>(string requestUri, CancellationToken cancellationToken) where T : class
    {
        var token = _configurationProvider.Load().AppToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            // Callers (AnimeScheduleProvider) already gate on the token
            // before reaching here, but fail fast and quietly instead of
            // sending an unauthenticated request AnimeSchedule.net would
            // just reject. Warn once per process, then drop to Debug so a
            // token left unset does not spam the log on every refresh.
            if (Interlocked.Exchange(ref _hasWarnedMissingToken, 1) == 0)
                _logger.LogWarning("AnimeSchedule.net request to {RequestUri} skipped: no app token configured.", requestUri);
            else
                _logger.LogDebug("AnimeSchedule.net request to {RequestUri} skipped: no app token configured.", requestUri);

            return null;
        }

        // One retry: a 429 means our own header-driven throttling fell
        // behind the server's own accounting (e.g. another app on the same
        // IP consumed the window). A second 429 in a row is left alone
        // rather than retried again, so a misconfigured token cannot spin.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "AnimeSchedule.net request to {RequestUri} failed.", requestUri);
                return null;
            }

            using (response)
            {
                _rateLimiter.UpdateFromHeaders(response.Headers, DateTimeOffset.UtcNow);

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
                {
                    _logger.LogWarning("AnimeSchedule.net rate limit hit while requesting {RequestUri}; retrying once.", requestUri);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    // A 404 is a routine answer rather than a failure: an
                    // anime AnimeSchedule.net does not know, and a week with
                    // no timetable at all, both come back as one (with a
                    // plain-text body, not JSON). Sweeping either would warn
                    // once per series or per week, so only a status that
                    // really is unexpected is worth a warning.
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        _logger.LogDebug("AnimeSchedule.net has no data for {RequestUri}.", requestUri);
                    else
                        _logger.LogWarning("AnimeSchedule.net request to {RequestUri} failed with {StatusCode}.", requestUri, response.StatusCode);

                    return null;
                }

                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return null;
    }
}
