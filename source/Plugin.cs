using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Plugin;
using Shoko.Plugin.AnimeSchedule.Api;

namespace Shoko.Plugin.AnimeSchedule;

/// <summary>
/// Plugin providing an <c>IAiringScheduleProvider</c> backed by
/// AnimeSchedule.net.
/// </summary>
/// <remarks>
/// AnimeSchedule.net's API terms of use require visible credit to
/// AnimeSchedule.net wherever data from it is shown; see the README.
/// </remarks>
public class Plugin : IPlugin, IPluginServiceRegistration
{
    /// <inheritdoc/>
    public Guid ID { get; private init; } = new("a92b3752-688a-408f-a03a-bad24718449a");

    /// <inheritdoc/>
    public string Name { get; private set; } = "AnimeSchedule.net";

    /// <inheritdoc/>
    public string Description { get; private set; } = """
        Pulls airing schedules, delays and streaming links from
        AnimeSchedule.net for the anime in your library. Requires your own
        AnimeSchedule.net application token. Data is provided by
        AnimeSchedule.net.
    """;

    /// <inheritdoc/>
    public static void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        // The provider itself is not registered: the server finds it by
        // reflection, constructs it with these services, and sweeps that very
        // instance, so nothing here needs a reference to it.
        serviceCollection.AddSingleton<AnimeScheduleRateLimiter>();
        // The contact URL is read back from the plugin's own registered info
        // rather than written here, so it names wherever this build was
        // published from instead of hard-coding one host into the source. A
        // local build has no repository URL stamped, so the comment is left
        // off rather than sent empty.
        serviceCollection.AddHttpClient<AnimeScheduleApiClient>((provider, client) =>
        {
            var info = provider.GetRequiredService<IPluginManager>().GetPluginInfo<Plugin>();
            var userAgent = $"Shoko.Plugin.AnimeSchedule/{info?.Version.Version.ToString(3) ?? "1.0"}";
            if (info?.RepositoryUrl is { Length: > 0 } repositoryUrl)
                userAgent += $" (+{repositoryUrl})";
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .UseSocketsHttpHandler((handler, _) =>
            {
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);
                handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1);
            });
    }
}
