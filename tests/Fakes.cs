using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NJsonSchema;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Enums;
using Shoko.Abstractions.Config.Events;
using Shoko.Abstractions.Config.Services;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Airing;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.CrossReferences;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.UI;
using Shoko.Abstractions.User;
using Shoko.Plugin.AnimeSchedule.Storage;

// The host's interface declares events this fake has no reason to raise.
#pragma warning disable CS0067

namespace Shoko.Plugin.AnimeSchedule.Tests;

/// <summary>
/// Just enough configuration service to hand a <see cref="ConfigurationProvider{TConfig}"/>
/// back one configuration instance. Everything else throws rather than
/// returning a plausible default, so a future call into the host that these
/// tests do not model announces itself instead of quietly passing.
/// </summary>
internal sealed class FakeConfigurationService(Configuration configuration) : IConfigurationService
{
    public event EventHandler<ConfigurationSavedEventArgs>? Saved;

    public event EventHandler<ConfigurationRequiresRestartEventArgs>? RequiresRestart;

    public IReadOnlyDictionary<Guid, IReadOnlySet<string>> RestartPendingFor => throw new NotSupportedException();

    public IReadOnlyDictionary<Guid, IReadOnlySet<string>> LoadedEnvironmentVariables => throw new NotSupportedException();

    public void AddParts(IEnumerable<Type> configurationTypes) => throw new NotSupportedException();

    public ConfigurationProvider<TConfig> CreateProvider<TConfig>() where TConfig : class, IConfiguration, new() => new(this);

    public IEnumerable<ConfigurationInfo> GetAllConfigurationInfos() => throw new NotSupportedException();

    public IReadOnlyList<ConfigurationInfo> GetConfigurationInfo(IPlugin plugin) => throw new NotSupportedException();

    public ConfigurationInfo? GetConfigurationInfo(Guid configurationId) => throw new NotSupportedException();

    public ConfigurationInfo? GetConfigurationInfo(Type type) => throw new NotSupportedException();

    // The provider routes Load() through the configuration's info, and the
    // info is only ever compared for identity on a Saved event this fake
    // never raises — so it need not be a real one.
    public ConfigurationInfo GetConfigurationInfo<TConfig>() where TConfig : class, IConfiguration, new() => null!;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Validate(ConfigurationInfo info, string json) => throw new NotSupportedException();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Validate(ConfigurationInfo info, IConfiguration config) => throw new NotSupportedException();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Validate<TConfig>(TConfig config) where TConfig : class, IConfiguration, new() => throw new NotSupportedException();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Validate(string json, JsonSchema schema) => throw new NotSupportedException();

    public ConfigurationActionResult PerformCustomAction(ConfigurationInfo info, IConfiguration configuration, string path, string actionID, IUser? user = null, Uri? uri = null) => throw new NotSupportedException();

    public ConfigurationActionResult PerformCustomAction<TConfig>(TConfig configuration, string path, string actionID, IUser? user = null, Uri? uri = null) where TConfig : class, IConfiguration, new() => throw new NotSupportedException();

    public Task<IReadOnlyList<UiOption>> GetOptionsAsync(ConfigurationInfo info, IConfiguration configuration, string path, IUser? user = null, Uri? uri = null) => throw new NotSupportedException();

    public Task<IReadOnlyList<UiOption>> GetOptionsAsync<TConfig>(TConfig configuration, string path, IUser? user = null, Uri? uri = null) where TConfig : class, IConfiguration, new() => throw new NotSupportedException();

    public ConfigurationActionResult PerformReactiveAction(ConfigurationInfo info, IConfiguration configuration, string path, ConfigurationActionType actionType, ReactiveEventType reactiveEventType = ReactiveEventType.All, IUser? user = null, Uri? uri = null) => throw new NotSupportedException();

