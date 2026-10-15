using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
/// authentication with the user's own app token, and the endpoints this
/// plugin needs.
/// </summary>
public sealed class AnimeScheduleApiClient
{
    private static readonly Uri BaseUri = new("https://animeschedule.net/api/v3/");

    // The longest wait a 429 may ask for and still be retried in place.
    private static readonly TimeSpan MaxRateLimitRetryWait = TimeSpan.FromMinutes(1);

    // The shortest suspension a second 429 in a row starts.
    private static readonly TimeSpan MinimumRateLimitSuspension = TimeSpan.FromMinutes(1);

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
    /// How many anime one page of the <c>/anime</c> search holds, and so how
    /// many AniDB IDs one lookup asks for at most.
    /// </summary>
    public const int PageSize = 18;

    /// <summary>
    /// The most pages one listing reads, so a misbehaving answer cannot keep
    /// a sweep paging forever.
    /// </summary>
    public const int MaxPages = 100;

    /// <summary>
    /// When the current suspension ends, in UTC, or <c>null</c> when
    /// requests may be sent.
    /// </summary>
    public DateTimeOffset? SuspendedUntil => _rateLimiter.SuspendedUntil;

    /// <summary>
    /// Looks up the AnimeSchedule.net anime linked to any of the given AniDB
    /// anime IDs via <c>GET /anime?anidb-ids=…&amp;mt=any</c>, paging when
    /// the answer runs past one page. An ID missing from the answer is one
    /// AnimeSchedule.net does not know.
    /// </summary>
    /// <param name="anidbAnimeIds">The AniDB anime IDs.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>
    /// The anime it knows, empty when it knows none, or <c>null</c> when the
    /// request failed.
    /// </returns>
    /// <exception cref="AnimeScheduleUnavailableException">AnimeSchedule.net is suspended.</exception>
    public async Task<IReadOnlyList<AnimeScheduleAnimeInfo>?> FindAnimeByAnidbIdsAsync(
        IReadOnlyCollection<int> anidbAnimeIds,
        CancellationToken cancellationToken = default
    )
    {
        if (anidbAnimeIds.Count == 0)
            return [];

        var query = string.Join("&", anidbAnimeIds.Distinct().Select(id => string.Create(CultureInfo.InvariantCulture, $"anidb-ids={id}")));
        var anime = new List<AnimeScheduleAnimeInfo>();
        var completed = await GetAllPagesAsync($"anime?{query}&mt=any", anime.AddRange, cancellationToken).ConfigureAwait(false);
        return completed ? anime : null;
    }

    /// <summary>
    /// Lists every AnimeSchedule.net anime of one airing status via
    /// <c>GET /anime?airing-statuses=</c>, handing over each page as it is
    /// read.
    /// </summary>
    /// <param name="airingStatus">The status, such as <c>ongoing</c> or <c>upcoming</c>.</param>
    /// <param name="onPage">Takes the anime of each page.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns><c>true</c> when the list was read to its end; <c>false</c> when a request failed.</returns>
    /// <exception cref="AnimeScheduleUnavailableException">AnimeSchedule.net is suspended.</exception>
    public Task<bool> ListAnimeAsync(
        string airingStatus,
        Action<IReadOnlyList<AnimeScheduleAnimeInfo>> onPage,
        CancellationToken cancellationToken = default
    )
        => GetAllPagesAsync($"anime?airing-statuses={Uri.EscapeDataString(airingStatus)}", onPage, cancellationToken);

    /// <summary>
    /// Reads every page of an <c>/anime</c> search. A 404 is an answer with
    /// no anime, not a failure.
    /// </summary>
    /// <param name="requestUri">The search, without a page.</param>
    /// <param name="onPage">Takes the anime of each page.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns><c>true</c> when the search was read to its end; <c>false</c> when a request failed.</returns>
    /// <exception cref="AnimeScheduleUnavailableException">AnimeSchedule.net is suspended.</exception>
    private async Task<bool> GetAllPagesAsync(
        string requestUri,
        Action<IReadOnlyList<AnimeScheduleAnimeInfo>> onPage,
        CancellationToken cancellationToken
    )
    {
        var read = 0;
        for (var page = 1; page <= MaxPages; page++)
        {
            var pageUri = page == 1 ? requestUri : string.Create(CultureInfo.InvariantCulture, $"{requestUri}&page={page}");
            var result = await GetAsync<AnimeScheduleAnimePage>(pageUri, logNotFound: false, cancellationToken).ConfigureAwait(false);
            if (result.IsNotFound)
                return true;
            if (result.Value is not { } value)
                return false;

            onPage(value.Anime);
            read += value.Anime.Count;
            if (value.Anime.Count == 0 || read >= value.TotalAmount)
                return true;
        }

        return true;
    }

