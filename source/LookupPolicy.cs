using System;
using System.Globalization;
using Shoko.Abstractions.Metadata;
using Shoko.Plugin.AnimeSchedule.Storage;

namespace Shoko.Plugin.AnimeSchedule;

/// <summary>
/// Where an anime stands against the window in which it could be on a
/// timetable, judged from its local AniDB data.
/// </summary>
public enum AiringWindow
{
    /// <summary>
    /// The anime airs now, ended only lately, starts soon, or has no dates
    /// that say otherwise.
    /// </summary>
    Open,

    /// <summary>
    /// The anime ended longer ago than <see cref="LookupPolicy.EndedGrace"/>.
    /// </summary>
    Ended,

    /// <summary>
    /// The anime starts further ahead than <see cref="LookupPolicy.StartLead"/>.
    /// </summary>
    NotStarted,
}

/// <summary>
/// The rules for when an AniDB anime is looked up on AnimeSchedule.net: only
/// while it could be airing, backing off after each miss, and giving up after
/// <see cref="MaxConsecutiveMisses"/> of them.
/// </summary>
public static class LookupPolicy
{
    /// <summary>
    /// How long after its end date an anime is still looked up.
    /// </summary>
    public static readonly TimeSpan EndedGrace = TimeSpan.FromDays(28);

    /// <summary>
    /// How long before its start date an anime is looked up.
    /// </summary>
    public static readonly TimeSpan StartLead = TimeSpan.FromDays(56);

    /// <summary>
    /// How long a known anime goes before it is looked up again.
    /// </summary>
    public static readonly TimeSpan KnownRecheck = TimeSpan.FromDays(7);

    /// <summary>
    /// The longest wait after a miss.
    /// </summary>
    public static readonly TimeSpan MaxMissDelay = TimeSpan.FromDays(30);

    /// <summary>
    /// How many misses in a row give an anime up.
    /// </summary>
    public const int MaxConsecutiveMisses = 8;

    #region Window

    /// <summary>
    /// Where the anime stands against the airing window. An end date only
    /// known to the year or month counts as its last day, and a start date
    /// as its first, so a partial date never closes the window early.
    /// </summary>
    /// <param name="airDate">The AniDB start date, or <c>null</c>.</param>
    /// <param name="endDate">The AniDB end date, or <c>null</c>.</param>
    /// <param name="today">Today, in UTC.</param>
    /// <returns>Where the anime stands.</returns>
    public static AiringWindow GetWindow(PartialDateOnly? airDate, PartialDateOnly? endDate, DateOnly today)
    {
        if (endDate is { } end && GetLastDay(end).AddDays((int)EndedGrace.TotalDays) < today)
            return AiringWindow.Ended;

        if (airDate is { } start && GetFirstDay(start) > today.AddDays((int)StartLead.TotalDays))
            return AiringWindow.NotStarted;

        return AiringWindow.Open;
    }

    /// <summary>
    /// The fingerprint of the AniDB data a lookup decision is made on.
    /// </summary>
    /// <param name="airDate">The AniDB start date, or <c>null</c>.</param>
    /// <param name="endDate">The AniDB end date, or <c>null</c>.</param>
    /// <param name="episodeCount">The AniDB episode count.</param>
    /// <returns>The fingerprint.</returns>
    public static string GetFingerprint(PartialDateOnly? airDate, PartialDateOnly? endDate, int episodeCount)
        => string.Create(CultureInfo.InvariantCulture, $"{airDate}|{endDate}|{episodeCount}");

    /// <summary>
    /// The first day a partial date can mean.
    /// </summary>
    /// <param name="date">The date.</param>
    /// <returns>The day.</returns>
    private static DateOnly GetFirstDay(PartialDateOnly date)
        => new(date.Year, date.Month ?? 1, date.Day ?? 1);

    /// <summary>
    /// The last day a partial date can mean.
    /// </summary>
    /// <param name="date">The date.</param>
    /// <returns>The day.</returns>
    private static DateOnly GetLastDay(PartialDateOnly date)
    {
        var month = date.Month ?? 12;
        return new(date.Year, month, date.Day ?? DateTime.DaysInMonth(date.Year, month));
    }

    #endregion