    public ConfigurationActionResult PerformReactiveAction<TConfig>(TConfig configuration, string path, ConfigurationActionType actionType, ReactiveEventType reactiveEventType = ReactiveEventType.All, IUser? user = null, Uri? uri = null) where TConfig : class, IConfiguration, new() => throw new NotSupportedException();

    public IConfiguration New(ConfigurationInfo info) => throw new NotSupportedException();

    public TConfig New<TConfig>() where TConfig : class, IConfiguration, new() => throw new NotSupportedException();

    public IConfiguration Load(ConfigurationInfo info, bool copy = false) => configuration;

    public TConfig Load<TConfig>(bool copy = false) where TConfig : class, IConfiguration, new() => (TConfig)(object)configuration;

    public bool Save(ConfigurationInfo info, IConfiguration json) => throw new NotSupportedException();

    public bool Save(ConfigurationInfo info, string json) => throw new NotSupportedException();

    public bool Save<TConfig>() where TConfig : class, IConfiguration, new() => throw new NotSupportedException();

    public bool Save<TConfig>(TConfig config) where TConfig : class, IConfiguration, new() => throw new NotSupportedException();

    public bool Save<TConfig>(string json) where TConfig : class, IConfiguration, new() => throw new NotSupportedException();

    public string GetSchema(ConfigurationInfo info) => throw new NotSupportedException();

    public JsonSchema GenerateSchema(Type type) => throw new NotSupportedException();

    public UiDefinition GenerateUiDefinition(Type type) => throw new NotSupportedException();

    public string Serialize(IConfiguration config) => throw new NotSupportedException();

    public string SerializeWithMasking(IConfiguration config) => throw new NotSupportedException();

    public string MaskSecrets(ConfigurationInfo info, string json) => throw new NotSupportedException();

    public string RestoreMaskedSecrets(ConfigurationInfo info, string json) => throw new NotSupportedException();

    public IConfiguration Deserialize(ConfigurationInfo info, string json) => throw new NotSupportedException();
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> that fails the test if it is ever
/// asked to send a request. Used to prove that a code path with no app
/// token never reaches the network.
/// </summary>
internal sealed class UnreachableHttpMessageHandler : System.Net.Http.HttpMessageHandler
{
    protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        => throw new InvalidOperationException($"Unexpected HTTP request sent to {request.RequestUri}.");
}

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that just records the level of
/// each entry logged, so a test can assert on how noisy a code path is
/// without pulling in a mocking framework.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<LogLevel> Entries { get; } = [];

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add(logLevel);
}

/// <summary>
/// Reads the captured AnimeSchedule.net responses in <c>tests/Fixtures</c>.
/// Every fixture is a verbatim (if trimmed to a handful of entries) capture
/// of a real response, so a model that only matches the documentation fails
/// here the same way it fails against the live API.
/// </summary>
internal static class Fixture
{
    public static string Read(string fileName)
        => System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));
}

/// <summary>
/// An <see cref="System.Net.Http.HttpMessageHandler"/> that replays a fixed
/// queue of responses, recording the URIs it was asked for. Used to drive
/// the API client over captured responses without touching the network.
/// </summary>
internal sealed class StubHttpMessageHandler : System.Net.Http.HttpMessageHandler
{
    private readonly Queue<(System.Net.HttpStatusCode StatusCode, string Body, string ContentType, TimeSpan? RetryAfter)> _responses = new();

    public List<string> Requests { get; } = [];

    public StubHttpMessageHandler Enqueue(System.Net.HttpStatusCode statusCode, string body, string contentType = "application/json", TimeSpan? retryAfter = null)
    {
        _responses.Enqueue((statusCode, body, contentType, retryAfter));
        return this;
    }

    protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri?.ToString() ?? "");
        if (!_responses.TryDequeue(out var response))
            throw new InvalidOperationException($"No stubbed response left for {request.RequestUri}.");

        var message = new System.Net.Http.HttpResponseMessage(response.StatusCode)
        {
            Content = new System.Net.Http.StringContent(response.Body, System.Text.Encoding.UTF8, response.ContentType),
        };
        if (response.RetryAfter is { } retryAfter)
            message.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
        return System.Threading.Tasks.Task.FromResult(message);
    }
}

