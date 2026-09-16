using System.Threading.Tasks;
using Shoko.QueueProcessor.Abstractions;
using Shoko.QueueProcessor.Acquisition.Attributes;
using Shoko.QueueProcessor.Concurrency;

namespace Shoko.Plugin.AnimeSchedule.Jobs;

/// <summary>
/// Recurring sweep that refreshes every Shoko-known series' AnimeSchedule.net
/// airing schedules. Registered via <c>RecurringJobRegistry</c> in
/// <see cref="Plugin.RegisterServices(Microsoft.AspNetCore.Builder.IApplicationBuilder, Shoko.Abstractions.Plugin.IApplicationPaths)"/>.
/// </summary>
[DatabaseRequired]
[NetworkRequired]
[DisallowConcurrentExecution]
public sealed class AnimeScheduleSweepJob(AnimeScheduleProvider provider) : IQueueJob
{
    /// <inheritdoc/>
    public string TypeName => "AnimeSchedule.net Sweep";

    /// <inheritdoc/>
    public string Title => "Syncing airing schedules from AnimeSchedule.net...";

    /// <inheritdoc/>
    public Task Process() => provider.SweepAsync();
}
