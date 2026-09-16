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
/// AnimeSchedule.net's API terms require visible credit; the plugin's
/// <see cref="Plugin.Description"/> and README carry that attribution. This
/// provider keeps no database of its own: everything it knows is re-derived
/// from the API on every refresh, with only short-lived in-memory caching to
/// avoid redundant calls within a single sweep.
/// </remarks>
public sealed class AnimeScheduleProvider : IAiringScheduleProvider<Configuration>
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

        var (year, week) = AnimeScheduleMapper.GetIsoWeek(DateTime.UtcNow);
        var (nextYear, nextWeek) = AnimeScheduleMapper.GetIsoWeek(DateTime.UtcNow.AddDays(7));

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

            ApplyAirType(series, animeInfo, airType, matching);
        }

        return true;
    }

    /// <summary>
    /// Runs one sweep for every Shoko-known series: fetches this and next
    /// week's raw, sub and dub timetables once each (six requests, regardless
    /// of library size), then applies them to every series whose AniDB anime
    /// can be keyed to AnimeSchedule.net. Called by
    /// <see cref="Jobs.AnimeScheduleSweepJob"/>.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_configurationProvider.Load().AppToken))
        {
            _logger.LogDebug("Skipping AnimeSchedule.net sweep: no app token configured.");
            return;
        }

        var series = _metadataService.GetAllSeriesForProvider(IMetadataService.ProviderName.Shoko).ToList();
        if (series.Count == 0)
            return;

        var (year, week) = AnimeScheduleMapper.GetIsoWeek(DateTime.UtcNow);
        var (nextYear, nextWeek) = AnimeScheduleMapper.GetIsoWeek(DateTime.UtcNow.AddDays(7));

        // Pre-warm the shared timetable cache with one round-trip per air
        // type per week, so the per-series RefreshAsync calls below make no
        // further timetable requests of their own.
        foreach (var airType in AnimeScheduleMapper.AllAirTypes)
        {
            await GetTimetableCachedAsync(airType, year, week, cancellationToken).ConfigureAwait(false);
            await GetTimetableCachedAsync(airType, nextYear, nextWeek, cancellationToken).ConfigureAwait(false);
        }

        foreach (var oneSeries in series)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RefreshAsync(oneSeries, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeSchedule.net sweep failed for series {SeriesID}.", oneSeries.ID);
            }
        }
    }

    private void ApplyAirType(ISeries series, AnimeScheduleAnimeInfo animeInfo, AnimeScheduleAirType airType, IReadOnlyList<AnimeScheduleTimetableEntry> entries)
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

            SetAiringsForSchedule(series, schedule, entries);
        }
    }

    private void SetAiringsForSchedule(ISeries series, IAiringSchedule schedule, IReadOnlyList<AnimeScheduleTimetableEntry> entries)
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

        IReadOnlyList<IEpisodeAiring> result;
        try
        {
            result = _scheduleService.SetAirings(this, schedule, airingData, new EpisodeAiringUpdateOptions { InferDelays = false });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set AnimeSchedule.net airings for schedule {ScheduleID}.", schedule.ID);
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
}