/// <summary>
/// Builds a stand-in for one of the host's interfaces, answering the members
/// a test names and throwing <see cref="NotSupportedException"/> for every
/// other one, so a code path that starts asking the host for more announces
/// itself instead of quietly passing. The host's interfaces are wide and
/// still moving, so this beats writing out a hundred members that only throw.
/// </summary>
internal static class Stub
{
    /// <summary>
    /// A stand-in for <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The interface to stand in for.</typeparam>
    /// <param name="answers">
    /// The members to answer, by name, each taking the call's arguments.
    /// Property getters are named without their <c>get_</c> prefix, and events
    /// are always accepted and never raised.
    /// </param>
    /// <returns>The stand-in.</returns>
    public static T Of<T>(params (string Member, Func<object?[], object?> Answer)[] answers) where T : class
        => StubProxy<T>.Create(answers.ToDictionary(answer => answer.Member, answer => answer.Answer, StringComparer.Ordinal));
}

/// <inheritdoc cref="Stub"/>
/// <typeparam name="T">The interface to stand in for.</typeparam>
internal class StubProxy<T> : System.Reflection.DispatchProxy where T : class
{
    private IReadOnlyDictionary<string, Func<object?[], object?>> _answers = new Dictionary<string, Func<object?[], object?>>(StringComparer.Ordinal);

    internal static T Create(IReadOnlyDictionary<string, Func<object?[], object?>> answers)
    {
        var proxy = Create<T, StubProxy<T>>()!;
        ((StubProxy<T>)(object)proxy)._answers = answers;
        return proxy;
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">The member was not stubbed.</exception>
    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        if (name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal))
            return null;

        var member = name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal) ? name[4..] : name;
        return _answers.TryGetValue(member, out var answer)
            ? answer(args ?? [])
            : throw new NotSupportedException($"{typeof(T).Name}.{member} was not stubbed.");
    }
}

/// <summary>
/// The host entities and services the sweep tests stand up.
/// </summary>
internal static class Host
{
    /// <summary>
    /// The AniList source, registered the way the AniList plugin registers it.
    /// </summary>
    public static MetadataSource Anilist { get; } = RegisterAnilist();

    /// <summary>
    /// Registers the AniList source once for the whole test run.
    /// </summary>
    /// <returns>The source.</returns>
    private static MetadataSource RegisterAnilist()
        => MetadataSource.TryGet("anilist", out var source) ? source : MetadataSource.Register("AniList", "anilist");

    /// <summary>
    /// A metadata service holding the given shoko series.
    /// </summary>
    /// <param name="series">The series the sweep walks.</param>
    /// <returns>The metadata service.</returns>
    public static IMetadataService MetadataService(IEnumerable<IShokoSeries> series)
        => Stub.Of<IMetadataService>(("GetAllShokoSeries", _ => series));

    /// <summary>
    /// A shoko series backed by an AniDB anime of the same ID.
    /// </summary>
    /// <param name="seriesId">The shoko series ID, and the AniDB anime ID.</param>
    /// <param name="airDate">The AniDB start date, or <c>null</c>.</param>
    /// <param name="endDate">The AniDB end date, or <c>null</c>.</param>
    /// <param name="malIds">AniDB's own MyAnimeList links.</param>
    /// <param name="episodes">The AniDB episode count.</param>
    /// <returns>The series.</returns>
    public static IShokoSeries ShokoSeries(int seriesId, PartialDateOnly? airDate = null, PartialDateOnly? endDate = null, IReadOnlyList<int>? malIds = null, int episodes = 12)
    {
        var anidbAnime = Stub.Of<IAnidbAnime>(
            ("ID", _ => new MetadataGuid(MetadataSource.AniDB, MetadataEntityType.Series, seriesId.ToString(CultureInfo.InvariantCulture))),
            ("AirDate", _ => airDate),
            ("EndDate", _ => endDate),
            ("EpisodeCounts", _ => new EpisodeCounts { Episodes = episodes }),
            ("MalIDs", _ => malIds ?? [])
        );
        return Stub.Of<IShokoSeries>(
            ("ID", _ => ShokoSeriesID(seriesId)),
            ("LocalID", _ => seriesId),
            ("AnidbAnime", _ => anidbAnime)
        );
    }

