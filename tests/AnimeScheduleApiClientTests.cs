using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Plugin.AnimeSchedule.Api;
using Xunit;

namespace Shoko.Plugin.AnimeSchedule.Tests;

/// <summary>
/// With no app token configured, <see cref="AnimeScheduleApiClient"/> must
/// fail fast and quietly: no unauthenticated request to AnimeSchedule.net,
/// no unhandled exception, and no per-call log spam.
/// </summary>
public class AnimeScheduleApiClientTests
{
    private static AnimeScheduleApiClient CreateClient(RecordingLogger<AnimeScheduleApiClient> logger, string? appToken = null, HttpMessageHandler? handler = null)
    {
        var configurationService = new FakeConfigurationService(new Configuration { AppToken = appToken });
        var configurationProvider = new ConfigurationProvider<Configuration>(configurationService);
        var http = new HttpClient(handler ?? new UnreachableHttpMessageHandler());

        return new AnimeScheduleApiClient(http, logger, new AnimeScheduleRateLimiter { MinimumInterval = TimeSpan.Zero }, configurationProvider);
    }

    [Fact]
    public async Task FindAnimeByAnidbIdsAsync_NoToken_FailsWithoutThrowing()
    {
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var client = CreateClient(logger);

        var result = await client.FindAnimeByAnidbIdsAsync([12345], TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTimetableAsync_NoToken_FailsWithoutThrowing()
    {
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var client = CreateClient(logger);

        var result = await client.GetTimetableAsync(AnimeScheduleAirType.Sub, 2026, 1, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task NoToken_RepeatedCalls_DoNotSpamWarnings()
    {
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var client = CreateClient(logger);

        // Whatever this process's shared "have we warned yet" state already
        // is, it must not flip back and forth: at most one of these three
        // calls may log at Warning, and once it drops to Debug it must stay
        // there for the rest of this sequence.
        for (var i = 0; i < 3; i++)
            await client.FindAnimeByAnidbIdsAsync([1], TestContext.Current.CancellationToken);

        Assert.Equal(3, logger.Entries.Count);
        Assert.All(logger.Entries, level => Assert.True(level is LogLevel.Warning or LogLevel.Debug));
        Assert.True(logger.Entries.Count(level => level == LogLevel.Warning) <= 1);

        // Once a Debug entry appears, every entry after it must also be
        // Debug: it can never fall back to Warning.
        var firstDebugIndex = logger.Entries.IndexOf(LogLevel.Debug);
        if (firstDebugIndex >= 0)
            Assert.All(logger.Entries.Skip(firstDebugIndex), level => Assert.Equal(LogLevel.Debug, level));
    }

    [Fact]
    public async Task FindAnimeByAnidbIdsAsync_BlankToken_TreatedAsNoToken()
    {
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var client = CreateClient(logger, appToken: "   ");

        var result = await client.FindAnimeByAnidbIdsAsync([1], TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTimetableAsync_RealResponse_IsDeserialized()
    {
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, Fixture.Read("timetable-sub-2026-w38.json"));
        var client = CreateClient(logger, appToken: "token", handler: handler);

        var result = await client.GetTimetableAsync(AnimeScheduleAirType.Sub, 2026, 38, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(5, result.Count);
        Assert.Contains(result, entry => entry.Streams.Any(stream => stream.Platform == "youtube"));
        Assert.Equal("https://animeschedule.net/api/v3/timetables/sub?year=2026&week=38&tz=UTC", Assert.Single(handler.Requests));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task GetTimetableAsync_NotFoundWithPlainTextBody_IsEmptyAndOnlyLogsDebug()
    {
        // An empty week answers 404 with a plain-text body, not JSON. A sweep
        // over empty weeks would warn once per week, so a 404 is logged at
        // Debug while any other failure stays at Warning.
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.NotFound, Fixture.Read("timetable-not-found.txt"), "text/plain");
        var client = CreateClient(logger, appToken: "token", handler: handler);

        var result = await client.GetTimetableAsync(AnimeScheduleAirType.Sub, 2030, 45, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result);
        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Entries));
    }

    [Fact]
    public async Task FindAnimeByAnidbIdsAsync_NotFound_IsAnAnswerWithNoAnimeAndLogsNothing()
    {
        // With no ID known, AnimeSchedule.net answers 404 with a plain-text
        // body. That is every ID missed, not a failed request.
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.NotFound, Fixture.Read("anime-not-found.txt"), "text/plain");
        var client = CreateClient(logger, appToken: "token", handler: handler);

        var result = await client.FindAnimeByAnidbIdsAsync([99999999, 99999998], TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task FindAnimeByAnidbIdsAsync_AsksForEveryIDInOneRequestThatMatchesAny()
    {
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, Fixture.Read("anime-anidb-4880.json"));
        var client = CreateClient(logger, appToken: "token", handler: handler);

        var result = await client.FindAnimeByAnidbIdsAsync([4880, 17318], TestContext.Current.CancellationToken);

        var anime = Assert.Single(result!);
        Assert.Equal("naruto-shippuuden", anime.Route);
        Assert.Equal("anidb.net/anime/4880", anime.Websites?.Anidb);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("anidb-ids=4880&anidb-ids=17318", request, StringComparison.Ordinal);
        Assert.Contains("mt=any", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAnimeAsync_ReadsPagesUntilTheTotalIsReached()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, AnimePage(page: 1, total: 3, "a", "b"))
            .Enqueue(HttpStatusCode.OK, AnimePage(page: 2, total: 3, "c"));
        var client = CreateClient(new RecordingLogger<AnimeScheduleApiClient>(), appToken: "token", handler: handler);
        var routes = new List<string>();

        var completed = await client.ListAnimeAsync("ongoing", page => routes.AddRange(page.Select(anime => anime.Route)), TestContext.Current.CancellationToken);

        Assert.True(completed);
        Assert.Equal(["a", "b", "c"], routes);
        Assert.EndsWith("&page=2", handler.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTimetableAsync_ServerError_FailsAndWarns()
    {
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.InternalServerError, "nope", "text/plain");
        var client = CreateClient(logger, appToken: "token", handler: handler);

        var result = await client.GetTimetableAsync(AnimeScheduleAirType.Sub, 2026, 38, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries));
    }

    [Fact]
    public async Task A_run_of_server_errors_suspends_the_service()
    {
        var handler = new StubHttpMessageHandler();
        for (var i = 0; i < AnimeScheduleRateLimiter.ServerErrorsToSuspend; i++)
            handler.Enqueue(HttpStatusCode.BadGateway, "nope", "text/plain");
        var client = CreateClient(new RecordingLogger<AnimeScheduleApiClient>(), appToken: "token", handler: handler);

        for (var i = 1; i < AnimeScheduleRateLimiter.ServerErrorsToSuspend; i++)
            Assert.Null(await client.GetTimetableAsync(AnimeScheduleAirType.Sub, 2026, 38, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AnimeScheduleUnavailableException>(() => client.GetTimetableAsync(AnimeScheduleAirType.Sub, 2026, 38, TestContext.Current.CancellationToken));

        Assert.NotNull(client.SuspendedUntil);
        await Assert.ThrowsAsync<AnimeScheduleUnavailableException>(() => client.GetTimetableAsync(AnimeScheduleAirType.Sub, 2026, 38, TestContext.Current.CancellationToken));
        Assert.Equal(AnimeScheduleRateLimiter.ServerErrorsToSuspend, handler.Requests.Count);
    }

    [Fact]
    public async Task A_second_rate_limit_in_a_row_suspends_the_service()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, "", "text/plain", retryAfter: TimeSpan.Zero)
            .Enqueue(HttpStatusCode.TooManyRequests, "", "text/plain", retryAfter: TimeSpan.Zero);
        var client = CreateClient(new RecordingLogger<AnimeScheduleApiClient>(), appToken: "token", handler: handler);

        await Assert.ThrowsAsync<AnimeScheduleUnavailableException>(() => client.GetTimetableAsync(AnimeScheduleAirType.Sub, 2026, 38, TestContext.Current.CancellationToken));

        Assert.Equal(2, handler.Requests.Count);
        Assert.NotNull(client.SuspendedUntil);
    }

    [Fact]
    public async Task A_rate_limit_is_retried_once_after_its_wait()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, "", "text/plain", retryAfter: TimeSpan.Zero)
            .Enqueue(HttpStatusCode.OK, "[]");
        var client = CreateClient(new RecordingLogger<AnimeScheduleApiClient>(), appToken: "token", handler: handler);

        var result = await client.GetTimetableAsync(AnimeScheduleAirType.Sub, 2026, 38, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Null(client.SuspendedUntil);
    }

    /// <summary>
    /// One page of an <c>/anime</c> listing holding anime of the given routes.
    /// </summary>
    /// <param name="page">The page number.</param>
    /// <param name="total">The total across every page.</param>
    /// <param name="routes">The routes on this page.</param>
    /// <returns>The JSON body.</returns>
    private static string AnimePage(int page, int total, params string[] routes)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            page,
            totalAmount = total,
            anime = routes.Select(route => new { route, title = route, status = "Ongoing" }),
        });

    [Theory]
    [InlineData(0)]
    [InlineData(54)]
    public async Task GetTimetableAsync_WeekOutsideIso8601_IsNeverRequested(int week)
    {
        // AnimeSchedule.net answers week 54 with week 53's payload byte for
        // byte, so an out-of-range week must not reach the network at all or
        // the same week would be ingested twice under two numbers.
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var client = CreateClient(logger, appToken: "token", handler: new UnreachableHttpMessageHandler());

        var result = await client.GetTimetableAsync(AnimeScheduleAirType.Sub, 2026, week, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result);
        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Entries));
    }
}