    #region Backoff

    /// <summary>
    /// Whether the anime is due a lookup.
    /// </summary>
    /// <param name="lookup">The anime's lookup state, or <c>null</c> when it has none.</param>
    /// <param name="now">The current time, in UTC.</param>
    /// <returns><c>true</c> when it is due.</returns>
    public static bool IsDue(AnidbLookup? lookup, DateTime now)
        => lookup is null || lookup.NextCheckAt is { } next && next <= now;

    /// <summary>
    /// How long to wait after the given number of misses in a row: one day
    /// after the first, doubling after each, up to <see cref="MaxMissDelay"/>.
    /// </summary>
    /// <param name="consecutiveMisses">The misses in a row, from one.</param>
    /// <returns>The wait.</returns>
    public static TimeSpan GetMissDelay(int consecutiveMisses)
    {
        var days = Math.Pow(2, Math.Clamp(consecutiveMisses, 1, 16) - 1);
        return TimeSpan.FromDays(Math.Min(days, MaxMissDelay.TotalDays));
    }

    /// <summary>
    /// Records that AnimeSchedule.net knows the anime.
    /// </summary>
    /// <param name="lookup">The anime's lookup state.</param>
    /// <param name="route">AnimeSchedule.net's route for it.</param>
    /// <param name="fingerprint">The fingerprint of its AniDB data.</param>
    /// <param name="now">The current time, in UTC.</param>
    public static void RecordHit(AnidbLookup lookup, string route, string fingerprint, DateTime now)
    {
        lookup.State = LookupState.Known;
        lookup.Route = route;
        lookup.ConsecutiveMisses = 0;
        lookup.LastCheckedAt = now;
        lookup.NextCheckAt = now + KnownRecheck;
        lookup.Fingerprint = fingerprint;
    }

    /// <summary>
    /// Records that AnimeSchedule.net did not know the anime, and gives it up
    /// on the <see cref="MaxConsecutiveMisses"/>th miss in a row.
    /// </summary>
    /// <param name="lookup">The anime's lookup state.</param>
    /// <param name="fingerprint">The fingerprint of its AniDB data.</param>
    /// <param name="now">The current time, in UTC.</param>
    public static void RecordMiss(AnidbLookup lookup, string fingerprint, DateTime now)
    {
        lookup.Route = null;
        lookup.ConsecutiveMisses++;
        lookup.LastCheckedAt = now;
        lookup.Fingerprint = fingerprint;
        if (lookup.ConsecutiveMisses >= MaxConsecutiveMisses)
        {
            GiveUp(lookup);
            return;
        }

        lookup.State = LookupState.Unknown;
        lookup.NextCheckAt = now + GetMissDelay(lookup.ConsecutiveMisses);
    }

    /// <summary>
    /// Gives the anime up: it is not looked up again until its AniDB data
    /// changes.
    /// </summary>
    /// <param name="lookup">The anime's lookup state.</param>
    public static void GiveUp(AnidbLookup lookup)
    {
        lookup.State = LookupState.GivenUp;
        lookup.Route = null;
        lookup.NextCheckAt = null;
    }

    /// <summary>
    /// Clears the backoff when the anime's AniDB data changed since the last
    /// decision. A given-up anime gets one fresh check: one more miss gives it
    /// up again.
    /// </summary>
    /// <param name="lookup">The anime's lookup state.</param>
    /// <param name="fingerprint">The fingerprint of its current AniDB data.</param>
    /// <param name="now">The current time, in UTC.</param>
    /// <returns><c>true</c> when the data had changed and the state was reset.</returns>
    public static bool ResetIfChanged(AnidbLookup lookup, string fingerprint, DateTime now)
    {
        if (string.Equals(lookup.Fingerprint, fingerprint, StringComparison.Ordinal))
            return false;

        if (lookup.State is LookupState.GivenUp)
        {
            lookup.State = LookupState.Unknown;
            lookup.ConsecutiveMisses = MaxConsecutiveMisses - 1;
        }
        else
        {
            lookup.ConsecutiveMisses = 0;
        }

        lookup.Fingerprint = fingerprint;
        lookup.NextCheckAt = now;
        return true;
    }

    #endregion
}
