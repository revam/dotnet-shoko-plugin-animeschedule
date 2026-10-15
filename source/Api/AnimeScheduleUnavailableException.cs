using System;

namespace Shoko.Plugin.AnimeSchedule.Api;

/// <summary>
/// AnimeSchedule.net is suspended: it limited the rate past one retry, or
/// answered with a run of server errors. Ends the work in hand rather than
/// one request.
/// </summary>
/// <param name="resumesAt">When requests may be sent again, in UTC.</param>
public sealed class AnimeScheduleUnavailableException(DateTimeOffset resumesAt)
    : Exception($"AnimeSchedule.net is suspended until {resumesAt:u}.")
{
    /// <summary>
    /// When requests may be sent again, in UTC.
    /// </summary>
    public DateTimeOffset ResumesAt { get; } = resumesAt;
}
