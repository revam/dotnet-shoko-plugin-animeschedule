using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Airing;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Plugin.AnimeSchedule.Api;

namespace Shoko.Plugin.AnimeSchedule;

/// <summary>
/// Airing schedule provider backed by AnimeSchedule.net. Keys series by their
/// AniDB anime ID (looked up against AnimeSchedule.net's own <c>route</c>
/// slug), and reports raw, sub and dub airings as separate tracked schedules,
/// one per streaming platform.
/// </summary>
/// <remarks>
/// <para>
/// AnimeSchedule.net's API terms require visible credit; the plugin's
/// <see cref="Plugin.Description"/> and README carry that attribution. This
/// provider keeps no database of its own: everything it knows is re-derived
/// from the API on every refresh, with only short-lived in-memory caching to
/// avoid redundant calls within a single sweep.
/// </para>
/// <para>
/// A timetable covers one week, so a write only ever knows about this week
/// and the next one. Airings go in through <c>MergeAirings</c> for that
/// reason: the entries the timetables still list are submitted, an episode
/// dropped from a week they do list is named as a removal, and everything
/// outside the fortnight is left as it stands.
/// </para>
/// </remarks>
public sealed class AnimeScheduleProvider : IAiringScheduleProvider<Configuration>, ISweepingAiringScheduleProvider
{
    private static readonly TimeSpan AnimeInfoTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan AnimeInfoNegativeTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan TimetableTtl = TimeSpan.FromMinutes(5);

    private readonly AnimeScheduleApiClient _apiClient;
    private readonly IAiringScheduleService _scheduleService;
    private readonly IMetadataService _metadataService;
    private readonly ConfigurationProvider<Configuration> _configurationProvider;
    private readonly ILogger<AnimeScheduleProvider> _logger;

    private readonly ConcurrentDictionary<int, (AnimeScheduleAnimeInfo? Info, DateTimeOffset ExpiresAt)> _animeCache = new();
    private readonly ConcurrentDictionary<(AnimeScheduleAirType AirType, int Year, int Week), (IReadOnlyList<AnimeScheduleTimetableEntry> Entries, DateTimeOffset ExpiresAt)> _timetableCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeScheduleProvider"/>
    /// class.
    /// </summary>
    public AnimeScheduleProvider(
        AnimeScheduleApiClient apiClient,
        IAiringScheduleService scheduleService,
        IMetadataService metadataService,
        ConfigurationProvider<Configuration> configurationProvider,
        ILogger<AnimeScheduleProvider> logger
    )
    {
        _apiClient = apiClient;
        _scheduleService = scheduleService;
        _metadataService = metadataService;
        _configurationProvider = configurationProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "AnimeSchedule.net";

    /// <inheritdoc/>
    public string? Description => """
        Pulls airing schedules, delays and streaming links from
        AnimeSchedule.net. Requires your own AnimeSchedule.net application
        token (see the plugin's configuration).
    """;

    /// <inheritdoc/>
    public IReadOnlySet<AiringKind> AvailableKinds { get; } = new HashSet<AiringKind>
    {
        AiringKind.Original,
        AiringKind.Subtitled,
        AiringKind.Dubbed,
    };

    #region Refreshing

    /// <inheritdoc/>
    public async Task<bool> RefreshAsync(ISeries series, CancellationToken cancellationToken = default)
    {
        var anidbAnime = ResolveAnidbAnime(series);
        if (anidbAnime is null)
            return false;

        if (string.IsNullOrWhiteSpace(_configurationProvider.Load().AppToken))
        {
            _logger.LogDebug("Skipping AnimeSchedule.net refresh for AniDB anime {AnimeID}: no app token configured.", anidbAnime.ID);
            return false;
        }

        var animeInfo = await ResolveAnimeInfoAsync(anidbAnime.ID, cancellationToken).ConfigureAwait(false);
        if (animeInfo is null)
        {
            _logger.LogDebug("AnimeSchedule.net does not know AniDB anime {AnimeID}.", anidbAnime.ID);
            return false;
        }

        var now = DateTime.UtcNow;
        var (year, week) = AnimeScheduleMapper.GetIsoWeek(now);
        var (nextYear, nextWeek) = AnimeScheduleMapper.GetIsoWeek(now.AddDays(7));
        var window = AnimeScheduleMapper.GetTimetableWindow(now);

        foreach (var airType in AnimeScheduleMapper.AllAirTypes)
        {
            var current = await GetTimetableCachedAsync(airType, year, week, cancellationToken).ConfigureAwait(false);
            var next = await GetTimetableCachedAsync(airType, nextYear, nextWeek, cancellationToken).ConfigureAwait(false);

            var matching = current
                .Concat(next)
                .Where(e => string.Equals(e.Route, animeInfo.Route, StringComparison.Ordinal))
                .OrderBy(e => e.EpisodeDate)
                .ToList();

            if (matching.Count == 0)
                continue;

            ApplyAirType(series, animeInfo, airType, matching, window);
        }

        return true;
    }

    #endregion

    #region Sweeping

    /// <summary>
    /// AnimeSchedule.net edits the coming week all day long, moving a slot as
    /// soon as a broadcaster announces it, so half an hour is a reasonable
    /// cadence for a whole walk. The value actually used is the user's own
    /// <c>AiringScheduleProviderInfo.SweepInterval</c>, which this only seeds,
    /// and the server never sweeps more often than every fifteen minutes.
    /// </summary>
    public TimeSpan? SuggestedSweepInterval => TimeSpan.FromMinutes(30);

    /// <inheritdoc/>
    public async Task<string?> SweepAsync(string? cursor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_configurationProvider.Load().AppToken))
        {
            _logger.LogDebug("Skipping the AnimeSchedule.net sweep: no app token configured.");
            return null;
        }