    /// <summary>
    /// The metadata ID of a shoko series.
    /// </summary>
    /// <param name="seriesId">The shoko series ID.</param>
    /// <returns>The ID.</returns>
    public static MetadataGuid ShokoSeriesID(int seriesId)
        => new(MetadataSource.Shoko, MetadataEntityType.Series, seriesId.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// A cross-reference store linking AniDB anime to AniList entries, each
    /// carrying the MyAnimeList ID AniList gives it.
    /// </summary>
    /// <param name="links">The AniList entry and its MyAnimeList ID, by AniDB anime ID.</param>
    /// <returns>The store.</returns>
    public static IMetadataCrossReferenceStore CrossReferences(IReadOnlyDictionary<int, (int AnilistID, int? MalID)>? links = null)
    {
        // Registered before the provider first asks for the source.
        var anilist = Anilist;
        return Stub.Of<IMetadataCrossReferenceStore>(("GetSeriesLinks", args =>
        {
            var anidbAnimeId = (int)args[0]!;
            if (links is null || !links.TryGetValue(anidbAnimeId, out var link) || args[1] is not MetadataSource source || source != anilist)
                return (IReadOnlyList<IMetadataSeriesCrossReference>)[];

            var malIds = link.MalID is { } malId
                ? (IReadOnlyList<MetadataGuid>)[new MetadataGuid(MetadataSource.Parse("mal"), MetadataEntityType.Series, malId.ToString(CultureInfo.InvariantCulture))]
                : [];
            var anilistSeries = Stub.Of<ISeries>(("CrossSourceIDs", _ => malIds));
            return (IReadOnlyList<IMetadataSeriesCrossReference>)
            [
                Stub.Of<IMetadataSeriesCrossReference>(
                    ("ProviderID", _ => new MetadataGuid(anilist, MetadataEntityType.Series, link.AnilistID.ToString(CultureInfo.InvariantCulture))),
                    ("Ordering", _ => 0),
                    ("Provider", _ => anilistSeries)
                ),
            ];
        }));
    }
}

/// <summary>
/// The plugin's database in memory, migrated the way the server migrates it,
/// for as long as the instance lives.
/// </summary>
internal sealed class TestDatabase : IDbContextFactory<AnimeScheduleDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private readonly DbContextOptions<AnimeScheduleDbContext> _options;

    public TestDatabase()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AnimeScheduleDbContext>().UseSqlite(_connection).Options;
        using var context = CreateDbContext();
        context.Database.Migrate();
    }

    public AnimeScheduleDbContext CreateDbContext()
        => new(_options);

    public void Dispose()
        => _connection.Dispose();
}

/// <summary>
/// The airing schedule service a sweep writes to, recording each write. It
/// holds a fixed set of this provider's schedules and their airings.
/// </summary>
internal sealed class FakeScheduleService
{
    private readonly Guid _providerID = Guid.NewGuid();

    /// <summary>
    /// The schedules the provider holds, with their airings.
    /// </summary>
    public Dictionary<IAiringSchedule, IReadOnlyList<IEpisodeAiring>> Schedules { get; } = [];

    /// <summary>
    /// The keys of the schedules written, in order.
    /// </summary>
    public List<string> WrittenScheduleKeys { get; } = [];

    /// <summary>
    /// The airings named as removals, in order.
    /// </summary>
    public List<IEpisodeAiring> Removals { get; } = [];

    /// <summary>
    /// The service.
    /// </summary>
    public IAiringScheduleService Service { get; }

