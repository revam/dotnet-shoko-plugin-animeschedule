using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Plugin.AnimeSchedule.Api;
using Shoko.Plugin.AnimeSchedule.Storage;
using Xunit;

namespace Shoko.Plugin.AnimeSchedule.Tests;

/// <summary>
/// Tests for the core-driven sweep: the server hands the provider a cursor
/// and a deadline, and expects a chunk that stops when the deadline fires and
/// says where the next one picks up. In between, the provider refreshes its
/// catalog, looks up only what the catalog does not cover, and writes from
/// the timetables.
/// </summary>
public class AnimeScheduleSweepTests
{
    #region Catalog

    [Fact]
    public async Task A_new_pass_lists_the_catalog_and_matches_by_anidb_id_without_a_lookup()
    {
        using var host = new SweepHost(Series(count: 2));
        host.Api.Lists["ongoing"].Add(FakeAnimeScheduleApi.Anime("one", anidbId: 1));
        host.Api.Lists["upcoming"].Add(FakeAnimeScheduleApi.Anime("two", anidbId: 2));

        var cursor = await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.Null(cursor);
        Assert.Contains(host.Api.Requests, uri => uri.Contains("airing-statuses=ongoing", StringComparison.Ordinal));
        Assert.Contains(host.Api.Requests, uri => uri.Contains("airing-statuses=upcoming", StringComparison.Ordinal));
        Assert.Empty(host.Api.Lookups);
    }

