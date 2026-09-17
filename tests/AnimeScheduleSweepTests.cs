using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Plugin.AnimeSchedule.Api;
using Xunit;

namespace Shoko.Plugin.AnimeSchedule.Tests;

/// <summary>
/// Tests for the core-driven sweep: the server hands the provider a cursor
/// and a deadline, and expects a chunk that stops when the deadline fires and
/// says where the next one picks up.
/// </summary>
public class AnimeScheduleSweepTests
{
    // Three air types, this week and the next, fetched once per chunk before
    // any series is looked at.
    private const int TimetableRequests = 6;

    #region Cursors

    [Fact]
    public async Task A_sweep_that_walks_every_series_ends_with_no_cursor()
    {
        using var host = new SweepHost(Series(count: 3));

        var cursor = await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.Null(cursor);
        Assert.Equal(TimetableRequests + 3, host.Requests.Count);
    }

    [Fact]
    public async Task A_sweep_resumes_after_the_series_the_cursor_names()
    {
        using var host = new SweepHost(Series(count: 3));

        var cursor = await host.Provider.SweepAsync("2", TestContext.Current.CancellationToken);

        Assert.Null(cursor);
        var lookup = Assert.Single(host.Requests, uri => uri.Contains("anidb-ids=", StringComparison.Ordinal));
        Assert.Contains("anidb-ids=3", lookup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sweep_with_nothing_left_to_walk_ends_without_asking_for_a_timetable()
    {
        using var host = new SweepHost(Series(count: 3));

        var cursor = await host.Provider.SweepAsync("9000", TestContext.Current.CancellationToken);

        Assert.Null(cursor);
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task An_unreadable_cursor_starts_the_sweep_over_rather_than_ending_it()
    {
        using var host = new SweepHost(Series(count: 3));

        var cursor = await host.Provider.SweepAsync("last-week", TestContext.Current.CancellationToken);

        Assert.Null(cursor);
        Assert.Equal(3, host.Requests.Count(uri => uri.Contains("anidb-ids=", StringComparison.Ordinal)));
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
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task A_chunk_that_runs_out_of_budget_resumes_at_the_last_series_it_finished()
    {
        using var deadline = new CancellationTokenSource();
        // The first series is looked up and answered; the deadline fires
        // during the second, the way it fires mid-request on a slow source.
        using var host = new SweepHost(Series(count: 3), (uri, requests) =>
        {
            if (!uri.Contains("anidb-ids=", StringComparison.Ordinal) || requests.Count(other => other.Contains("anidb-ids=", StringComparison.Ordinal)) < 2)
                return false;

            deadline.Cancel();
            return true;
        });

        var cursor = await host.Provider.SweepAsync(null, deadline.Token);

        // The first series, so the work the chunk did is kept.
        Assert.Equal("1", cursor);
    }

    #endregion

    #region The app token

    [Fact]
    public async Task A_sweep_without_an_app_token_ends_without_touching_the_network()
    {
        using var host = new SweepHost(Series(count: 3), appToken: null);

        var cursor = await host.Provider.SweepAsync(null, TestContext.Current.CancellationToken);

        Assert.Null(cursor);
        Assert.Empty(host.Requests);
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
    /// the same ID, so a lookup's <c>anidb-ids</c> says which series a chunk
    /// covered.
    /// </summary>
    /// <param name="count">How many series to build.</param>
    /// <returns>The series.</returns>
    private static IReadOnlyList<IShokoSeries> Series(int count)
        => [.. Enumerable.Range(1, count).Select(Host.ShokoSeries)];

    /// <summary>
    /// The provider, wired to a stubbed AnimeSchedule.net and a metadata
    /// service holding a fixed set of series.
    /// </summary>
    private sealed class SweepHost : IDisposable
    {
        private readonly StubAnimeScheduleHandler _handler;
        private readonly HttpClient _httpClient;

        public RecordingLogger<AnimeScheduleProvider> Logger { get; } = new();

        public AnimeScheduleProvider Provider { get; }

        public IReadOnlyList<string> Requests => _handler.Requests;

        public SweepHost(IReadOnlyList<IShokoSeries> series, Func<string, IReadOnlyList<string>, bool>? abortWhen = null, string? appToken = "token")
        {
            _handler = new StubAnimeScheduleHandler(abortWhen);
            _httpClient = new HttpClient(_handler);
            var configurationProvider = new ConfigurationProvider<Configuration>(new FakeConfigurationService(new Configuration { AppToken = appToken }));
            var apiClient = new AnimeScheduleApiClient(
                _httpClient,
                new RecordingLogger<AnimeScheduleApiClient>(),
                new AnimeScheduleRateLimiter(),
                configurationProvider
            );
            Provider = new AnimeScheduleProvider(
                apiClient,
                // AnimeSchedule.net knows none of these series, so the sweep
                // never reaches a write.
                scheduleService: null!,
                Host.MetadataService(series),
                configurationProvider,
                Logger
            );
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            _handler.Dispose();
        }
    }

    #endregion
}