    public FakeScheduleService()
    {
        Service = Stub.Of<IAiringScheduleService>(
            ("GetProviderInfo", args => new AiringScheduleProviderInfo
            {
                ID = _providerID,
                Version = new Version(1, 0),
                Name = "AnimeSchedule.net",
                Description = "",
                Provider = (IAiringScheduleProvider)args[0]!,
                ConfigurationInfo = null,
                PluginInfo = null!,
                Priority = 0,
                EnabledKinds = [],
                SweepInterval = TimeSpan.FromMinutes(30),
            }),
            ("GetSchedulesForProvider", _ => (IReadOnlyList<IAiringSchedule>)[.. Schedules.Keys]),
            ("GetAiringsForSchedule", args => Schedules.FirstOrDefault(pair => pair.Key.ID == (Guid)args[0]!).Value ?? []),
            ("AddOrUpdateSchedule", args =>
            {
                var data = (AiringScheduleData)args[1]!;
                WrittenScheduleKeys.Add(data.Key!);
                return Schedules.Keys.FirstOrDefault(schedule => schedule.SeriesID == data.Series.ID && schedule.Key == data.Key)
                    ?? Schedule(data.Series.ID, data.Key!);
            }),
            ("MergeAirings", args =>
            {
                Removals.AddRange((IEnumerable<IEpisodeAiring>?)args[3] ?? []);
                return (IReadOnlyList<IEpisodeAiring>)[];
            })
        );
    }

    /// <summary>
    /// A schedule of this provider.
    /// </summary>
    /// <param name="seriesId">The series it is for.</param>
    /// <param name="key">The schedule key.</param>
    /// <returns>The schedule.</returns>
    public static IAiringSchedule Schedule(MetadataGuid seriesId, string key)
    {
        var id = Guid.NewGuid();
        return Stub.Of<IAiringSchedule>(
            ("ID", _ => id),
            ("Key", _ => key),
            ("SeriesID", _ => seriesId),
            ("LastEpisodeNumber", _ => null)
        );
    }

    /// <summary>
    /// An airing at the given time.
    /// </summary>
    /// <param name="airedAt">When it airs, in UTC.</param>
    /// <param name="key">Its key.</param>
    /// <returns>The airing.</returns>
    public static IEpisodeAiring Airing(DateTime airedAt, string key)
        => Stub.Of<IEpisodeAiring>(("AiredAt", _ => airedAt), ("Key", _ => key));
}

/// <summary>
/// A stand-in for AnimeSchedule.net, answering the airing lists, the AniDB
/// lookups and the timetables from what a test sets up, recording each
/// request, and able to abort one the way a sweep's deadline does.
/// </summary>
/// <param name="abortWhen">
/// Optional. Called with each request URI and everything requested so far;
/// answering <c>true</c> aborts that request.
/// </param>
internal sealed class FakeAnimeScheduleApi(Func<string, IReadOnlyList<string>, bool>? abortWhen = null) : System.Net.Http.HttpMessageHandler
{
    private readonly List<string> _requests = [];

    /// <summary>
    /// Every request URI, in order.
    /// </summary>
    public IReadOnlyList<string> Requests => _requests;

    /// <summary>
    /// The anime of each airing list, by status.
    /// </summary>
    public Dictionary<string, List<object>> Lists { get; } = new(StringComparer.Ordinal)
    {
        ["ongoing"] = [],
        ["upcoming"] = [],
    };

    /// <summary>
    /// The anime a lookup by AniDB ID finds.
    /// </summary>
    public List<object> Known { get; } = [];

    /// <summary>
    /// The entries of each air type's timetable, by its path segment; every
    /// week answers the same.
    /// </summary>
    public Dictionary<string, List<object>> Timetables { get; } = new(StringComparer.Ordinal)
    {
        ["raw"] = [],
        ["sub"] = [],
        ["dub"] = [],
    };

