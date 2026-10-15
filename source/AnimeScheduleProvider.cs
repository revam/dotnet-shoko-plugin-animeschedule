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
using Shoko.Abstractions.Metadata.CrossReferences;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Plugin.AnimeSchedule.Api;
using Shoko.Plugin.AnimeSchedule.Storage;

namespace Shoko.Plugin.AnimeSchedule;

/// <summary>
/// Airing schedule provider backed by AnimeSchedule.net. Keys series by their
/// AniDB anime ID (matched against AnimeSchedule.net's own <c>route</c>
/// slug), and reports raw, sub and dub airings as separate tracked schedules,
/// one per streaming platform.
/// </summary>
/// <remarks>
/// <para>
/// AnimeSchedule.net's API terms require visible credit; the plugin's
/// <see cref="Plugin.Description"/> and README carry that attribution. The
/// plugin keeps a catalog of the anime AnimeSchedule.net lists as ongoing or
/// upcoming, and the lookup state of each AniDB anime, in a database of its
/// own, so a sweep asks only about the few anime the catalog does not cover.
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
    private static readonly TimeSpan TimetableTtl = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan UpcomingListInterval = TimeSpan.FromDays(1);

    private static readonly TimeSpan CatalogRetention = TimeSpan.FromDays(60);

    private readonly AnimeScheduleApiClient _apiClient;
    private readonly IAiringScheduleService _scheduleService;
    private readonly IMetadataService _metadataService;
    private readonly IMetadataCrossReferenceStore _crossReferences;
    private readonly AnimeScheduleStore _store;
    private readonly ConfigurationProvider<Configuration> _configurationProvider;
    private readonly ILogger<AnimeScheduleProvider> _logger;

    private readonly ConcurrentDictionary<(AnimeScheduleAirType AirType, int Year, int Week), (IReadOnlyList<AnimeScheduleTimetableEntry> Entries, DateTimeOffset ExpiresAt)> _timetableCache = new();

    private readonly Lock _upcomingLock = new();

    private DateTime? _upcomingListedAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeScheduleProvider"/>
    /// class.
    /// </summary>
    /// <param name="apiClient">The AnimeSchedule.net client.</param>
    /// <param name="scheduleService">The server's airing schedules.</param>
    /// <param name="metadataService">The server's metadata, for the library's series.</param>
    /// <param name="crossReferences">The links between AniDB anime and other sources.</param>
    /// <param name="store">The plugin's own database.</param>
    /// <param name="configurationProvider">The plugin's configuration.</param>
    /// <param name="logger">The logger.</param>
    public AnimeScheduleProvider(
        AnimeScheduleApiClient apiClient,
        IAiringScheduleService scheduleService,
        IMetadataService metadataService,
        IMetadataCrossReferenceStore crossReferences,
        AnimeScheduleStore store,
        ConfigurationProvider<Configuration> configurationProvider,
        ILogger<AnimeScheduleProvider> logger
    )
    {
        _apiClient = apiClient;
        _scheduleService = scheduleService;
        _metadataService = metadataService;
        _crossReferences = crossReferences;
        _store = store;
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

    /// <summary>
    /// Refreshes one series: matches it against the catalog, looks it up
    /// when the catalog does not cover it and it could be airing, whenever
    /// its next lookup is due or not, and writes its airings.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    /// <returns><c>true</c> when AnimeSchedule.net knows the series.</returns>
    public async Task<bool> RefreshAsync(ISeries series, CancellationToken cancellationToken = default)
    {
        var anidbAnime = ResolveAnidbAnime(series);
        if (anidbAnime is null || !anidbAnime.ID.TryGetNumericID<int>(out var anidbAnimeId))
            return false;

        if (string.IsNullOrWhiteSpace(_configurationProvider.Load().AppToken))
        {
            _logger.LogDebug("Skipping AnimeSchedule.net refresh for AniDB anime {AnimeID}: no app token configured.", anidbAnimeId);
            return false;
        }

        var now = DateTime.UtcNow;
        var stats = new SweepStats();
        try
        {
            var catalog = new CatalogIndex(_store.GetCatalog());
            var plan = PlanSeries(series, catalog, _store.GetLookup(anidbAnimeId), now, ignoreNextCheck: true);
            await LookUpAsync([plan], now, stats, cancellationToken).ConfigureAwait(false);
            SaveLookups([plan]);

            var timetables = await LoadTimetablesAsync(now, cancellationToken).ConfigureAwait(false);
            Write(plan, timetables, GetOwnSchedules(), AnimeScheduleMapper.GetTimetableWindow(now));
            return plan.Route is not null;
        }
        catch (AnimeScheduleUnavailableException ex)
        {
            _logger.LogDebug("Stopped the AnimeSchedule.net refresh for AniDB anime {AnimeID}: suspended until {ResumesAt:u}.", anidbAnimeId, ex.ResumesAt);
            return false;
        }
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

    /// <summary>
    /// Runs one chunk of a sweep. A pass starts by refreshing the catalog,
    /// then walks the library's series by shoko series ID, looking up at most
    /// <see cref="AnimeScheduleApiClient.PageSize"/> uncovered anime per
    /// request, and writing each series' airings from the timetables.
    /// </summary>
    /// <param name="cursor">The shoko series ID the last chunk finished at, or <c>null</c> for a new pass.</param>
    /// <param name="cancellationToken">Fires at the chunk's deadline.</param>
    /// <returns>Where the next chunk resumes, or <c>null</c> when the pass is over.</returns>
    public async Task<string?> SweepAsync(string? cursor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_configurationProvider.Load().AppToken))
        {
            _logger.LogDebug("Skipping the AnimeSchedule.net sweep: no app token configured.");
            return null;
        }

        var after = ParseCursor(cursor);
        var now = DateTime.UtcNow;
        var stats = new SweepStats();
        try
        {
            if (cursor is null)
                await RefreshCatalogAsync(now, cancellationToken).ConfigureAwait(false);

            var series = _metadataService.GetAllShokoSeries()
                .Where(entry => entry.LocalID > after)
                .OrderBy(entry => entry.LocalID)
                .ToList();
            if (series.Count == 0)
            {
                _logger.LogDebug("The sweep found no series after shoko series {SeriesID}, and has come full circle.", after);
                return null;
            }

            var timetables = await LoadTimetablesAsync(now, cancellationToken).ConfigureAwait(false);
            var catalog = new CatalogIndex(_store.GetCatalog());
            var lookups = _store.GetLookups();
            var schedules = GetOwnSchedules();
            var window = AnimeScheduleMapper.GetTimetableWindow(now);
            stats.CatalogSize = catalog.Count;

            var index = 0;
            while (index < series.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A block runs up to the series that fills one lookup request,
                // so every lookup asks for as many anime as a page holds.
                var block = new List<SeriesPlan>();
                var pending = 0;
                while (index < series.Count && pending < AnimeScheduleApiClient.PageSize)
                {
                    var plan = PlanSeries(series[index++], catalog, null, now, ignoreNextCheck: false, lookups);
                    block.Add(plan);
                    if (plan.NeedsLookup)
                        pending++;
                }

                await LookUpAsync(block, now, stats, cancellationToken).ConfigureAwait(false);
                foreach (var plan in block.Where(plan => plan.Lookup is not null))
                    lookups[plan.AnidbID] = plan.Lookup!;
                SaveLookups(block);

                foreach (var plan in block)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var seriesId = ((IShokoSeries)plan.Series).LocalID;
                    if (plan.Route is not null)
                        stats.Matched++;

                    try
                    {
                        if (Write(plan, timetables, schedules, window))
                            stats.Written++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One series the server refuses is not worth stalling
                        // the walk over; the cursor moves past it either way.
                        _logger.LogWarning(ex, "The AnimeSchedule.net sweep failed for shoko series {SeriesID}.", seriesId);
                    }

                    after = seriesId;
                }
            }

            LogSummary(stats, "has come full circle");
            return null;
        }
        catch (OperationCanceledException)
        {
            // Hand back the ground covered, rather than ending as a timeout
            // that would walk these series again from the old cursor.
            LogSummary(stats, $"ran out of budget; the next chunk resumes after shoko series {after}");
            return FormatCursor(after);
        }
        catch (AnimeScheduleUnavailableException ex)
        {
            LogSummary(stats, $"stopped: AnimeSchedule.net is suspended until {ex.ResumesAt:u}");
            return null;
        }
    }

    /// <summary>
    /// Refreshes the catalog: the ongoing list every pass, and the upcoming
    /// list once a day. Each page goes in as it is read, so a pass cut short
    /// keeps what it got, and the anime not seen for a while are dropped.
    /// </summary>
    /// <param name="now">The current time, in UTC.</param>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    /// <returns>A task that completes once the catalog is refreshed.</returns>
    /// <exception cref="AnimeScheduleUnavailableException">AnimeSchedule.net is suspended.</exception>
    private async Task RefreshCatalogAsync(DateTime now, CancellationToken cancellationToken)
    {
        var listed = 0;
        void Upsert(IReadOnlyList<AnimeScheduleAnimeInfo> anime)
        {
            _store.UpsertCatalog(anime.Select(entry => AnimeScheduleMapper.ToCatalogEntry(entry, now)));
            listed += anime.Count;
        }

        if (await _apiClient.ListAnimeAsync("ongoing", Upsert, cancellationToken).ConfigureAwait(false))
            _store.PruneCatalog(now - CatalogRetention);

        bool upcomingDue;
        lock (_upcomingLock)
            upcomingDue = _upcomingListedAt is not { } listedAt || now - listedAt >= UpcomingListInterval;

        if (upcomingDue && await _apiClient.ListAnimeAsync("upcoming", Upsert, cancellationToken).ConfigureAwait(false))
        {
            lock (_upcomingLock)
                _upcomingListedAt = now;
        }

        _logger.LogDebug("Refreshed the AnimeSchedule.net catalog with {Count} listed anime.", listed);
    }

    /// <summary>
    /// Writes a summary of the chunk.
    /// </summary>
    /// <param name="stats">What the chunk did.</param>
    /// <param name="ending">How the chunk ended.</param>
    private void LogSummary(SweepStats stats, string ending)
        => _logger.LogDebug(
            "The AnimeSchedule.net sweep chunk {Ending}: {CatalogSize} anime in the catalog, {Matched} series matched, " +
            "{Lookups} lookups for {LookedUp} anime, {Misses} misses, {GivenUp} given up, {Written} series written.",
            ending,
            stats.CatalogSize,
            stats.Matched,
            stats.Lookups,
            stats.LookedUp,
            stats.Misses,
            stats.GivenUp,
            stats.Written
        );

    /// <summary>
    /// Reads the shoko series ID the last chunk finished at out of the
    /// cursor. A cursor that cannot be read starts the walk over rather than
    /// ending it, since an unreadable cursor says nothing about what has been
    /// covered.
    /// </summary>
    /// <param name="cursor">The cursor the chunk was called with.</param>
    /// <returns>The shoko series ID to resume after; <c>0</c> starts the walk.</returns>
    private int ParseCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
            return 0;

        if (int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var seriesId))
            return seriesId;

        _logger.LogWarning("Starting the walk over: the cursor \"{Cursor}\" is not a shoko series ID.", cursor);
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

    #region Matching

    /// <summary>
    /// Works out how one series is matched: by the catalog, by an earlier
    /// lookup, or by a lookup still to make. Makes no requests.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="lookup">The series' lookup state, when <paramref name="lookups"/> is not given.</param>
    /// <param name="now">The current time, in UTC.</param>
    /// <param name="ignoreNextCheck">Whether to look the anime up even when its next lookup is not due.</param>
    /// <param name="lookups">Every lookup state, by AniDB anime ID, or <c>null</c>.</param>
    /// <returns>The plan.</returns>
    private SeriesPlan PlanSeries(
        ISeries series,
        CatalogIndex catalog,
        AnidbLookup? lookup,
        DateTime now,
        bool ignoreNextCheck,
        IReadOnlyDictionary<int, AnidbLookup>? lookups = null
    )
    {
        var plan = new SeriesPlan(series);
        var anidbAnime = ResolveAnidbAnime(series);
        if (anidbAnime is null || !anidbAnime.ID.TryGetNumericID<int>(out var anidbAnimeId))
            return plan;

        plan.AnidbAnime = anidbAnime;
        plan.AnidbID = anidbAnimeId;
        plan.Fingerprint = LookupPolicy.GetFingerprint(anidbAnime.AirDate, anidbAnime.EndDate, anidbAnime.EpisodeCounts.Episodes);
        var window = LookupPolicy.GetWindow(anidbAnime.AirDate, anidbAnime.EndDate, DateOnly.FromDateTime(now));

        // The MyAnimeList fallback reads the linked AniList entries, so it is
        // left to the anime that could be airing.
        plan.Entry = catalog.Match(
            anidbAnimeId,
            () => GetAnilistIDs(anidbAnimeId),
            window is AiringWindow.Open ? () => GetAnilistMalIDs(anidbAnimeId) : null
        );
        if (plan.Entry is not null)
        {
            plan.Route = plan.Entry.Route;
            return plan;
        }

        lookup = lookups is null ? lookup : lookups.GetValueOrDefault(anidbAnimeId);
        plan.Lookup = lookup;
        if (lookup is not null && LookupPolicy.ResetIfChanged(lookup, plan.Fingerprint, now))
            plan.LookupChanged = true;

        if (lookup is { State: LookupState.Known, Route: { } knownRoute })
        {
            plan.Route = knownRoute;
            plan.Entry = catalog.GetByRoute(knownRoute);
        }

        if (window is not AiringWindow.Open)
        {
            if (window is AiringWindow.Ended && lookup is { State: LookupState.Unknown })
            {
                LookupPolicy.GiveUp(lookup);
                plan.LookupChanged = true;
            }

            return plan;
        }

        if (lookup is { State: LookupState.GivenUp })
            return plan;

        plan.NeedsLookup = ignoreNextCheck || LookupPolicy.IsDue(lookup, now);
        return plan;
    }

    /// <summary>
    /// Looks up the anime of the plans that need it, in one request, and
    /// records each answer. A failed request records nothing, so no anime
    /// takes a miss for it.
    /// </summary>
    /// <param name="plans">The plans; those that need no lookup are passed over.</param>
    /// <param name="now">The current time, in UTC.</param>
    /// <param name="stats">Counts what the lookup did.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>A task that completes once the answers are recorded.</returns>
    /// <exception cref="AnimeScheduleUnavailableException">AnimeSchedule.net is suspended.</exception>
    private async Task LookUpAsync(IReadOnlyList<SeriesPlan> plans, DateTime now, SweepStats stats, CancellationToken cancellationToken)
    {
        var pending = plans.Where(plan => plan.NeedsLookup).ToList();
        if (pending.Count == 0)
            return;

        stats.Lookups++;
        stats.LookedUp += pending.Count;
        var found = await _apiClient.FindAnimeByAnidbIdsAsync([.. pending.Select(plan => plan.AnidbID)], cancellationToken).ConfigureAwait(false);
        if (found is null)
            return;

        var entries = found.Select(anime => AnimeScheduleMapper.ToCatalogEntry(anime, now)).ToList();
        _store.UpsertCatalog(entries);
        var byAnidb = new Dictionary<int, CatalogEntry>();
        foreach (var entry in entries)
        {
            if (entry.AnidbID is { } anidbID)
                byAnidb.TryAdd(anidbID, entry);
        }

        // An answer to a single ID is that anime's, whatever its links say.
        if (pending.Count == 1 && entries.Count == 1)
            byAnidb.TryAdd(pending[0].AnidbID, entries[0]);

        foreach (var plan in pending)
        {
            var lookup = plan.Lookup ??= new AnidbLookup { AnidbAnimeID = plan.AnidbID };
            plan.LookupChanged = true;
            if (byAnidb.TryGetValue(plan.AnidbID, out var entry))
            {
                LookupPolicy.RecordHit(lookup, entry.Route, plan.Fingerprint, now);
                plan.Route = entry.Route;
                plan.Entry = entry;
                continue;
            }

            LookupPolicy.RecordMiss(lookup, plan.Fingerprint, now);
            plan.Route = null;
            plan.Entry = null;
            stats.Misses++;
            if (lookup.State is LookupState.GivenUp)
                stats.GivenUp++;
            _logger.LogTrace("AnimeSchedule.net does not know AniDB anime {AnimeID}.", plan.AnidbID);
        }
    }

    /// <summary>
    /// Stores the lookup states the plans changed.
    /// </summary>
    /// <param name="plans">The plans.</param>
    private void SaveLookups(IEnumerable<SeriesPlan> plans)
        => _store.SaveLookups(plans.Where(plan => plan.LookupChanged && plan.Lookup is not null).Select(plan => plan.Lookup!));

    /// <summary>
    /// The AniList anime the AniDB anime is linked to, best link first.
    /// </summary>
    /// <param name="anidbAnimeId">The AniDB anime ID.</param>
    /// <returns>The AniList anime IDs, or an empty list.</returns>
    private IReadOnlyList<int> GetAnilistIDs(int anidbAnimeId)
        => [.. GetAnilistLinks(anidbAnimeId).Select(link => link.ProviderID!.TryGetNumericID<int>(out var id) ? id : 0).Where(id => id > 0)];

    /// <summary>
    /// The MyAnimeList IDs the linked AniList entries give themselves. These
    /// name one entry each, unlike AniDB's own MyAnimeList links.
    /// </summary>
    /// <param name="anidbAnimeId">The AniDB anime ID.</param>
    /// <returns>The MyAnimeList IDs, or an empty list.</returns>
    private IReadOnlyList<int> GetAnilistMalIDs(int anidbAnimeId)
    {
        var links = GetAnilistLinks(anidbAnimeId);
        if (links.Count == 0 || !MetadataSource.TryParse("mal", out var malSource))
            return [];

        var ids = new List<int>();
        foreach (var link in links)
        {
            try
            {
                if (link.Provider is not ISeries anilistSeries)
                    continue;

                foreach (var malID in anilistSeries.GetCrossSourceIDs(malSource))
                {
                    if (malID.TryGetNumericID<int>(out var id) && id > 0)
                        ids.Add(id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Could not read the AniList entry linked to AniDB anime {AnimeID}.", anidbAnimeId);
            }
        }

        return ids;
    }

    /// <summary>
    /// The AniDB anime's links to AniList series, best first. Empty when no
    /// plugin registered the AniList source.
    /// </summary>
    /// <param name="anidbAnimeId">The AniDB anime ID.</param>
    /// <returns>The links.</returns>
    private IReadOnlyList<IMetadataSeriesCrossReference> GetAnilistLinks(int anidbAnimeId)
    {
        // Only a registered source: parsing the value before the AniList
        // plugin registers it would claim it as an unregistered one.
        if (!MetadataSource.TryGet("anilist", out var anilistSource))
            return [];

        return [.. _crossReferences.GetSeriesLinks(anidbAnimeId, anilistSource).Where(link => link.ProviderID is not null).OrderBy(link => link.Ordering)];
    }

    #endregion

    #region Writing

    /// <summary>
    /// Writes one series: its airings from the timetables when it is matched
    /// and listed, and the withdrawals on every other schedule of this
    /// provider it holds. An air type whose timetables failed is left alone.
    /// </summary>
    /// <param name="plan">The series' plan.</param>
    /// <param name="timetables">This week's and next week's entries per air type, <c>null</c> where they failed.</param>
    /// <param name="schedules">This provider's schedules, by series.</param>
    /// <param name="window">The stretch of time the timetables cover, in UTC.</param>
    /// <returns><c>true</c> when anything was written.</returns>
    private bool Write(
        SeriesPlan plan,
        IReadOnlyDictionary<AnimeScheduleAirType, IReadOnlyList<AnimeScheduleTimetableEntry>?> timetables,
        IReadOnlyDictionary<MetadataGuid, IReadOnlyList<IAiringSchedule>> schedules,
        (DateTime FromUtc, DateTime ToUtc) window
    )
    {
        var touched = new HashSet<Guid>();
        if (plan.Route is { } route)
        {
            var animeInfo = plan.Entry is { } entry ? AnimeScheduleMapper.ToAnimeInfo(entry) : new AnimeScheduleAnimeInfo { Route = route };
            foreach (var (airType, entries) in timetables)
            {
                if (entries is null)
                    continue;

                var matching = entries
                    .Where(e => string.Equals(e.Route, route, StringComparison.Ordinal))
                    .OrderBy(e => e.EpisodeDate)
                    .ToList();
                if (matching.Count > 0)
                    ApplyAirType(plan.Series, animeInfo, airType, matching, window, touched);
            }
        }

        var held = schedules.GetValueOrDefault(plan.Series.ID) ?? [];
        if (plan.AnidbAnime is { } anidbAnime && schedules.TryGetValue(anidbAnime.ID, out var heldByAnidb))
            held = [.. held, .. heldByAnidb];

        var withdrew = false;
        foreach (var schedule in held.DistinctBy(schedule => schedule.ID))
        {
            if (touched.Contains(schedule.ID))
                continue;

            if (AnimeScheduleMapper.GetAirTypeFromScheduleKey(schedule.Key) is not { } airType || timetables.GetValueOrDefault(airType) is null)
                continue;

            withdrew |= WithdrawUnlisted(schedule, window);
        }

        return touched.Count > 0 || withdrew;
    }

    /// <summary>
    /// Writes one air type's entries as a schedule per streaming platform,
    /// and the airings on each.
    /// </summary>
    /// <param name="series">The series the schedules are for.</param>
    /// <param name="animeInfo">What AnimeSchedule.net knows about the anime.</param>
    /// <param name="airType">The air type the entries came from.</param>
    /// <param name="entries">This week's and next week's entries for the anime.</param>
    /// <param name="window">The stretch of time those two weeks cover, in UTC.</param>
    /// <param name="touched">Takes the ID of every schedule written.</param>
    private void ApplyAirType(
        ISeries series,
        AnimeScheduleAnimeInfo animeInfo,
        AnimeScheduleAirType airType,
        IReadOnlyList<AnimeScheduleTimetableEntry> entries,
        (DateTime FromUtc, DateTime ToUtc) window,
        ISet<Guid> touched
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
                : _scheduleService.FindOrRegisterChannel(platform.DisplayName, AiringChannelType.Streaming, platform.CountryCode);

            var scheduleData = new AiringScheduleData
            {
                Series = series,
                ChannelID = channel?.ChannelID,
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

            touched.Add(schedule.ID);
            WriteAiringsForSchedule(schedule, entries, window);
        }
    }

    /// <summary>
    /// Writes the airings one schedule takes from this week's and next week's
    /// entries, and links the episodes a single release covers together.
    /// </summary>
    /// <param name="schedule">The schedule to write to.</param>
    /// <param name="entries">The entries to write.</param>
    /// <param name="window">The stretch of time the entries cover, in UTC.</param>
    private void WriteAiringsForSchedule(
        IAiringSchedule schedule,
        IReadOnlyList<AnimeScheduleTimetableEntry> entries,
        (DateTime FromUtc, DateTime ToUtc) window
    )
    {
        var airingData = new List<EpisodeAiringData>();
        var linkGroups = new List<IReadOnlyList<int>>();

        foreach (var (entry, numbers) in AnimeScheduleMapper.ResolveEpisodeOwnership(entries))
        {
            // The schedule's line runs from episode one to the anime's
            // episode total, when known; a number outside it is refused.
            var onLine = numbers
                .Where(number => number >= 1 && (schedule.LastEpisodeNumber is not { } last || number <= last))
                .ToList();
            if (onLine.Count > 1)
                linkGroups.Add(onLine);

            foreach (var number in onLine)
                airingData.Add(AnimeScheduleMapper.MapAiring(entry, number));
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
                .Select(number => result.FirstOrDefault(airing => airing.SequenceNumber == number))
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
    /// Names as removals the airings of a schedule, inside the fortnight the
    /// timetables cover, now that the timetables no longer list its anime on
    /// that air type and platform at all.
    /// </summary>
    /// <param name="schedule">The schedule.</param>
    /// <param name="window">The stretch of time the timetables cover, in UTC.</param>
    /// <returns><c>true</c> when anything was withdrawn.</returns>
    private bool WithdrawUnlisted(IAiringSchedule schedule, (DateTime FromUtc, DateTime ToUtc) window)
    {
        var withdrawn = FindWithdrawnAirings(schedule, [], window);
        if (withdrawn.Count == 0)
            return false;

        try
        {
            _scheduleService.MergeAirings(this, schedule, [], withdrawn, new EpisodeAiringUpdateOptions { InferDelays = false });
            return true;
        }
        catch (AiringScheduleValidationException ex)
        {
            // Such as a write that would empty the schedule's retention window.
            _logger.LogDebug(ex, "Could not withdraw the unlisted AnimeSchedule.net airings of schedule {ScheduleID}.", schedule.ID);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to withdraw the unlisted AnimeSchedule.net airings of schedule {ScheduleID}.", schedule.ID);
            return false;
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

    /// <summary>
    /// This provider's schedules, by the series they are for. Read from the
    /// server, with no requests to AnimeSchedule.net.
    /// </summary>
    /// <returns>The schedules, by series ID.</returns>
    private Dictionary<MetadataGuid, IReadOnlyList<IAiringSchedule>> GetOwnSchedules()
    {
        Guid providerID;
        try
        {
            providerID = _scheduleService.GetProviderInfo(this).ID;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogDebug(ex, "Could not read this provider's AnimeSchedule.net schedules.");
            return [];
        }

        return _scheduleService.GetSchedulesForProvider(providerID)
            .GroupBy(schedule => schedule.SeriesID)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<IAiringSchedule>)[.. group]);
    }

    #endregion

    #region Timetables

    /// <summary>
    /// This week's and next week's timetable entries for each air type, with
    /// one request per air type and week at most every five minutes.
    /// </summary>
    /// <param name="now">The current time, in UTC.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The entries per air type, <c>null</c> where either week failed.</returns>
    /// <exception cref="AnimeScheduleUnavailableException">AnimeSchedule.net is suspended.</exception>
    private async Task<Dictionary<AnimeScheduleAirType, IReadOnlyList<AnimeScheduleTimetableEntry>?>> LoadTimetablesAsync(
        DateTime now,
        CancellationToken cancellationToken
    )
    {
        var (year, week) = AnimeScheduleMapper.GetIsoWeek(now);
        var (nextYear, nextWeek) = AnimeScheduleMapper.GetIsoWeek(now.AddDays(7));
        var timetables = new Dictionary<AnimeScheduleAirType, IReadOnlyList<AnimeScheduleTimetableEntry>?>();
        foreach (var airType in AnimeScheduleMapper.AllAirTypes)
        {
            var current = await GetTimetableCachedAsync(airType, year, week, cancellationToken).ConfigureAwait(false);
            var next = await GetTimetableCachedAsync(airType, nextYear, nextWeek, cancellationToken).ConfigureAwait(false);
            timetables[airType] = current is null || next is null ? null : [.. current, .. next];
        }

        return timetables;
    }

    /// <summary>
    /// One week's timetable for one air type, from the cache while it is
    /// fresh. A failed request is not cached.
    /// </summary>
    /// <param name="airType">The air type.</param>
    /// <param name="year">The ISO 8601 year.</param>
    /// <param name="week">The ISO 8601 week.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The entries, or <c>null</c> when the request failed.</returns>
    /// <exception cref="AnimeScheduleUnavailableException">AnimeSchedule.net is suspended.</exception>
    private async Task<IReadOnlyList<AnimeScheduleTimetableEntry>?> GetTimetableCachedAsync(
        AnimeScheduleAirType airType,
        int year,
        int week,
        CancellationToken cancellationToken
    )
    {
        var key = (airType, year, week);
        if (_timetableCache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Entries;

        var entries = await _apiClient.GetTimetableAsync(airType, year, week, cancellationToken).ConfigureAwait(false);
        if (entries is not null)
            _timetableCache[key] = (entries, DateTimeOffset.UtcNow.Add(TimetableTtl));
        return entries;
    }

    /// <summary>
    /// The AniDB anime behind a series.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <returns>The AniDB anime, or <c>null</c>.</returns>
    private static IAnidbAnime? ResolveAnidbAnime(ISeries series) => series switch
    {
        IAnidbAnime anidbAnime => anidbAnime,
        IShokoSeries shokoSeries => shokoSeries.AnidbAnime,
        _ => null,
    };

    #endregion

    #region Types

    /// <summary>
    /// How one series is matched, and what is still to learn about it.
    /// </summary>
    /// <param name="series">The series.</param>
    private sealed class SeriesPlan(ISeries series)
    {
        /// <summary>
        /// The series.
        /// </summary>
        public ISeries Series { get; } = series;

        /// <summary>
        /// The AniDB anime behind the series, or <c>null</c>.
        /// </summary>
        public IAnidbAnime? AnidbAnime { get; set; }

        /// <summary>
        /// The AniDB anime ID, or <c>0</c> without one.
        /// </summary>
        public int AnidbID { get; set; }

        /// <summary>
        /// The fingerprint of the anime's AniDB data.
        /// </summary>
        public string Fingerprint { get; set; } = "";

        /// <summary>
        /// The anime's catalog entry, when it has one.
        /// </summary>
        public CatalogEntry? Entry { get; set; }

        /// <summary>
        /// The anime's route on AnimeSchedule.net, when it is known.
        /// </summary>
        public string? Route { get; set; }

        /// <summary>
        /// The anime's lookup state, or <c>null</c> when it has none.
        /// </summary>
        public AnidbLookup? Lookup { get; set; }

        /// <summary>
        /// Whether <see cref="Lookup"/> changed and needs storing.
        /// </summary>
        public bool LookupChanged { get; set; }

        /// <summary>
        /// Whether the anime is to be looked up.
        /// </summary>
        public bool NeedsLookup { get; set; }
    }

    /// <summary>
    /// What one chunk of a sweep did, for its summary.
    /// </summary>
    private sealed class SweepStats
    {
        /// <summary>
        /// How many anime the catalog holds.
        /// </summary>
        public int CatalogSize { get; set; }

        /// <summary>
        /// How many series were matched to a route.
        /// </summary>
        public int Matched { get; set; }

        /// <summary>
        /// How many lookup requests were made.
        /// </summary>
        public int Lookups { get; set; }

        /// <summary>
        /// How many anime were looked up.
        /// </summary>
        public int LookedUp { get; set; }

        /// <summary>
        /// How many looked-up anime AnimeSchedule.net did not know.
        /// </summary>
        public int Misses { get; set; }

        /// <summary>
        /// How many anime were given up after a miss.
        /// </summary>
        public int GivenUp { get; set; }

        /// <summary>
        /// How many series had anything written.
        /// </summary>
        public int Written { get; set; }
    }

    #endregion
}
