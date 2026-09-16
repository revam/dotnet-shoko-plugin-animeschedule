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

        return new AnimeScheduleApiClient(http, logger, new AnimeScheduleRateLimiter(), configurationProvider);
    }

    [Fact]
    public async Task FindAnimeByAnidbIdAsync_NoToken_ReturnsNullWithoutThrowing()
    {
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var client = CreateClient(logger);

        var result = await client.FindAnimeByAnidbIdAsync(12345, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTimetableAsync_NoToken_ReturnsEmptyWithoutThrowing()
    {
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var client = CreateClient(logger);

        var result = await client.GetTimetableAsync(AnimeScheduleAirType.Sub, 2026, 1, TestContext.Current.CancellationToken);

        Assert.Empty(result);
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
            await client.FindAnimeByAnidbIdAsync(1, TestContext.Current.CancellationToken);

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
    public async Task FindAnimeByAnidbIdAsync_BlankToken_TreatedAsNoToken()
    {
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var client = CreateClient(logger, appToken: "   ");

        var result = await client.FindAnimeByAnidbIdAsync(1, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTimetableAsync_RealResponse_IsDeserialized()
    {
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, Fixture.Read("timetable-sub-2026-w38.json"));
        var client = CreateClient(logger, appToken: "token", handler: handler);

        var result = await client.GetTimetableAsync(AnimeScheduleAirType.Sub, 2026, 38, TestContext.Current.CancellationToken);

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

        Assert.Empty(result);
        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Entries));
    }

    [Fact]
    public async Task FindAnimeByAnidbIdAsync_NotFoundWithPlainTextBody_IsNullAndOnlyLogsDebug()
    {
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.NotFound, Fixture.Read("anime-not-found.txt"), "text/plain");
        var client = CreateClient(logger, appToken: "token", handler: handler);

        var result = await client.FindAnimeByAnidbIdAsync(99999999, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Entries));
    }

    [Fact]
    public async Task FindAnimeByAnidbIdAsync_RealResponse_ReturnsTheFirstMatch()
    {
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, Fixture.Read("anime-anidb-4880.json"));
        var client = CreateClient(logger, appToken: "token", handler: handler);

        var result = await client.FindAnimeByAnidbIdAsync(4880, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("naruto-shippuuden", result.Route);
        Assert.Equal(500, result.Episodes);
        Assert.Equal("Finished", result.Status);
    }

    [Fact]
    public async Task GetTimetableAsync_ServerError_StillWarns()
    {
        var logger = new RecordingLogger<AnimeScheduleApiClient>();
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.InternalServerError, "nope", "text/plain");
        var client = CreateClient(logger, appToken: "token", handler: handler);

        var result = await client.GetTimetableAsync(AnimeScheduleAirType.Sub, 2026, 38, TestContext.Current.CancellationToken);

        Assert.Empty(result);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries));
    }

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

        Assert.Empty(result);
        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Entries));
    }
}
