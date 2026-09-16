using System;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Plugin;
using Shoko.Plugin.AnimeSchedule.Api;
using Shoko.Plugin.AnimeSchedule.Jobs;
using Shoko.QueueProcessor.Scheduling;

namespace Shoko.Plugin.AnimeSchedule;

/// <summary>
/// Plugin providing an <c>IAiringScheduleProvider</c> backed by
/// AnimeSchedule.net.
/// </summary>
/// <remarks>
/// AnimeSchedule.net's API terms of use require visible credit to
/// AnimeSchedule.net wherever data from it is shown; see the README.
/// </remarks>
public class Plugin : IPlugin, IPluginServiceRegistration, IPluginApplicationRegistration
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
        serviceCollection.AddSingleton<AnimeScheduleRateLimiter>();
        serviceCollection.AddSingleton<AnimeScheduleProvider>();
        serviceCollection.AddHttpClient<AnimeScheduleApiClient>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Shoko.Plugin.AnimeSchedule/1.0 (+https://github.com/revam/dotnet-shoko-plugin-animeschedule)");
        })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .UseSocketsHttpHandler((handler, _) =>
            {
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);
                handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1);
            });
    }

    /// <inheritdoc/>
    public static void RegisterServices(IApplicationBuilder application, IApplicationPaths applicationPaths)
    {
        var registry = application.ApplicationServices.GetRequiredService<RecurringJobRegistry>();
        registry.Register<AnimeScheduleSweepJob>(interval: TimeSpan.FromMinutes(30), runImmediately: true);
    }
}
