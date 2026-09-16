using System.Linq;
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
    private static AnimeScheduleApiClient CreateClient(RecordingLogger<AnimeScheduleApiClient> logger, string? appToken = null)
    {
        var configurationService = new FakeConfigurationService(new Configuration { AppToken = appToken });
        var configurationProvider = new ConfigurationProvider<Configuration>(configurationService);
        var http = new HttpClient(new UnreachableHttpMessageHandler());

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
}
