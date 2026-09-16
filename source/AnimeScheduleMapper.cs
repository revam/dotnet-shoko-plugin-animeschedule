using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Airing;
using Shoko.Plugin.AnimeSchedule.Api;

namespace Shoko.Plugin.AnimeSchedule;

/// <summary>
/// Pure mapping logic from AnimeSchedule.net's API shapes to the airing
/// schedule abstractions. Kept free of I/O and of <see cref="Shoko.Abstractions.Metadata.Services.IAiringScheduleService"/>
/// calls so it can be unit tested directly.
/// </summary>
public static class AnimeScheduleMapper
{
    /// <summary>
    /// Every air type this provider tracks, in the order they are fetched.
    /// </summary>
    public static readonly IReadOnlyList<AnimeScheduleAirType> AllAirTypes =
    [
        AnimeScheduleAirType.Raw,
        AnimeScheduleAirType.Sub,
        AnimeScheduleAirType.Dub,
    ];

    /// <summary>
    /// One entry per streaming platform AnimeSchedule.net tracks: its stable
    /// key (used in a schedule's identity), its display name (used to
    /// register the channel), and how to read its URL off a
    /// <see cref="AnimeScheduleStreams"/> instance.
    /// </summary>
    private static readonly IReadOnlyList<(string Key, string DisplayName, Func<AnimeScheduleStreams, string?> Select)> PlatformSelectors =
    [
        ("crunchyroll", "Crunchyroll", s => s.Crunchyroll),
        ("funimation", "Funimation", s => s.Funimation),
        ("wakanim", "Wakanim", s => s.Wakanim),
        ("amazon", "Amazon", s => s.Amazon),
        ("hidive", "HIDIVE", s => s.Hidive),
        ("hulu", "Hulu", s => s.Hulu),
        ("youtube", "YouTube", s => s.Youtube),
        ("netflix", "Netflix", s => s.Netflix),
    ];

    /// <summary>
    /// One resolved streaming platform for a schedule: its key (for the
    /// schedule's identity, and <c>null</c> for the channel-less fallback),
    /// its display name (for <c>FindOrRegisterChannel</c>), and the URL to
    /// carry on the schedule.
    /// </summary>
    public readonly record struct PlatformLink(string? Key, string? DisplayName, string? Url);

    /// <summary>
    /// The single channel-less platform, used when an anime has no known
    /// stream for an air type at all: still one schedule, just with no
    /// channel and no URL.
    /// </summary>
    public static readonly PlatformLink NoPlatform = new(null, null, null);

    /// <summary>
    /// Maps an air type to the <see cref="AiringKind"/> it declares, per the
    /// plugin's fixed <c>raw → Original</c>, <c>sub → Subtitled</c>,
    /// <c>dub → Dubbed</c> mapping.
    /// </summary>
    public static AiringKind GetKind(AnimeScheduleAirType airType) => airType switch
    {
        AnimeScheduleAirType.Raw => AiringKind.Original,
        AnimeScheduleAirType.Sub => AiringKind.Subtitled,
        AnimeScheduleAirType.Dub => AiringKind.Dubbed,
        _ => throw new ArgumentOutOfRangeException(nameof(airType), airType, null),
    };

    /// <summary>
    /// Maps an air type (and, for <c>raw</c>, whether the anime is a
    /// donghua) to the track's language code: <c>zh</c> for a donghua raw
    /// release, <c>ja</c> for any other raw release, and <c>en</c> for both
    /// sub and dub.
    /// </summary>
    public static string GetLanguageCode(AnimeScheduleAirType airType, bool donghua) => airType switch
    {
        AnimeScheduleAirType.Raw => donghua ? "zh" : "ja",
        AnimeScheduleAirType.Sub => "en",
        AnimeScheduleAirType.Dub => "en",
        _ => throw new ArgumentOutOfRangeException(nameof(airType), airType, null),
    };

    /// <summary>
    /// Builds the single track a schedule for this air type carries.
    /// </summary>
    public static AiringTrackData BuildTrack(AnimeScheduleAirType airType, bool donghua)
        => new(GetKind(airType), GetLanguageCode(airType, donghua));

    /// <summary>
    /// Builds the provider key for a schedule: <c>{airType}:{platform}</c>,
    /// or <c>{airType}:none</c> for the channel-less fallback. Stable across
    /// runs so re-fetching the same air type and platform updates the same
    /// schedule instead of creating a new one.
    /// </summary>
    public static string BuildScheduleKey(AnimeScheduleAirType airType, string? platformKey)
        => $"{airType.ToString().ToLowerInvariant()}:{platformKey ?? "none"}";

