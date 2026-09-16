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
    /// Canonical display names for the streaming platforms AnimeSchedule.net
    /// is known to report, keyed by the stable <c>platform</c> key it sends.
    /// </summary>
    /// <remarks>
    /// The API's own <c>name</c> is not stable enough to register a channel
    /// by — the same platform arrives as both <c>BiliBili TV</c> and
    /// <c>Bilibili TV</c>, and air type-specific entries append <c>(Sub)</c>
    /// or <c>(Dub)</c> — so a known platform is always named from this table
    /// and only an unknown one falls back to the API's spelling.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> KnownPlatformNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["amazon"] = "Amazon",
        ["apple"] = "Apple TV",
        ["bilibili"] = "Bilibili TV",
        ["crunchyroll"] = "Crunchyroll",
        ["disney"] = "Disney+",
        ["funimation"] = "Funimation",
        ["hidive"] = "HIDIVE",
        ["hulu"] = "Hulu",
        ["netflix"] = "Netflix",
        ["oceanveil"] = "OceanVeil",
        ["wakanim"] = "Wakanim",
        ["youtube"] = "YouTube",
    };

    /// <summary>
    /// The air type suffixes AnimeSchedule.net appends to a platform's
    /// display name when it tracks that platform per air type.
    /// </summary>
    private static readonly string[] AirTypeNameSuffixes = [" (Sub)", " (Dub)", " (Raw)"];

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
    /// Turns a stream URL as AnimeSchedule.net sends it into an absolute one.
    /// Every URL the API returns is scheme-less (<c>www.youtube.com/...</c>,
    /// <c>amzn.to/...</c>), which no client would follow as-is, so a missing
    /// scheme is filled in as <c>https</c>.
    /// </summary>
    /// <returns>The absolute URL, or <c>null</c> when there is no URL.</returns>
    public static string? NormalizeStreamUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmed = url.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return $"https:{trimmed}";

        return trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : $"https://{trimmed}";
    }

    /// <summary>
    /// Resolves the display name to register a platform's channel under:
    /// this plugin's own spelling for a platform it knows, and otherwise the
    /// API's name with any air type suffix stripped, so a platform added to
    /// AnimeSchedule.net after this table was written still gets a channel.
    /// </summary>
    public static string GetPlatformDisplayName(string platformKey, string? apiName)
    {
        if (KnownPlatformNames.TryGetValue(platformKey, out var known))
            return known;

        var name = apiName?.Trim() ?? "";
        foreach (var suffix in AirTypeNameSuffixes)
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length].TrimEnd();
                break;
            }

        return string.IsNullOrEmpty(name) ? platformKey : name;
    }

    /// <summary>
    /// Resolves every streaming platform referenced by any of the given
    /// entries (typically the current and next week's entries for one air
    /// type), keeping the most recently seen URL per platform. Entries are
    /// expected to be given in chronological order.
    /// </summary>
    /// <returns>
    /// The resolved platforms ordered by key, or a single
    /// <see cref="NoPlatform"/> entry when none of the entries name a stream.
    /// </returns>
    public static IReadOnlyList<PlatformLink> GetPlatforms(IEnumerable<AnimeScheduleTimetableEntry> entries)
    {
        var byKey = new Dictionary<string, PlatformLink>(StringComparer.Ordinal);
        foreach (var entry in entries)
            foreach (var stream in entry.Streams)
            {
                if (string.IsNullOrWhiteSpace(stream.Platform))
                    continue;

                if (NormalizeStreamUrl(stream.Url) is not { } url)
                    continue;

                var key = stream.Platform.Trim().ToLowerInvariant();
                byKey[key] = new PlatformLink(key, GetPlatformDisplayName(key, stream.Name), url);
            }

        if (byKey.Count == 0)
            return [NoPlatform];

        // Ordered so the schedules for one air type are always created in the
        // same order, whatever order the API listed the platforms in.
        return byKey.Values.OrderBy(platform => platform.Key, StringComparer.Ordinal).ToList();
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
    /// Whether this entry's own slot was postponed.
    /// </summary>
    /// <remarks>
    /// <c>airingStatus</c> is a series-level state: once a series is delayed
    /// or on break, <em>every</em> entry AnimeSchedule.net returns for it
    /// reads <c>delayed-air</c>, including the weeks that aired on time long
    /// before the delay began, and every one of them carries the same
    /// <c>delayedFrom</c>/<c>delayedUntil</c> window. An entry slotted before
    /// that window opened aired on time whatever the series-level status
    /// says, so the window's start is what narrows the series' state down to
    /// the episodes it actually applies to.
    /// </remarks>
    public static bool IsDelayed(AnimeScheduleTimetableEntry entry)
    {
        if (!string.Equals(entry.AiringStatus, "delayed-air", StringComparison.OrdinalIgnoreCase))
            return false;

        return entry.DelayedFrom is not { } delayedFrom || entry.EpisodeDate >= delayedFrom;
    }

    /// <summary>
    /// Resolves an entry's airing time, original time and delay flag. Since
    /// this provider reports delays itself (submitted with
    /// <see cref="EpisodeAiringUpdateOptions.InferDelays"/> off), a delayed
    /// entry with no new date yet becomes a slotless airing rather than being
    /// left at its old time.
    /// </summary>
    public static (DateTime? AiredAt, DateTime? OriginalAiredAt, bool IsDelayed) ResolveTiming(AnimeScheduleTimetableEntry entry)
    {
        var episodeDate = DateTime.SpecifyKind(entry.EpisodeDate.UtcDateTime, DateTimeKind.Utc);
        if (!IsDelayed(entry))
            return (episodeDate, null, false);

        var originalAiredAt = entry.DelayedFrom is { } delayedFrom
            ? DateTime.SpecifyKind(delayedFrom.UtcDateTime, DateTimeKind.Utc)
            : episodeDate;

        return (ResolveDelayedSlot(entry, originalAiredAt), originalAiredAt, true);
    }

    /// <summary>
    /// Resolves when a delayed entry is expected back, or <c>null</c> when
    /// the postponement is still indefinite.
    /// </summary>
    private static DateTime? ResolveDelayedSlot(AnimeScheduleTimetableEntry entry, DateTime originalAiredAt)
    {
        if (entry.DelayedUntil is not { } delayedUntil)
            return null;

        var resumesAt = DateTime.SpecifyKind(delayedUntil.UtcDateTime, DateTimeKind.Utc);
        if (resumesAt <= originalAiredAt)
            return null;

        // The delay window is recorded at day granularity — both ends are
        // always midnight UTC — while the entry carries the slot's real time
        // of day. Putting the two back together reproduces exactly the time
        // the API itself reports for the episode once the week it resumes in
        // is queried.
        return resumesAt.Date + entry.EpisodeDate.UtcDateTime.TimeOfDay;
    }

    /// <summary>
    /// Decides which entry owns each episode number across a set of entries.
    /// </summary>
    /// <remarks>
    /// The timetable is a projection, not a log: a stalled series' next
    /// episode is reported again in every week it is queried for, at that
    /// week's slot, so fetching the current and the next week can hand back
    /// the same episode number twice. The later projection is the current
    /// one, and submitting both would be rejected outright as two airings
    /// sharing a key.
    /// </remarks>
    /// <returns>
    /// Each entry that still owns at least one episode number, in
    /// chronological order, paired with the numbers it owns.
    /// </returns>
    public static IReadOnlyList<(AnimeScheduleTimetableEntry Entry, IReadOnlyList<int> EpisodeNumbers)> ResolveEpisodeOwnership(
        IEnumerable<AnimeScheduleTimetableEntry> entries
    )
    {
        var ordered = entries.OrderBy(entry => entry.EpisodeDate).ToList();
        var ownerByNumber = new Dictionary<int, AnimeScheduleTimetableEntry>();
        foreach (var entry in ordered)
            foreach (var number in GetEpisodeNumbers(entry))
                ownerByNumber[number] = entry;

        var result = new List<(AnimeScheduleTimetableEntry, IReadOnlyList<int>)>();
        foreach (var entry in ordered)
        {
            var owned = GetEpisodeNumbers(entry)
                .Where(number => ReferenceEquals(ownerByNumber.GetValueOrDefault(number), entry))
                .ToList();
            if (owned.Count > 0)
                result.Add((entry, owned));
        }

        return result;
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
