using System;
using System.Linq;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

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
/// </remarks>
public sealed class AnimeScheduleRateLimiter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();

    private int? _remaining;
    private DateTimeOffset? _resetAt;

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
                delay = ComputeDelay(_remaining, _resetAt, DateTimeOffset.UtcNow);

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            // Optimistically decrement so a caller that never checks the
            // response headers (e.g. a failed request) still backs off
            // before the next call, rather than hammering the endpoint.
            lock (_stateLock)
            {
                if (_remaining is > 0)
                    _remaining--;
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