    /// <summary>
    /// Answers every request this status instead, when set.
    /// </summary>
    public System.Net.HttpStatusCode? FailWith { get; set; }

    /// <summary>
    /// Answers the timetables this status instead, when set.
    /// </summary>
    public System.Net.HttpStatusCode? FailTimetablesWith { get; set; }

    /// <summary>
    /// The requests that were lookups by AniDB ID.
    /// </summary>
    public IReadOnlyList<string> Lookups => [.. _requests.Where(uri => uri.Contains("anidb-ids=", StringComparison.Ordinal))];

    /// <summary>
    /// An anime as the API returns it, with links to the given IDs.
    /// </summary>
    /// <param name="route">The route.</param>
    /// <param name="anidbId">The AniDB anime ID, or <c>null</c>.</param>
    /// <param name="anilistId">The AniList anime ID, or <c>null</c>.</param>
    /// <param name="malId">The MyAnimeList anime ID, or <c>null</c>.</param>
    /// <returns>The anime.</returns>
    public static object Anime(string route, int? anidbId = null, int? anilistId = null, int? malId = null)
        => new
        {
            route,
            title = route,
            status = "Ongoing",
            episodes = 12,
            websites = new
            {
                anidb = anidbId is { } anidb ? $"anidb.net/anime/{anidb}" : "",
                aniList = anilistId is { } anilist ? $"anilist.co/anime/{anilist}/{route}" : "",
                mal = malId is { } mal ? $"myanimelist.net/anime/{mal}/{route}" : "",
            },
        };

    /// <summary>
    /// A timetable entry of the given route, airing at the given time.
    /// </summary>
    /// <param name="route">The route.</param>
    /// <param name="airsAt">When the episode airs.</param>
    /// <param name="episode">The episode number.</param>
    /// <returns>The entry.</returns>
    public static object Entry(string route, DateTime airsAt, int episode = 1)
        => new { route, title = route, episodeDate = airsAt, episodeNumber = episode, airingStatus = "unaired" };

    /// <inheritdoc/>
    /// <exception cref="OperationCanceledException">The request was aborted.</exception>
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!.PathAndQuery;
        _requests.Add(uri);

        if (abortWhen is not null && abortWhen(uri, _requests))
            throw new OperationCanceledException(cancellationToken);

        if (FailWith is { } failWith)
            return Answer(failWith, "");

        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        if (uri.Contains("/timetables/", StringComparison.Ordinal))
        {
            if (FailTimetablesWith is { } failTimetablesWith)
                return Answer(failTimetablesWith, "");

            var segment = request.RequestUri.Segments[^1];
            return Answer(System.Net.HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(Timetables[segment]));
        }

        List<object> anime;
        if (query.GetValues("anidb-ids") is { } ids)
        {
            var wanted = ids.ToHashSet(StringComparer.Ordinal);
            anime = [.. Known.Where(entry => wanted.Contains(System.Text.Json.JsonSerializer.SerializeToElement(entry).GetProperty("websites").GetProperty("anidb").GetString()!.Split('/')[^1]))];
        }
        else
        {
            anime = Lists[query["airing-statuses"]!];
        }

        if (anime.Count == 0)
            return Answer(System.Net.HttpStatusCode.NotFound, "Couldn't find anime with these parameters.");

        return Answer(System.Net.HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new { page = 1, totalAmount = anime.Count, anime }));
    }

    /// <summary>
    /// A response with the given status and body.
    /// </summary>
    /// <param name="statusCode">The status.</param>
    /// <param name="body">The body.</param>
    /// <returns>The response.</returns>
    private static Task<System.Net.Http.HttpResponseMessage> Answer(System.Net.HttpStatusCode statusCode, string body)
    {
        var message = new System.Net.Http.HttpResponseMessage(statusCode)
        {
            Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

        // A rate limit that asks for no wait, so the retry is not slept on.
        if (statusCode == System.Net.HttpStatusCode.TooManyRequests)
            message.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
        return Task.FromResult(message);
    }
}
