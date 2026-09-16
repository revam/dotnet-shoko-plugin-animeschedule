using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shoko.Plugin.AnimeSchedule.Api;

/// <summary>
/// The <c>airType</c> path segment of the <c>/timetables/{airType}</c>
/// endpoint. Each value is fetched as its own request rather than "all", so a
/// single timetable entry's origin is always known from which call produced
/// it.
/// </summary>
public enum AnimeScheduleAirType
{
    /// <summary>
    /// The original-language broadcast or release.
    /// </summary>
    Raw,

    /// <summary>
    /// An English-subtitled release.
    /// </summary>
    Sub,

    /// <summary>
    /// An English-dubbed release.
    /// </summary>
    Dub,
}

/// <summary>
/// A page of the <c>/anime</c> search response.
/// </summary>
public sealed class AnimeScheduleAnimePage
{
    /// <summary>
    /// The current page number.
    /// </summary>
    public int Page { get; set; }

    /// <summary>
    /// The total number of matching anime, across all pages.
    /// </summary>
    public int TotalAmount { get; set; }

    /// <summary>
    /// The anime on this page.
    /// </summary>
    public List<AnimeScheduleAnimeInfo> Anime { get; set; } = [];
}

/// <summary>
/// The subset of the AnimeSchedule.net anime object this plugin needs: enough
/// to join a timetable entry back to the anime by <see cref="Route"/>, and to
/// set a schedule's coverage and finished state.
/// </summary>
public sealed class AnimeScheduleAnimeInfo
{
    /// <summary>
    /// AnimeSchedule.net's own unique ID for the anime.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// The display title.
    /// </summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// The unique URL slug. Timetable entries are joined to an anime by this
    /// value.
    /// </summary>
    public string Route { get; set; } = "";

    /// <summary>
    /// The total number of episodes, or <c>null</c>/<c>0</c> when unknown.
    /// </summary>
    public int? Episodes { get; set; }

    /// <summary>
    /// One of <c>Upcoming</c>, <c>Ongoing</c>, <c>Delayed</c> or
    /// <c>Finished</c>.
    /// </summary>
    public string Status { get; set; } = "";
}

/// <summary>
/// One entry of a <c>/timetables/{airType}</c> response: an anime with an
/// episode airing (or having aired) in the requested week.
/// </summary>
public sealed class AnimeScheduleTimetableEntry
{
    /// <summary>
    /// The display title.
    /// </summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// The unique URL slug, joined back to the anime resolved from
    /// <c>/anime?anidb-ids=</c>.
    /// </summary>
    public string Route { get; set; } = "";

    /// <summary>
    /// The date from which the episode has been delayed, i.e. its original
    /// slot. <c>null</c> when the episode is not currently delayed.
    /// </summary>
    [JsonConverter(typeof(AnimeScheduleSentinelDateTimeOffsetConverter))]
    public DateTimeOffset? DelayedFrom { get; set; }

    /// <summary>
    /// The date until which the episode has been delayed, i.e. its new slot,
    /// when one is already known. <c>null</c> when the episode is not
    /// currently delayed, or is delayed with no new date announced yet.
    /// </summary>
    [JsonConverter(typeof(AnimeScheduleSentinelDateTimeOffsetConverter))]
    public DateTimeOffset? DelayedUntil { get; set; }

    /// <summary>
    /// The episode's immediate timetable status: <c>airing</c>, <c>aired</c>,
    /// <c>unaired</c> or <c>delayed-air</c>. Read as a raw string rather than
    /// an enum because the API spells the delayed value with a hyphen.
    /// </summary>
    public string? AiringStatus { get; set; }

    /// <summary>
    /// The episode's scheduled date and time.
    /// </summary>
    public DateTimeOffset EpisodeDate { get; set; }

    /// <summary>
    /// The episode's number.
    /// </summary>
    public int EpisodeNumber { get; set; }

    /// <summary>
    /// The lowest episode number, when this entry covers a run of several
    /// episodes released together (e.g. a two-episode premiere). The full
    /// range is <see cref="SubtractedEpisodeNumber"/> to
    /// <see cref="EpisodeNumber"/>, inclusive.
    /// </summary>
    public int? SubtractedEpisodeNumber { get; set; }

    /// <summary>
    /// Whether this timetable anime is a donghua (Chinese animation) rather
    /// than a Japanese one. Only meaningful for <see cref="AnimeScheduleAirType.Raw"/>
    /// entries, where it decides the original-language code.
    /// </summary>
    public bool Donghua { get; set; }

    /// <summary>
    /// The streaming platforms this episode is (or will be) available on.
    /// Absent from the response entirely when there are none, so an entry
    /// that names no stream deserializes to an empty list rather than
    /// <c>null</c>.
    /// </summary>
    public List<AnimeScheduleStream> Streams
    {
        get => _streams;
        set => _streams = value ?? [];
    }

    private List<AnimeScheduleStream> _streams = [];
}

/// <summary>
/// One streaming platform an anime or episode is available on.
/// </summary>
/// <remarks>
/// AnimeSchedule.net returns these as an array, not as an object with a
/// property per platform, and the set of platforms is open-ended: alongside
/// the ones its documentation names it also reports <c>apple</c>,
/// <c>bilibili</c>, <c>disney</c> and <c>oceanveil</c>.
/// </remarks>
public sealed class AnimeScheduleStream
{
    /// <summary>
    /// The platform's stable lower-case key, e.g. <c>crunchyroll</c> or
    /// <c>youtube</c>. Unlike <see cref="Name"/> this is spelled the same way
    /// on every entry, so it is what a schedule is keyed and channelled by.
    /// </summary>
    public string Platform { get; set; } = "";

    /// <summary>
    /// The link to the anime or episode on that platform. The API sends this
    /// without a scheme (<c>www.youtube.com/...</c>), so it has to be
    /// absolutised before being handed on as a URL; see
    /// <see cref="Shoko.Plugin.AnimeSchedule.AnimeScheduleMapper.NormalizeStreamUrl"/>.
    /// </summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// The platform's display name as the API spells it on this particular
    /// entry. Not stable: the same platform is variously named
    /// <c>BiliBili TV</c> and <c>Bilibili TV</c>, and an air type-specific
    /// entry carries a <c>(Sub)</c> or <c>(Dub)</c> suffix, so this is only a
    /// fallback for a platform key the plugin does not know yet.
    /// </summary>
    public string Name { get; set; } = "";
}

/// <summary>
/// AnimeSchedule.net represents "no delay" datetimes as the literal string
/// <c>0001-01-01T00:00:00Z</c> rather than JSON <c>null</c>. This converter
/// treats both spellings of "absent" the same way.
/// </summary>
public sealed class AnimeScheduleSentinelDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    private static readonly DateTimeOffset SentinelValue = new(1, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc/>
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        var raw = reader.GetString();
        if (string.IsNullOrEmpty(raw))
            return null;

        if (!DateTimeOffset.TryParse(raw, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var value))
            return null;

        return value == SentinelValue ? null : value;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is { } dto)
            writer.WriteStringValue(dto);
        else
            writer.WriteNullValue();
    }
}
