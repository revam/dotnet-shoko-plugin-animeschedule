using System;
using System.Linq;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Connectivity.Suspensions;

namespace Shoko.Plugin.AnimeSchedule.Api;

/// <summary>
/// Tracks AnimeSchedule.net's <c>X-RateLimit-*</c> response headers and holds
/// requests back once the window is exhausted, instead of guessing a fixed
/// throughput.
/// </summary>
/// <remarks>
/// AnimeSchedule.net enforces 120 requests/minute per application <em>and</em>
/// per IP address independently (see the plugin's <see cref="Configuration"/>
/// remarks), and reports whichever of the two is more restrictive on every
/// response via <c>X-RateLimit-Remaining</c>/<c>X-RateLimit-Reset</c>. Reading
/// those headers back is simpler and more accurate than maintaining a
/// client-side token bucket for each limit separately, and self-corrects if
/// the server's own accounting ever disagrees with ours.
///
/// Before the first response arrives (or if a response omits the headers)
/// <see cref="ComputeDelay"/> lets requests straight through: a single caller
/// starting from nothing cannot known better than the server, and
/// <see cref="Api.AnimeScheduleApiClient"/> only ever has one request in
/// flight at a time (guarded by <see cref="WaitAsync"/>'s semaphore), so a
/// burst before the first response is not possible from this client alone.
///
/// Requests are also spaced <see cref="MinimumInterval"/> apart, about half
/// the rate AnimeSchedule.net allows, and the limiter keeps the suspension
/// the client reports for a rate limit or a run of server errors.
/// </remarks>
/// <param name="reporter">Reports the suspensions to the server, or <c>null</c>.</param>
/// <param name="logger">The logger, or <c>null</c>.</param>
public sealed class AnimeScheduleRateLimiter(
    ISuspensionReporter<AnimeScheduleSuspensionProvider>? reporter = null,
    ILogger<AnimeScheduleRateLimiter>? logger = null
)
{
    /// <summary>
    /// How many server errors in a row suspend the service.
    /// </summary>
    public const int ServerErrorsToSuspend = 3;

    /// <summary>
    /// How long a run of server errors suspends the service.
    /// </summary>
    public static readonly TimeSpan ServerErrorSuspension = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();

    private int? _remaining;
    private DateTimeOffset? _resetAt;
    private DateTimeOffset? _lastRequestAt;
    private DateTimeOffset? _suspendedUntil;
    private int _serverErrors;

    /// <summary>
    /// The least time between two requests. One second, so about 60 requests
    /// a minute against AnimeSchedule.net's 120.
    /// </summary>
    internal TimeSpan MinimumInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// When the current suspension ends, in UTC, or <c>null</c> when requests
    /// may be sent.
    /// </summary>
    public DateTimeOffset? SuspendedUntil
    {
        get
        {
            lock (_stateLock)
                return _suspendedUntil is { } until && until > DateTimeOffset.UtcNow ? until : null;
        }
    }

    /// <summary>
    /// Waits until it is safe to send another request, then reserves a slot
    /// for it. Callers must send exactly one request per call and report the
    /// response back through <see cref="UpdateFromHeaders"/>.
    /// </summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TimeSpan delay;
            lock (_stateLock)
            {
                var now = DateTimeOffset.UtcNow;
                delay = ComputeDelay(_remaining, _resetAt, now);
                if (_lastRequestAt is { } last && last + MinimumInterval - now is { Ticks: > 0 } spacing && spacing > delay)
                    delay = spacing;
            }

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            // Optimistically decrement so a caller that never checks the
            // response headers (e.g. a failed request) still backs off
            // before the next call, rather than hammering the endpoint.
            lock (_stateLock)
            {
                if (_remaining is > 0)
                    _remaining--;
                _lastRequestAt = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Updates the tracked rate limit state from a response's headers. Safe
    /// to call even when the headers are missing or unparsable; the state is
    /// simply left as it was.
    /// </summary>
    public void UpdateFromHeaders(HttpResponseHeaders headers, DateTimeOffset now)
    {
        var (remaining, resetAt) = ParseHeaders(headers, now);
        lock (_stateLock)
        {
            if (remaining.HasValue)
                _remaining = remaining;
            if (resetAt.HasValue)
                _resetAt = resetAt;
        }
    }

    #region Suspensions

    /// <summary>
    /// A request succeeded, which ends a run of server errors.
    /// </summary>
    public void NotifySuccess()
    {
        lock (_stateLock)
            _serverErrors = 0;
    }

    /// <summary>
    /// AnimeSchedule.net answered with a server error. The
    /// <see cref="ServerErrorsToSuspend"/>th in a row suspends the service
    /// for <see cref="ServerErrorSuspension"/>.
    /// </summary>
    /// <returns>When the suspension ends, or <c>null</c> when this error did not suspend.</returns>
    public DateTimeOffset? NotifyServerError()
    {
        lock (_stateLock)
        {
            if (++_serverErrors < ServerErrorsToSuspend)
                return null;

            _serverErrors = 0;
        }

        var until = DateTimeOffset.UtcNow + ServerErrorSuspension;
        Suspend(SuspensionKind.ServerErrors, until);
        return until;
    }

    /// <summary>
    /// AnimeSchedule.net limited the rate again after the one retry, which
    /// suspends the service until the given time.
    /// </summary>
    /// <param name="until">When the limit lifts, in UTC.</param>
    public void NotifyRateLimited(DateTimeOffset until)
        => Suspend(SuspensionKind.RateLimited, until);

    /// <summary>
    /// Keeps the suspension and reports it to the server. The server clears
    /// it once its end passes, so nothing resumes it here.
    /// </summary>
    /// <param name="kind">Why the service is suspended.</param>
    /// <param name="until">When the suspension ends, in UTC.</param>
    private void Suspend(SuspensionKind kind, DateTimeOffset until)
    {
        lock (_stateLock)
        {
            if (_suspendedUntil is not { } current || current < until)
                _suspendedUntil = until;
        }

        logger?.LogInformation(
            "AnimeSchedule.net is suspended ({Kind}) until {ResumesAt:u}; the plugin sends no requests until then.",
            kind,
            until
        );
        if (reporter is null)
            return;

        try
        {
            reporter.Suspend(kind, resumesAt: until.UtcDateTime);
        }
        catch (InvalidOperationException ex)
        {
            logger?.LogWarning(ex, "Could not report the AnimeSchedule.net {Kind} suspension.", kind);
        }
    }

    #endregion

    /// <summary>
    /// Parses AnimeSchedule.net's rate limit headers. Pulled out as a pure,
    /// static function so the parsing logic can be unit tested without a
    /// real HTTP round-trip.
    /// </summary>
    /// <param name="headers">The response headers to read.</param>
    /// <param name="now">
    /// The current time, used only to sanity-check a reset timestamp that has
    /// already elapsed.
    /// </param>
    public static (int? Remaining, DateTimeOffset? ResetAt) ParseHeaders(HttpResponseHeaders headers, DateTimeOffset now)
    {
        int? remaining = null;
        DateTimeOffset? resetAt = null;

        if (headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues)
            && int.TryParse(remainingValues.FirstOrDefault(), out var parsedRemaining))
        {
            remaining = parsedRemaining;
        }

        if (headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
            && long.TryParse(resetValues.FirstOrDefault(), out var resetUnixSeconds))
        {
            resetAt = DateTimeOffset.FromUnixTimeSeconds(resetUnixSeconds);
        }

        return (remaining, resetAt);
    }

    /// <summary>
    /// Computes how long to wait before the next request is safe to send,
    /// from the last known rate limit state. Pure and static so it can be
    /// unit tested without invoking <see cref="Task.Delay(TimeSpan)"/>.
    /// </summary>
    /// <param name="remaining">
    /// The last known remaining request count, or <c>null</c> when no
    /// response has been seen yet.
    /// </param>
    /// <param name="resetAt">
    /// The last known reset time, or <c>null</c> when no response has been
    /// seen yet.
    /// </param>
    /// <param name="now">The current time.</param>
    /// <returns>
    /// A non-negative delay: zero when a request can be sent immediately, or
    /// the time remaining until the window resets (plus a small buffer)
    /// otherwise.
    /// </returns>
    public static TimeSpan ComputeDelay(int? remaining, DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (remaining is null or > 0)
            return TimeSpan.Zero;

        if (resetAt is null || resetAt <= now)
            return TimeSpan.Zero;

        return resetAt.Value - now + TimeSpan.FromMilliseconds(250);
    }
}