        var after = ParseCursor(cursor);
        var series = _metadataService.GetAllSeriesForProvider(IMetadataService.ProviderName.Shoko)
            .Where(entry => entry.ID > after)
            .OrderBy(entry => entry.ID)
            .ToList();
        if (series.Count == 0)
        {
            _logger.LogDebug("The sweep found no series after shoko series {SeriesID}, and has come full circle.", after);
            return null;
        }

        var now = DateTime.UtcNow;
        var (year, week) = AnimeScheduleMapper.GetIsoWeek(now);
        var (nextYear, nextWeek) = AnimeScheduleMapper.GetIsoWeek(now.AddDays(7));

        // Fill the shared timetable cache with one round-trip per air type
        // per week, so the per-series refreshes below make no timetable
        // requests of their own. Six requests, whatever the library's size.
        try
        {
            foreach (var airType in AnimeScheduleMapper.AllAirTypes)
            {
                await GetTimetableCachedAsync(airType, year, week, cancellationToken).ConfigureAwait(false);
                await GetTimetableCachedAsync(airType, nextYear, nextWeek, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return ResumeAfter(after, 0);
        }

        var swept = 0;
        foreach (var oneSeries in series)
        {
            if (cancellationToken.IsCancellationRequested)
                return ResumeAfter(after, swept);

            try
            {
                await RefreshAsync(oneSeries, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The budget ran out mid-request. Handing back the ground
                // already covered beats letting the chunk end as a timeout,
                // which would walk these series again from the old cursor.
                return ResumeAfter(after, swept);
            }
            catch (Exception ex)
            {
                // One series AnimeSchedule.net answers oddly for is not worth
                // stalling the walk over; the cursor moves past it either way.
                _logger.LogWarning(ex, "The AnimeSchedule.net sweep failed for series {SeriesID}.", oneSeries.ID);
            }

            after = oneSeries.ID;
            swept++;
        }

        _logger.LogDebug("The sweep covered the last {Count} series, and has come full circle.", swept);
        return null;

        string ResumeAfter(int seriesId, int count)
        {
            _logger.LogDebug(
                "The sweep covered {Count} series before running out of budget; the next chunk resumes after shoko series {SeriesID}.",
                count,
                seriesId
            );
            return FormatCursor(seriesId);
        }
    }

    /// <summary>
    /// Reads the shoko series ID the last chunk finished at out of the
    /// cursor. A cursor that cannot be read starts the sweep over rather than
    /// ending it, since an unreadable cursor says nothing about what has been
    /// covered.
    /// </summary>
    /// <param name="cursor">The cursor the chunk was called with.</param>
    /// <returns>The shoko series ID to resume after; <c>0</c> starts a fresh sweep.</returns>
    private int ParseCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
            return 0;

        if (int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var seriesId))
            return seriesId;

        _logger.LogWarning("Starting a fresh sweep: the cursor \"{Cursor}\" is not a shoko series ID.", cursor);
        return 0;
    }

