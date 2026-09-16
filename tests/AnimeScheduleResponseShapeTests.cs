using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Shoko.Plugin.AnimeSchedule.Api;
using Xunit;

namespace Shoko.Plugin.AnimeSchedule.Tests;

/// <summary>
/// Deserializes captured AnimeSchedule.net responses and checks every field
/// the models claim, against what the live API actually sends. The models
/// were originally written from the documentation and never exercised, which
/// is how <c>streams</c> came to be modelled as an object with a property per
/// platform when the API returns an array.
/// </summary>
public class AnimeScheduleResponseShapeTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static List<AnimeScheduleTimetableEntry> ReadTimetable(string fileName)
    {
        var entries = JsonSerializer.Deserialize<List<AnimeScheduleTimetableEntry>>(Fixture.Read(fileName), Options);
        Assert.NotNull(entries);
        return entries;
    }

    #region Timetable

    [Fact]
    public void Timetable_RealResponse_DeserializesEveryEntry()
    {
        var entries = ReadTimetable("timetable-sub-2026-w38.json");

        Assert.Equal(5, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrEmpty(entry.Title));
            Assert.False(string.IsNullOrEmpty(entry.Route));
            Assert.False(string.IsNullOrEmpty(entry.AiringStatus));
            Assert.True(entry.EpisodeNumber > 0);
            Assert.True(entry.EpisodeDate.Year > 2000, "episodeDate is never the year-1 sentinel.");
        });
    }

    [Fact]
    public void Timetable_Streams_AreAnArrayOfPlatformObjects()
    {
        var entry = ReadTimetable("timetable-sub-2026-w38.json")
            .Single(e => e.Route == "hokuto-no-ken-kenougun-zako-tachi-no-banka-part-2");

        var stream = Assert.Single(entry.Streams);
        Assert.Equal("youtube", stream.Platform);
        Assert.Equal("YouTube", stream.Name);
        Assert.Equal("www.youtube.com/playlist?list=PLcPnuVJ3jm6A", stream.Url);
    }

    [Fact]
    public void Timetable_Streams_CarryPlatformsBeyondTheDocumentedSet()
    {
        var entry = ReadTimetable("timetable-sub-2026-w38.json")
            .Single(e => e.Route == "toumei-na-yoru-ni-kakeru-kimi-to-me-ni-mienai-koi-wo-shita");

        // apple and bilibili are not in AnimeSchedule.net's documented
        // platform list, but the live API sends them all the same.
        Assert.Equal(["apple", "crunchyroll", "youtube", "bilibili"], entry.Streams.Select(s => s.Platform));
    }

    [Fact]
    public void Timetable_Streams_UrlsNeverCarryAScheme()
    {
        var entries = ReadTimetable("timetable-sub-2026-w38.json");

        var urls = entries.SelectMany(entry => entry.Streams).Select(stream => stream.Url).ToList();
        Assert.NotEmpty(urls);
        Assert.All(urls, url => Assert.DoesNotContain("://", url, StringComparison.Ordinal));
    }

    [Fact]
    public void Timetable_MissingStreams_IsAnEmptyListNotNull()
    {
        var entry = ReadTimetable("timetable-sub-2026-w38.json").Single(e => e.Route == "ganso-bandori-chan");

        Assert.Empty(entry.Streams);
    }

    [Fact]
    public void Timetable_SentinelDelayDates_AreNull()
    {
        var entry = ReadTimetable("timetable-sub-2026-w38.json")
            .Single(e => e.Route == "hokuto-no-ken-kenougun-zako-tachi-no-banka-part-2");

        Assert.Null(entry.DelayedFrom);
        Assert.Null(entry.DelayedUntil);
    }

    [Fact]
    public void Timetable_RealDelayWindow_IsParsed()
    {
        var entry = ReadTimetable("timetable-sub-2026-w38.json")
            .Single(e => e.Route == "bleach-sennen-kessen-hen-kashin-tan");

        Assert.Equal("delayed-air", entry.AiringStatus);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero), entry.DelayedFrom);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero), entry.DelayedUntil);
    }

    [Fact]
    public void Timetable_SubtractedEpisodeNumber_IsParsedWhenPresent()
    {
        var entry = Assert.Single(ReadTimetable("timetable-sub-2026-w01.json"));

        Assert.Equal(2, entry.EpisodeNumber);
        Assert.Equal(1, entry.SubtractedEpisodeNumber);
    }

    [Fact]
    public void Timetable_SubtractedEpisodeNumber_IsNullWhenAbsent()
    {
        var entries = ReadTimetable("timetable-sub-2026-w38.json");

        Assert.All(entries, entry => Assert.Null(entry.SubtractedEpisodeNumber));
    }

    [Fact]
    public void Timetable_Donghua_IsAlwaysPresentAsABoolean()
    {
        var entries = ReadTimetable("timetable-sub-2026-w38.json");

        Assert.All(entries, entry => Assert.False(entry.Donghua));
    }

    #endregion

    #region Anime

    [Fact]
    public void Anime_RealResponse_DeserializesThePageAndItsEntries()
    {
        var page = JsonSerializer.Deserialize<AnimeScheduleAnimePage>(Fixture.Read("anime-anidb-4880.json"), Options);

        Assert.NotNull(page);
        Assert.Equal(1, page.Page);
        Assert.Equal(1, page.TotalAmount);

        var anime = Assert.Single(page.Anime);
        Assert.Equal("Vr3M", anime.Id);
        Assert.Equal("Naruto: Shippuuden", anime.Title);
        Assert.Equal("naruto-shippuuden", anime.Route);
        Assert.Equal(500, anime.Episodes);
        Assert.Equal("Finished", anime.Status);
    }

    #endregion
}
