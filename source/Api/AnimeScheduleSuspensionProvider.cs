using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shoko.Abstractions.Connectivity.Suspensions;

namespace Shoko.Plugin.AnimeSchedule.Api;

/// <summary>
/// The plugin's requests to AnimeSchedule.net, suspended while it limits the
/// rate or answers with server errors. The rate limiter reports through it.
/// </summary>
/// <remarks>
/// The server holds back only provider jobs of metadata and release
/// providers, and an airing sweep is neither, so the plugin also checks the
/// suspension itself before it sweeps or refreshes.
/// </remarks>
public sealed class AnimeScheduleSuspensionProvider : ISuspensionProvider
{
    /// <inheritdoc/>
    public string Name => "AnimeSchedule.net";

    /// <inheritdoc/>
    public string? Description => "Requests to AnimeSchedule.net, held back while it limits the rate or answers with server errors.";

    /// <inheritdoc/>
    public IReadOnlyList<Type> HeldProviderTypes => [typeof(AnimeScheduleProvider)];

    /// <summary>
    /// Never called: none of the plugin's suspensions can be lifted.
    /// </summary>
    /// <param name="kind">The kind of the suspension.</param>
    /// <param name="token">Cancels the lift.</param>
    /// <returns>A completed task.</returns>
    public Task Lift(SuspensionKind kind, CancellationToken token)
        => Task.CompletedTask;
}