    /// <summary>
    /// Writes the shoko series ID to resume after as a cursor.
    /// </summary>
    /// <param name="seriesId">The shoko series ID the chunk finished at.</param>
    /// <returns>The cursor.</returns>
    private static string FormatCursor(int seriesId)
        => seriesId.ToString(CultureInfo.InvariantCulture);

    #endregion

    #region Writing

    /// <summary>
    /// Writes one air type's entries as a schedule per streaming platform,
    /// and the airings on each.
    /// </summary>
    /// <param name="series">The series the schedules are for.</param>
    /// <param name="animeInfo">What AnimeSchedule.net knows about the anime.</param>
    /// <param name="airType">The air type the entries came from.</param>
    /// <param name="entries">This week's and next week's entries for the anime.</param>
    /// <param name="window">The stretch of time those two weeks cover, in UTC.</param>
    private void ApplyAirType(
        ISeries series,
        AnimeScheduleAnimeInfo animeInfo,
        AnimeScheduleAirType airType,
        IReadOnlyList<AnimeScheduleTimetableEntry> entries,
        (DateTime FromUtc, DateTime ToUtc) window
    )
    {
        var donghua = entries.Any(e => e.Donghua);
        var track = AnimeScheduleMapper.BuildTrack(airType, donghua);
        var isFinished = string.Equals(animeInfo.Status, "Finished", StringComparison.OrdinalIgnoreCase);
        var lastEpisodeNumber = animeInfo.Episodes is > 0 ? animeInfo.Episodes : null;
        var platforms = AnimeScheduleMapper.GetPlatforms(entries);

        foreach (var platform in platforms)
        {
            var channel = platform.DisplayName is null
                ? null
                : _scheduleService.FindOrRegisterChannel(platform.DisplayName, AiringChannelType.Streaming);

            var scheduleData = new AiringScheduleData
            {
                Series = series,
                ChannelID = channel?.ID,
                Tracks = [track],
                FirstEpisodeNumber = 1,
                LastEpisodeNumber = lastEpisodeNumber,
                IsFinished = isFinished,
                TimeZone = TimeZoneInfo.Utc,
                Key = AnimeScheduleMapper.BuildScheduleKey(airType, platform.Key),
                Url = platform.Url,
            };

            IAiringSchedule schedule;
            try
            {
                schedule = _scheduleService.AddOrUpdateSchedule(this, scheduleData);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add or update the AnimeSchedule.net schedule {Key} for series {SeriesID}.", scheduleData.Key, series.ID);
                continue;
            }

            WriteAiringsForSchedule(series, schedule, entries, window);
        }
    }

    /// <summary>
    /// Writes the airings one schedule takes from this week's and next week's
    /// entries, and links the episodes a single release covers together.
    /// </summary>
    /// <param name="series">The series the schedule is for.</param>
    /// <param name="schedule">The schedule to write to.</param>
    /// <param name="entries">The entries to write.</param>
    /// <param name="window">The stretch of time the entries cover, in UTC.</param>
    private void WriteAiringsForSchedule(
        ISeries series,
        IAiringSchedule schedule,
        IReadOnlyList<AnimeScheduleTimetableEntry> entries,
        (DateTime FromUtc, DateTime ToUtc) window
    )
    {
        var airingData = new List<EpisodeAiringData>();
        var linkGroups = new List<IReadOnlyList<int>>();
        var episodeByNumber = new Dictionary<int, IEpisode>();

        foreach (var (entry, numbers) in AnimeScheduleMapper.ResolveEpisodeOwnership(entries))
        {
            if (numbers.Count > 1)
                linkGroups.Add(numbers);

            foreach (var number in numbers)
            {
                if (!episodeByNumber.TryGetValue(number, out var episode))
                {
                    episode = FindEpisode(series, number);
                    if (episode is null)
                        continue;

                    episodeByNumber[number] = episode;
                }

                airingData.Add(AnimeScheduleMapper.MapAiring(entry, episode));
            }
        }

        if (airingData.Count == 0)
            return;

        // A timetable is a fortnight of one run, never the whole of it, so
        // the write is a delta: an episode this fortnight no longer lists is
        // named as a removal, and the weeks either side of it are untouched.
        IReadOnlyList<IEpisodeAiring> result;
        try
        {
            var withdrawn = FindWithdrawnAirings(schedule, airingData, window);
            result = _scheduleService.MergeAirings(this, schedule, airingData, withdrawn, new EpisodeAiringUpdateOptions { InferDelays = false });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write the AnimeSchedule.net airings for schedule {ScheduleID}.", schedule.ID);
            return;
        }

        foreach (var group in linkGroups)
        {
            var toLink = group
                .Select(number => episodeByNumber.GetValueOrDefault(number))
                .Where(episode => episode is not null)
                .Select(episode => result.FirstOrDefault(a =>
                    a.EpisodeSource == episode!.Source
                    && a.EpisodeID == episode.ID.ToString(CultureInfo.InvariantCulture)))
                .Where(airing => airing is not null)
                .Select(airing => airing!)
                .ToList();

            if (toLink.Count < 2)
                continue;

            try
            {
                _scheduleService.LinkAirings(this, toLink);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to link AnimeSchedule.net airings for schedule {ScheduleID}.", schedule.ID);
            }
        }
    }