    /// <summary>
    /// Resolves every streaming platform referenced by any of the given
    /// entries (typically the current and next week's entries for one air
    /// type), keeping the most recently seen URL per platform. Entries are
    /// expected to be given in chronological order.
    /// </summary>
    /// <returns>
    /// The resolved platforms, or a single <see cref="NoPlatform"/> entry
    /// when none of the entries name a stream.
    /// </returns>
    public static IReadOnlyList<PlatformLink> GetPlatforms(IEnumerable<AnimeScheduleTimetableEntry> entries)
    {
        var materialized = entries as IReadOnlyList<AnimeScheduleTimetableEntry> ?? entries.ToList();
        var result = new List<PlatformLink>();

        foreach (var (key, displayName, select) in PlatformSelectors)
        {
            string? url = null;
            foreach (var entry in materialized)
            {
                var candidate = select(entry.Streams);
                if (!string.IsNullOrWhiteSpace(candidate))
                    url = candidate;
            }

            if (url is not null)
                result.Add(new PlatformLink(key, displayName, url));
        }

        return result.Count > 0 ? result : [NoPlatform];
    }

    /// <summary>
    /// Expands a timetable entry into the episode numbers it covers.
    /// <see cref="AnimeScheduleTimetableEntry.SubtractedEpisodeNumber"/>, when
    /// present and lower than <see cref="AnimeScheduleTimetableEntry.EpisodeNumber"/>,
    /// means the entry is a single release covering that inclusive range
    /// (e.g. a two-episode premiere); such a range should be linked together
    /// after being submitted.
    /// </summary>
    public static IReadOnlyList<int> GetEpisodeNumbers(AnimeScheduleTimetableEntry entry)
    {
        if (entry.SubtractedEpisodeNumber is { } low && low > 0 && low < entry.EpisodeNumber)
            return Enumerable.Range(low, entry.EpisodeNumber - low + 1).ToList();

        return [entry.EpisodeNumber];
    }

    /// <summary>
    /// Whether the entry's immediate timetable status is <c>delayed-air</c>.
    /// </summary>
    public static bool IsDelayed(AnimeScheduleTimetableEntry entry)
        => string.Equals(entry.AiringStatus, "delayed-air", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves an entry's airing time, original time and delay flag. Since
    /// this provider reports delays itself (submitted with
    /// <see cref="EpisodeAiringUpdateOptions.InferDelays"/> off), a delayed
    /// entry with no new date yet becomes a slotless airing rather than being
    /// left at its old time.
    /// </summary>
    public static (DateTime? AiredAt, DateTime? OriginalAiredAt, bool IsDelayed) ResolveTiming(AnimeScheduleTimetableEntry entry)
    {
        if (!IsDelayed(entry))
            return (DateTime.SpecifyKind(entry.EpisodeDate.UtcDateTime, DateTimeKind.Utc), null, false);

        var originalAiredAt = entry.DelayedFrom.HasValue
            ? DateTime.SpecifyKind(entry.DelayedFrom.Value.UtcDateTime, DateTimeKind.Utc)
            : DateTime.SpecifyKind(entry.EpisodeDate.UtcDateTime, DateTimeKind.Utc);
        var airedAt = entry.DelayedUntil.HasValue
            ? DateTime.SpecifyKind(entry.DelayedUntil.Value.UtcDateTime, DateTimeKind.Utc)
            : (DateTime?)null;

        return (airedAt, originalAiredAt, true);
    }

    /// <summary>
    /// Maps one timetable entry, for one of the episodes it covers, to the
    /// airing data submitted to <c>SetAirings</c>.
    /// </summary>
    public static EpisodeAiringData MapAiring(AnimeScheduleTimetableEntry entry, IEpisode episode)
    {
        var (airedAt, originalAiredAt, isDelayed) = ResolveTiming(entry);
        return new EpisodeAiringData
        {
            Episode = episode,
            AiredAt = airedAt,
            OriginalAiredAt = originalAiredAt,
            IsDelayed = isDelayed,
            Key = episode.EpisodeNumber.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// Gets the ISO 8601 week (year, week number) containing the given UTC
    /// date. Pulled out as a pure function so "this week" and "next week" are
    /// both computed the same, testable way.
    /// </summary>
    public static (int Year, int Week) GetIsoWeek(DateTime utcDate)
        => (System.Globalization.ISOWeek.GetYear(utcDate), System.Globalization.ISOWeek.GetWeekOfYear(utcDate));
}