    /// <summary>
    /// Fetches one week's timetable for the given air type via
    /// <c>GET /timetables/{raw|sub|dub}?year=&amp;week=</c>, in UTC.
    /// </summary>
    /// <param name="airType">The air type.</param>
    /// <param name="year">The ISO 8601 year.</param>
    /// <param name="week">The ISO 8601 week.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The week's entries, empty for a week with no timetable, or
    /// <c>null</c> when the request failed.
    /// </returns>
    /// <exception cref="AnimeScheduleUnavailableException">AnimeSchedule.net is suspended.</exception>
    public async Task<IReadOnlyList<AnimeScheduleTimetableEntry>?> GetTimetableAsync(
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

        var result = await GetAsync<List<AnimeScheduleTimetableEntry>>(
            $"timetables/{segment}?year={year}&week={week}&tz=UTC",
            logNotFound: true,
            cancellationToken
        ).ConfigureAwait(false);

        return result.IsNotFound ? [] : result.Value;
    }

    /// <summary>
    /// Sends one GET, retrying once after a rate limit and suspending the
    /// service after a second one or a run of server errors.
    /// </summary>
    /// <typeparam name="T">The response shape.</typeparam>
    /// <param name="requestUri">The request, relative to the API's base.</param>
    /// <param name="logNotFound">Whether a 404 is worth a Debug line.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The answer; neither a value nor a 404 means the request failed.</returns>
    /// <exception cref="AnimeScheduleUnavailableException">AnimeSchedule.net is suspended.</exception>
    private async Task<ApiResult<T>> GetAsync<T>(string requestUri, bool logNotFound, CancellationToken cancellationToken) where T : class
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

            return default;
        }

        // A 429 is retried once after its wait; a second in a row suspends
        // the service rather than spinning.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_rateLimiter.SuspendedUntil is { } suspendedUntil)
                throw new AnimeScheduleUnavailableException(suspendedUntil);

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
                return default;
            }

            using (response)
            {
                var now = DateTimeOffset.UtcNow;
                _rateLimiter.UpdateFromHeaders(response.Headers, now);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = GetRateLimitWait(response.Headers, now);
                    if (attempt == 0 && wait <= MaxRateLimitRetryWait)
                    {
                        _logger.LogWarning("AnimeSchedule.net rate limit hit while requesting {RequestUri}; retrying once in {Wait}.", requestUri, wait);
                        await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var until = now + (wait > MinimumRateLimitSuspension ? wait : MinimumRateLimitSuspension);
                    _rateLimiter.NotifyRateLimited(until);
                    throw new AnimeScheduleUnavailableException(until);
                }

                if ((int)response.StatusCode >= 500)
                {
                    if (_rateLimiter.NotifyServerError() is { } until)
                        throw new AnimeScheduleUnavailableException(until);

                    _logger.LogWarning("AnimeSchedule.net request to {RequestUri} failed with {StatusCode}.", requestUri, response.StatusCode);
                    return default;
                }

                _rateLimiter.NotifySuccess();
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // A routine answer: an empty week, or no anime matching a
                    // search. Only the timetables say so in the log.
                    if (logNotFound)
                        _logger.LogDebug("AnimeSchedule.net has no data for {RequestUri}.", requestUri);

                    return new(null, IsNotFound: true);
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("AnimeSchedule.net request to {RequestUri} failed with {StatusCode}.", requestUri, response.StatusCode);
                    return default;
                }

                try
                {
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using (stream.ConfigureAwait(false))
                    {
                        return new(await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false), IsNotFound: false);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "AnimeSchedule.net answered {RequestUri} with a body that could not be read.", requestUri);
                    return default;
                }
            }
        }

        return default;
    }

    /// <summary>
    /// How long a 429 asks to wait: its <c>Retry-After</c>, else the time
    /// until the rate limit window resets, else one minute.
    /// </summary>
    /// <param name="headers">The response headers.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The wait, never below zero.</returns>
    internal static TimeSpan GetRateLimitWait(HttpResponseHeaders headers, DateTimeOffset now)
    {
        TimeSpan? wait = headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - now,
            _ => AnimeScheduleRateLimiter.ParseHeaders(headers, now).ResetAt is { } resetAt ? resetAt - now : null,
        };

        return wait is { } value ? value < TimeSpan.Zero ? TimeSpan.Zero : value : TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// The answer to one request.
    /// </summary>
    /// <typeparam name="T">The response shape.</typeparam>
    /// <param name="Value">The body, or <c>null</c>.</param>
    /// <param name="IsNotFound">Whether the answer was a 404.</param>
    private readonly record struct ApiResult<T>(T? Value, bool IsNotFound) where T : class;
}