    /// <summary>
    /// The airings already on the schedule, inside the fortnight the
    /// timetables cover, that those timetables no longer list. An episode
    /// AnimeSchedule.net has dropped from a week it still publishes reaches
    /// the service through this, the same way leaving it out of a whole-line
    /// write would.
    /// </summary>
    /// <param name="schedule">The schedule being written.</param>
    /// <param name="airings">The airings this write submits.</param>
    /// <param name="window">The stretch of time the timetables cover, in UTC.</param>
    /// <returns>The airings to name as removals.</returns>
    private IReadOnlyList<IEpisodeAiring> FindWithdrawnAirings(
        IAiringSchedule schedule,
        IReadOnlyList<EpisodeAiringData> airings,
        (DateTime FromUtc, DateTime ToUtc) window
    )
    {
        var submitted = new HashSet<string>(airings.Select(airing => airing.Key!), StringComparer.Ordinal);
        return _scheduleService
            .GetAiringsForSchedule(schedule.ID, new EpisodeAiringFilteringOptions { IncludeEstimates = false })
            .Where(airing => airing.AiredAt is { } airedAt && airedAt >= window.FromUtc && airedAt < window.ToUtc && !submitted.Contains(airing.Key))
            .ToList();
    }

    #endregion

    #region Caching

    private async Task<AnimeScheduleAnimeInfo?> ResolveAnimeInfoAsync(int anidbAnimeId, CancellationToken cancellationToken)
    {
        if (_animeCache.TryGetValue(anidbAnimeId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Info;

        var info = await _apiClient.FindAnimeByAnidbIdAsync(anidbAnimeId, cancellationToken).ConfigureAwait(false);
        var expiresAt = DateTimeOffset.UtcNow.Add(info is null ? AnimeInfoNegativeTtl : AnimeInfoTtl);
        _animeCache[anidbAnimeId] = (info, expiresAt);
        return info;
    }

    private async Task<IReadOnlyList<AnimeScheduleTimetableEntry>> GetTimetableCachedAsync(AnimeScheduleAirType airType, int year, int week, CancellationToken cancellationToken)
    {
        var key = (airType, year, week);
        if (_timetableCache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Entries;

        var entries = await _apiClient.GetTimetableAsync(airType, year, week, cancellationToken).ConfigureAwait(false);
        _timetableCache[key] = (entries, DateTimeOffset.UtcNow.Add(TimetableTtl));
        return entries;
    }

    private static IAnidbAnime? ResolveAnidbAnime(ISeries series) => series switch
    {
        IAnidbAnime anidbAnime => anidbAnime,
        IShokoSeries shokoSeries => shokoSeries.AnidbAnime,
        _ => null,
    };

    private static IEpisode? FindEpisode(ISeries series, int episodeNumber)
        => series.Episodes.FirstOrDefault(e => e.Type == EpisodeType.Episode && e.EpisodeNumber == episodeNumber);

    #endregion
}