    [Fact]
    public async Task The_upcoming_list_is_read_at_most_once_a_day()
    {
        using var host = new SweepHost(Series(count: 1));

        await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);
        await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(2, host.Api.Requests.Count(uri => uri.Contains("airing-statuses=ongoing", StringComparison.Ordinal)));
        Assert.Single(host.Api.Requests, uri => uri.Contains("airing-statuses=upcoming", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_series_matches_the_catalog_by_its_anilist_link_then_by_that_entrys_mal_id()
    {
        var links = new Dictionary<int, (int AnilistID, int? MalID)>
        {
            [1] = (101, null),
            [2] = (202, 302),
        };
        using var host = new SweepHost(Series(count: 2), links: links);
        host.Api.Lists["ongoing"].Add(FakeAnimeScheduleApi.Anime("by-anilist", anilistId: 101));
        host.Api.Lists["ongoing"].Add(FakeAnimeScheduleApi.Anime("by-mal", malId: 302));

        await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.Empty(host.Api.Lookups);
    }

    [Fact]
    public async Task AniDBs_own_mal_links_never_match_the_catalog()
    {
        // AniDB folds many MyAnimeList entries into one anime, so its links
        // cannot say which catalog entry is this one.
        using var host = new SweepHost([Host.ShokoSeries(1, malIds: [21, 22])]);
        host.Api.Lists["ongoing"].Add(FakeAnimeScheduleApi.Anime("some-other-anime", malId: 21));

        await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.Contains("anidb-ids=1", Assert.Single(host.Api.Lookups), StringComparison.Ordinal);
    }

    #endregion

    #region Lookups

    [Fact]
    public async Task Uncovered_anime_are_looked_up_a_page_at_a_time()
    {
        var count = AnimeScheduleApiClient.PageSize + 2;
        using var host = new SweepHost(Series(count));

        await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(2, host.Api.Lookups.Count);
        Assert.Equal(AnimeScheduleApiClient.PageSize, host.Api.Lookups[0].Split("anidb-ids=").Length - 1);
        Assert.All(host.Api.Lookups, uri => Assert.Contains("mt=any", uri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_miss_waits_out_its_backoff_and_a_hit_is_matched_from_then_on()
    {
        using var host = new SweepHost(Series(count: 2));
        host.Api.Known.Add(FakeAnimeScheduleApi.Anime("known", anidbId: 2));

        await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);
        await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.Single(host.Api.Lookups);
        var lookups = host.Store.GetLookups();
        Assert.Equal(LookupState.Unknown, lookups[1].State);
        Assert.Equal(1, lookups[1].ConsecutiveMisses);
        Assert.Equal(LookupState.Known, lookups[2].State);
        Assert.Equal("known", lookups[2].Route);
    }

    [Fact]
    public async Task A_failed_lookup_is_not_counted_as_a_miss()
    {
        using var host = new SweepHost(Series(count: 1));
        host.Api.FailWith = HttpStatusCode.BadRequest;

        await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.Empty(host.Store.GetLookups());
    }

    [Theory]
    [InlineData(-400, -300)]
    [InlineData(400, null)]
    public async Task An_anime_that_cannot_be_airing_is_never_looked_up(int startInDays, int? endInDays)
    {
        var today = DateTime.UtcNow.Date;
        var series = Host.ShokoSeries(
            1,
            airDate: new PartialDateOnly(today.AddDays(startInDays)),
            endDate: endInDays is { } end ? new PartialDateOnly(today.AddDays(end)) : null
        );
        using var host = new SweepHost([series]);

        await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.Empty(host.Api.Lookups);
    }

    [Fact]
    public async Task An_unknown_anime_that_has_ended_is_given_up()
    {
        var today = DateTime.UtcNow.Date;
        var airDate = new PartialDateOnly(today.AddDays(-400));
        var endDate = new PartialDateOnly(today.AddDays(-300));
        using var host = new SweepHost([Host.ShokoSeries(1, airDate, endDate)]);
        host.Store.SaveLookups([Lookup(1, LookupState.Unknown, LookupPolicy.GetFingerprint(airDate, endDate, 12), DateTime.UtcNow.AddDays(-1))]);

        await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.Empty(host.Api.Lookups);
        Assert.Equal(LookupState.GivenUp, host.Store.GetLookup(1)!.State);
    }

    [Fact]
    public async Task A_given_up_anime_is_looked_up_again_only_once_its_anidb_data_changes()
    {
        using var host = new SweepHost(Series(count: 2));
        host.Store.SaveLookups(
        [
            Lookup(1, LookupState.GivenUp, LookupPolicy.GetFingerprint(null, null, 12), null),
            Lookup(2, LookupState.GivenUp, "stale", null),
        ]);

        await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        var lookup = Assert.Single(host.Api.Lookups);
        Assert.DoesNotContain("anidb-ids=1", lookup, StringComparison.Ordinal);
        Assert.Contains("anidb-ids=2", lookup, StringComparison.Ordinal);
        Assert.Equal(LookupState.GivenUp, host.Store.GetLookup(2)!.State);
    }

    [Fact]
    public async Task A_refresh_of_one_series_looks_it_up_before_its_next_check_is_due()
    {
        using var host = new SweepHost(Series(count: 1));
        host.Store.SaveLookups([Lookup(1, LookupState.Unknown, LookupPolicy.GetFingerprint(null, null, 12), DateTime.UtcNow.AddDays(5))]);
        host.Api.Known.Add(FakeAnimeScheduleApi.Anime("found", anidbId: 1));

        var known = await host.Provider.RefreshAsync(Host.ShokoSeries(1), TestContext.Current.CancellationToken);

        Assert.True(known);
        Assert.Single(host.Api.Lookups);
        Assert.Equal(LookupState.Known, host.Store.GetLookup(1)!.State);
    }

    #endregion

    #region Writing

    [Fact]
    public async Task A_matched_series_listed_in_the_timetables_is_written()
    {
        using var host = new SweepHost(Series(count: 1));
        host.Api.Lists["ongoing"].Add(FakeAnimeScheduleApi.Anime("one", anidbId: 1));
        host.Api.Timetables["sub"].Add(FakeAnimeScheduleApi.Entry("one", DateTime.UtcNow.AddDays(1)));

        await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("sub:none", Assert.Single(host.Schedules.WrittenScheduleKeys));
    }

    [Fact]
    public async Task A_series_no_longer_listed_has_its_airings_in_the_fortnight_withdrawn()
    {
        using var host = new SweepHost(Series(count: 1));
        var airing = FakeScheduleService.Airing(AnimeScheduleMapper.GetTimetableWindow(DateTime.UtcNow).FromUtc.AddDays(3), "4");
        host.Schedules.Schedules[FakeScheduleService.Schedule(Host.ShokoSeriesID(1), "sub:crunchyroll")] = [airing];

        await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.Same(airing, Assert.Single(host.Schedules.Removals));
    }

    [Fact]
    public async Task Nothing_is_withdrawn_when_the_timetables_failed()
    {
        using var host = new SweepHost(Series(count: 1));
        host.Api.FailTimetablesWith = HttpStatusCode.BadRequest;
        var airing = FakeScheduleService.Airing(AnimeScheduleMapper.GetTimetableWindow(DateTime.UtcNow).FromUtc.AddDays(3), "4");
        host.Schedules.Schedules[FakeScheduleService.Schedule(Host.ShokoSeriesID(1), "sub:crunchyroll")] = [airing];

        await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.Empty(host.Schedules.Removals);
    }

    #endregion

    #region Cursors

    [Fact]
    public async Task A_sweep_resumes_after_the_series_the_cursor_names_without_listing_the_catalog()
    {
        using var host = new SweepHost(Series(count: 3));

        var cursor = await host.Provider.SweepAsync("2", TestContext.Current.CancellationToken);

        Assert.Null(cursor);
        Assert.DoesNotContain(host.Api.Requests, uri => uri.Contains("airing-statuses=", StringComparison.Ordinal));
        var lookup = Assert.Single(host.Api.Lookups);
        Assert.Contains("anidb-ids=3", lookup, StringComparison.Ordinal);
        Assert.DoesNotContain("anidb-ids=2", lookup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sweep_with_nothing_left_to_walk_ends_without_asking_for_anything()
    {
        using var host = new SweepHost(Series(count: 3));

        var cursor = await host.Provider.SweepAsync("9000", TestContext.Current.CancellationToken);

        Assert.Null(cursor);
        Assert.Empty(host.Api.Requests);
    }

    [Fact]
    public async Task An_unreadable_cursor_starts_the_walk_over_rather_than_ending_it()
    {
        using var host = new SweepHost(Series(count: 3));

        var cursor = await host.Provider.SweepAsync("last-week", TestContext.Current.CancellationToken);

        Assert.Null(cursor);
        Assert.Contains("anidb-ids=1", Assert.Single(host.Api.Lookups), StringComparison.Ordinal);
        Assert.Contains(LogLevel.Warning, host.Logger.Entries);
    }

    #endregion

    #region Deadlines

    [Fact]
    public async Task A_chunk_whose_budget_is_already_spent_hands_its_cursor_straight_back()
    {
        using var host = new SweepHost(Series(count: 3));
        using var deadline = new CancellationTokenSource();
        await deadline.CancelAsync();

        var cursor = await host.Provider.SweepAsync("1", deadline.Token);

        Assert.Equal("1", cursor);
        Assert.Empty(host.Api.Requests);
    }

    [Fact]
    public async Task A_chunk_that_runs_out_of_budget_resumes_after_the_last_series_it_finished()
    {
        using var deadline = new CancellationTokenSource();
        // The first lookup is answered; the deadline fires during the second,
        // the way it fires mid-request on a slow source.
        using var host = new SweepHost(Series(AnimeScheduleApiClient.PageSize + 2), (uri, requests) =>
        {
            if (!uri.Contains("anidb-ids=", StringComparison.Ordinal) || requests.Count(other => other.Contains("anidb-ids=", StringComparison.Ordinal)) < 2)
                return false;

            deadline.Cancel();
            return true;
        });

        var cursor = await host.Provider.SweepAsync(null, deadline.Token);

        // The last series of the first lookup's block, so the work the chunk
        // did is kept.
        Assert.Equal(AnimeScheduleApiClient.PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture), cursor);
    }

    #endregion

    #region Suspension

    [Fact]
    public async Task A_rate_limit_after_the_retry_ends_the_pass_and_holds_the_next_one_back()
    {
        using var host = new SweepHost(Series(count: 3));
        host.Api.FailWith = HttpStatusCode.TooManyRequests;

        var cursor = await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);
        var requests = host.Api.Requests.Count;
        var next = await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.Null(cursor);
        Assert.Null(next);
        Assert.Equal(2, requests);
        Assert.Equal(requests, host.Api.Requests.Count);
    }

    #endregion

    #region The app token

    [Fact]
    public async Task A_sweep_without_an_app_token_ends_without_touching_the_network()
    {
        using var host = new SweepHost(Series(count: 3), appToken: null);

        var cursor = await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.Null(cursor);
        Assert.Empty(host.Api.Requests);
    }

    #endregion

    #region Logging

    [Fact]
    public async Task A_sweep_says_nothing_at_information_level()
    {
        using var host = new SweepHost(Series(count: 3));

        await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(LogLevel.Information, host.Logger.Entries);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Shoko series numbered from one up, each backed by the AniDB anime of
    /// the same ID with no dates, so every one could be airing.
    /// </summary>
    /// <param name="count">How many series to build.</param>
    /// <returns>The series.</returns>
    private static IReadOnlyList<IShokoSeries> Series(int count)
        => [.. Enumerable.Range(1, count).Select(id => Host.ShokoSeries(id))];

    /// <summary>
    /// A stored lookup state.
    /// </summary>
    /// <param name="anidbAnimeId">The AniDB anime ID.</param>
    /// <param name="state">The state.</param>
    /// <param name="fingerprint">The fingerprint the state was decided on.</param>
    /// <param name="nextCheckAt">When the next lookup is due, or <c>null</c>.</param>
    /// <returns>The row.</returns>
    private static AnidbLookup Lookup(int anidbAnimeId, LookupState state, string fingerprint, DateTime? nextCheckAt)
        => new()
        {
            AnidbAnimeID = anidbAnimeId,
            State = state,
            Fingerprint = fingerprint,
            LastCheckedAt = DateTime.UtcNow.AddDays(-1),
            NextCheckAt = nextCheckAt,
            ConsecutiveMisses = state is LookupState.GivenUp ? LookupPolicy.MaxConsecutiveMisses : 1,
        };

    /// <summary>
    /// The provider, wired to a stand-in AnimeSchedule.net, an in-memory
    /// database and a metadata service holding a fixed set of series.
    /// </summary>
    private sealed class SweepHost : IDisposable
    {
        private readonly HttpClient _httpClient;

        private readonly TestDatabase _database = new();

        public RecordingLogger<AnimeScheduleProvider> Logger { get; } = new();

        public AnimeScheduleProvider Provider { get; }

        public FakeAnimeScheduleApi Api { get; }

        public AnimeScheduleStore Store { get; }

        public FakeScheduleService Schedules { get; } = new();

        public SweepHost(
            IReadOnlyList<IShokoSeries> series,
            Func<string, IReadOnlyList<string>, bool>? abortWhen = null,
            string? appToken = "token",
            IReadOnlyDictionary<int, (int AnilistID, int? MalID)>? links = null
        )
        {
            Api = new FakeAnimeScheduleApi(abortWhen);
            _httpClient = new HttpClient(Api);
            Store = new AnimeScheduleStore(_database);
            var configurationProvider = new ConfigurationProvider<Configuration>(new FakeConfigurationService(new Configuration { AppToken = appToken }));
            var apiClient = new AnimeScheduleApiClient(
                _httpClient,
                new RecordingLogger<AnimeScheduleApiClient>(),
                new AnimeScheduleRateLimiter { MinimumInterval = TimeSpan.Zero },
                configurationProvider
            );
            Provider = new AnimeScheduleProvider(
                apiClient,
                Schedules.Service,
                Host.MetadataService(series),
                Host.CrossReferences(links),
                Store,
                configurationProvider,
                Logger
            );
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            Api.Dispose();
            _database.Dispose();
        }
    }

    #endregion
}
