using System;
using System.Linq;
using Shoko.Abstractions.Metadata.Airing;
using Shoko.Plugin.AnimeSchedule.Api;
using Xunit;

namespace Shoko.Plugin.AnimeSchedule.Tests;

public class AnimeScheduleMapperTests
{
    #region Kind / language / track

    [Theory]
    [InlineData(AnimeScheduleAirType.Raw, AiringKind.Original)]
    [InlineData(AnimeScheduleAirType.Sub, AiringKind.Subtitled)]
    [InlineData(AnimeScheduleAirType.Dub, AiringKind.Dubbed)]
    public void GetKind_MapsAirTypeToAiringKind(AnimeScheduleAirType airType, AiringKind expected)
        => Assert.Equal(expected, AnimeScheduleMapper.GetKind(airType));

    [Fact]
    public void GetLanguageCode_Raw_NonDonghua_IsJapanese()
        => Assert.Equal("ja", AnimeScheduleMapper.GetLanguageCode(AnimeScheduleAirType.Raw, donghua: false));

    [Fact]
    public void GetLanguageCode_Raw_Donghua_IsChinese()
        => Assert.Equal("zh", AnimeScheduleMapper.GetLanguageCode(AnimeScheduleAirType.Raw, donghua: true));

    [Theory]
    [InlineData(AnimeScheduleAirType.Sub)]
    [InlineData(AnimeScheduleAirType.Dub)]
    public void GetLanguageCode_SubOrDub_IsEnglish_RegardlessOfDonghua(AnimeScheduleAirType airType)
    {
        Assert.Equal("en", AnimeScheduleMapper.GetLanguageCode(airType, donghua: false));
        Assert.Equal("en", AnimeScheduleMapper.GetLanguageCode(airType, donghua: true));
    }

    [Fact]
    public void BuildTrack_Raw_Donghua_IsOriginalChinese()
    {
        var track = AnimeScheduleMapper.BuildTrack(AnimeScheduleAirType.Raw, donghua: true);

        Assert.Equal(AiringKind.Original, track.Kind);
        Assert.Equal("zh", track.LanguageCode);
    }

    #endregion

    #region Schedule key

    [Theory]
    [InlineData(AnimeScheduleAirType.Raw, "crunchyroll", "raw:crunchyroll")]
    [InlineData(AnimeScheduleAirType.Sub, "netflix", "sub:netflix")]
    [InlineData(AnimeScheduleAirType.Dub, null, "dub:none")]
    public void BuildScheduleKey_CombinesAirTypeAndPlatform(AnimeScheduleAirType airType, string? platformKey, string expected)
        => Assert.Equal(expected, AnimeScheduleMapper.BuildScheduleKey(airType, platformKey));

    #endregion

    #region Platforms

    private static AnimeScheduleTimetableEntry EntryWith(params (string Platform, string Url, string Name)[] streams)
        => new()
        {
            Streams = streams.Select(s => new AnimeScheduleStream { Platform = s.Platform, Url = s.Url, Name = s.Name }).ToList(),
        };

    [Fact]
    public void GetPlatforms_NoStreams_ReturnsSingleNoPlatformFallback()
    {
        var entries = new[] { EntryWith() };

        var platforms = AnimeScheduleMapper.GetPlatforms(entries);

        var platform = Assert.Single(platforms);
        Assert.Equal(AnimeScheduleMapper.NoPlatform, platform);
    }

    [Fact]
    public void GetPlatforms_ReadsEveryStreamInTheArray()
    {
        var entries = new[]
        {
            EntryWith(
                ("crunchyroll", "www.crunchyroll.com/series/GT00378087/one", "Crunchyroll"),
                ("netflix", "www.netflix.com/title/80000603", "Netflix")
            ),
        };

        var platforms = AnimeScheduleMapper.GetPlatforms(entries);

        Assert.Equal(2, platforms.Count);
        Assert.Contains(platforms, p => p.Key == "crunchyroll" && p.DisplayName == "Crunchyroll" && p.Url == "https://www.crunchyroll.com/series/GT00378087/one");
        Assert.Contains(platforms, p => p.Key == "netflix" && p.DisplayName == "Netflix" && p.Url == "https://www.netflix.com/title/80000603");
    }

    [Fact]
    public void GetPlatforms_UndocumentedPlatform_StillBecomesAChannel()
    {
        // The live API reports apple, bilibili, disney and oceanveil, none of
        // which are in its own documented platform list.
        var entries = new[] { EntryWith(("apple", "apple.co/4hwRC6x", "Apple TV"), ("oceanveil", "oceanveil.example/1", "OceanVeil (Sub)")) };

        var platforms = AnimeScheduleMapper.GetPlatforms(entries);

        Assert.Contains(platforms, p => p.Key == "apple" && p.DisplayName == "Apple TV" && p.Url == "https://apple.co/4hwRC6x");
        Assert.Contains(platforms, p => p.Key == "oceanveil" && p.DisplayName == "OceanVeil");
    }

    [Fact]
    public void GetPlatforms_AcrossMultipleEntries_KeepsLatestUrlPerPlatform()
    {
        var entries = new[]
        {
            EntryWith(("crunchyroll", "crunchyroll.example/old", "Crunchyroll")),
            EntryWith(("crunchyroll", "crunchyroll.example/new", "Crunchyroll")),
        };

        var platforms = AnimeScheduleMapper.GetPlatforms(entries);

        var platform = Assert.Single(platforms);
        Assert.Equal("https://crunchyroll.example/new", platform.Url);
    }

    [Fact]
    public void GetPlatforms_IsOrderedByPlatformKey()
    {
        var entries = new[] { EntryWith(("youtube", "a.example", "YouTube"), ("amazon", "b.example", "Amazon"), ("netflix", "c.example", "Netflix")) };

        var platforms = AnimeScheduleMapper.GetPlatforms(entries);

        Assert.Equal(["amazon", "netflix", "youtube"], platforms.Select(p => p.Key));
    }

    [Fact]
    public void GetPlatforms_StreamWithoutAPlatformOrUrl_IsSkipped()
    {
        var entries = new[] { EntryWith(("", "a.example", "Nameless"), ("netflix", "  ", "Netflix")) };

        var platforms = AnimeScheduleMapper.GetPlatforms(entries);

        Assert.Equal(AnimeScheduleMapper.NoPlatform, Assert.Single(platforms));
    }

    #endregion

    #region Stream URLs

    [Theory]
    [InlineData("www.youtube.com/playlist?list=PLcPnuVJ3jm6A", "https://www.youtube.com/playlist?list=PLcPnuVJ3jm6A")]
    [InlineData("amzn.to/3S8MfTE", "https://amzn.to/3S8MfTE")]
    [InlineData("  apple.co/4hwRC6x  ", "https://apple.co/4hwRC6x")]
    [InlineData("//cdn.example/1", "https://cdn.example/1")]
    [InlineData("https://www.netflix.com/title/1", "https://www.netflix.com/title/1")]
    [InlineData("http://www.netflix.com/title/1", "http://www.netflix.com/title/1")]
    public void NormalizeStreamUrl_FillsInTheMissingScheme(string url, string expected)
        => Assert.Equal(expected, AnimeScheduleMapper.NormalizeStreamUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeStreamUrl_NoUrl_IsNull(string? url)
        => Assert.Null(AnimeScheduleMapper.NormalizeStreamUrl(url));

    [Theory]
    [InlineData("bilibili", "BiliBili TV", "Bilibili TV")]
    [InlineData("bilibili", "Bilibili TV", "Bilibili TV")]
    [InlineData("hulu", "Hulu (Dub)", "Hulu")]
    [InlineData("youtube", "Ani-One", "YouTube")]
    [InlineData("hidive", "HiDive", "HIDIVE")]
    public void GetPlatformDisplayName_KnownPlatform_IgnoresTheApiSpelling(string platformKey, string apiName, string expected)
        => Assert.Equal(expected, AnimeScheduleMapper.GetPlatformDisplayName(platformKey, apiName));

    [Theory]
    [InlineData("newthing", "New Thing (Sub)", "New Thing")]
    [InlineData("newthing", "New Thing", "New Thing")]
    [InlineData("newthing", "", "newthing")]
    [InlineData("newthing", null, "newthing")]
    public void GetPlatformDisplayName_UnknownPlatform_FallsBackToTheApiSpelling(string platformKey, string? apiName, string expected)
        => Assert.Equal(expected, AnimeScheduleMapper.GetPlatformDisplayName(platformKey, apiName));

    #endregion

    #region Episode number ranges

    [Fact]
    public void GetEpisodeNumbers_NoSubtractedNumber_ReturnsSingleEpisode()
    {
        var entry = new AnimeScheduleTimetableEntry { EpisodeNumber = 5, SubtractedEpisodeNumber = null };

        var numbers = AnimeScheduleMapper.GetEpisodeNumbers(entry);

        Assert.Equal([5], numbers);
    }

    [Fact]
    public void GetEpisodeNumbers_SubtractedNumberLowerThanEpisode_ReturnsInclusiveRange()
    {
        // "SubtractedEpisodeNumber - EpisodeNumber" per AnimeSchedule.net's
        // own docs, e.g. a two-episode premiere reported as "1-2".
        var entry = new AnimeScheduleTimetableEntry { EpisodeNumber = 2, SubtractedEpisodeNumber = 1 };

        var numbers = AnimeScheduleMapper.GetEpisodeNumbers(entry);

        Assert.Equal([1, 2], numbers);
    }

    [Fact]
    public void GetEpisodeNumbers_SubtractedNumberEqualOrHigher_IsIgnored()
    {
        var entry = new AnimeScheduleTimetableEntry { EpisodeNumber = 5, SubtractedEpisodeNumber = 5 };

        var numbers = AnimeScheduleMapper.GetEpisodeNumbers(entry);

        Assert.Equal([5], numbers);
    }

    #endregion

    #region Delay / timing

    [Theory]
    [InlineData("delayed-air", true)]
    [InlineData("DELAYED-AIR", true)]
    [InlineData("airing", false)]
    [InlineData("aired", false)]
    [InlineData("unaired", false)]
    [InlineData(null, false)]
    public void IsDelayed_MatchesAiringStatusCaseInsensitively(string? airingStatus, bool expected)
    {
        var entry = new AnimeScheduleTimetableEntry { AiringStatus = airingStatus };

        Assert.Equal(expected, AnimeScheduleMapper.IsDelayed(entry));
    }

    [Fact]
    public void IsDelayed_EntrySlottedBeforeTheDelayWindow_IsNotDelayed()
    {
        // Captured live: bleach-sennen-kessen-hen-kashin-tan, ISO week 37 of
        // 2026. Episode 8 aired on time on the 12th; the series only went on
        // break on the 14th, but AnimeSchedule.net stamps the series-level
        // "delayed-air" status and the same delay window onto every one of
        // its entries, this one included.
        var entry = new AnimeScheduleTimetableEntry
        {
            AiringStatus = "delayed-air",
            EpisodeDate = new DateTimeOffset(2026, 9, 12, 14, 30, 0, TimeSpan.Zero),
            DelayedFrom = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
            DelayedUntil = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero),
        };

        Assert.False(AnimeScheduleMapper.IsDelayed(entry));

        var (airedAt, originalAiredAt, isDelayed) = AnimeScheduleMapper.ResolveTiming(entry);
        Assert.False(isDelayed);
        Assert.Null(originalAiredAt);
        Assert.Equal(new DateTime(2026, 9, 12, 14, 30, 0, DateTimeKind.Utc), airedAt);
    }

    [Fact]
    public void IsDelayed_EntrySlottedAtTheDelayWindow_IsDelayed()
    {
        // The same series' episode 9, in ISO week 38: this is the slot the
        // break actually took out.
        var entry = new AnimeScheduleTimetableEntry
        {
            AiringStatus = "delayed-air",
            EpisodeDate = new DateTimeOffset(2026, 9, 14, 15, 30, 0, TimeSpan.Zero),
            DelayedFrom = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
            DelayedUntil = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero),
        };

        Assert.True(AnimeScheduleMapper.IsDelayed(entry));
    }

    [Fact]
    public void ResolveTiming_NotDelayed_UsesEpisodeDateAsIs()
    {
        var episodeDate = new DateTimeOffset(2026, 9, 20, 15, 0, 0, TimeSpan.Zero);
        var entry = new AnimeScheduleTimetableEntry { AiringStatus = "airing", EpisodeDate = episodeDate };

        var (airedAt, originalAiredAt, isDelayed) = AnimeScheduleMapper.ResolveTiming(entry);

        Assert.False(isDelayed);
        Assert.Null(originalAiredAt);
        Assert.Equal(episodeDate.UtcDateTime, airedAt);
    }

    [Fact]
    public void ResolveTiming_DelayedWithNewDate_KeepsTheSlotsTimeOfDay()
    {
        // The delay window is recorded at day granularity, so the new slot
        // takes its date from delayedUntil and its time from the entry. For
        // bungou-stray-dogs-wan-2 that reproduces 2026-09-17T12:41Z, which is
        // exactly what the API reports once week 38 is queried.
        var entry = new AnimeScheduleTimetableEntry
        {
            AiringStatus = "delayed-air",
            EpisodeDate = new DateTimeOffset(2026, 9, 10, 12, 41, 0, TimeSpan.Zero),
            DelayedFrom = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero),
            DelayedUntil = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero),
        };

        var (airedAt, originalAiredAt, isDelayed) = AnimeScheduleMapper.ResolveTiming(entry);

        Assert.True(isDelayed);
        Assert.Equal(new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc), originalAiredAt);
        Assert.Equal(new DateTime(2026, 9, 17, 12, 41, 0, DateTimeKind.Utc), airedAt);
    }

    [Fact]
    public void ResolveTiming_DelayedWithNoNewDateYet_IsSlotless()
    {
        // AnimeSchedule.net reports delays itself, so an indefinite
        // postponement (no delayedUntil yet) must produce a slotless airing
        // rather than keeping the old, now-wrong, time. Captured live from
        // beyblade-x, on an open-ended break since 2026-03-14.
        var entry = new AnimeScheduleTimetableEntry
        {
            AiringStatus = "delayed-air",
            EpisodeDate = new DateTimeOffset(2026, 9, 19, 22, 45, 0, TimeSpan.Zero),
            DelayedFrom = new DateTimeOffset(2026, 3, 14, 0, 0, 0, TimeSpan.Zero),
            DelayedUntil = null,
        };

        var (airedAt, originalAiredAt, isDelayed) = AnimeScheduleMapper.ResolveTiming(entry);

        Assert.True(isDelayed);
        Assert.Equal(new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc), originalAiredAt);
        Assert.Null(airedAt);
    }

    [Fact]
    public void ResolveTiming_DelayedUntilNotAfterDelayedFrom_IsSlotless()
    {
        var entry = new AnimeScheduleTimetableEntry
        {
            AiringStatus = "delayed-air",
            EpisodeDate = new DateTimeOffset(2026, 9, 20, 8, 30, 0, TimeSpan.Zero),
            DelayedFrom = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero),
            DelayedUntil = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero),
        };

        var (airedAt, _, isDelayed) = AnimeScheduleMapper.ResolveTiming(entry);

        Assert.True(isDelayed);
        Assert.Null(airedAt);
    }

    [Fact]
    public void ResolveTiming_DelayedWithNoWindowAtAll_FallsBackToTheEntrySlot()
    {
        var episodeDate = new DateTimeOffset(2026, 9, 20, 15, 0, 0, TimeSpan.Zero);
        var entry = new AnimeScheduleTimetableEntry { AiringStatus = "delayed-air", EpisodeDate = episodeDate };

        var (airedAt, originalAiredAt, isDelayed) = AnimeScheduleMapper.ResolveTiming(entry);

        Assert.True(isDelayed);
        Assert.Equal(episodeDate.UtcDateTime, originalAiredAt);
        Assert.Null(airedAt);
    }

    #endregion

    #region Episode ownership across weeks

    [Fact]
    public void ResolveEpisodeOwnership_SameEpisodeInTwoWeeks_KeepsTheLaterProjection()
    {
        // A stalled series is reported again in every week it is queried
        // for, at that week's slot, so fetching the current and the next
        // week hands back the same episode number twice. Submitting both
        // would be rejected as two airings sharing a key.
        var earlier = new AnimeScheduleTimetableEntry { EpisodeNumber = 1, EpisodeDate = new DateTimeOffset(2026, 9, 13, 8, 30, 0, TimeSpan.Zero) };
        var later = new AnimeScheduleTimetableEntry { EpisodeNumber = 1, EpisodeDate = new DateTimeOffset(2026, 9, 20, 8, 30, 0, TimeSpan.Zero) };

        var owned = AnimeScheduleMapper.ResolveEpisodeOwnership([earlier, later]);

        var (entry, numbers) = Assert.Single(owned);
        Assert.Same(later, entry);
        Assert.Equal([1], numbers);
    }

    [Fact]
    public void ResolveEpisodeOwnership_DistinctEpisodes_AreAllKeptInChronologicalOrder()
    {
        var second = new AnimeScheduleTimetableEntry { EpisodeNumber = 12, EpisodeDate = new DateTimeOffset(2026, 9, 20, 8, 30, 0, TimeSpan.Zero) };
        var first = new AnimeScheduleTimetableEntry { EpisodeNumber = 11, EpisodeDate = new DateTimeOffset(2026, 9, 13, 8, 30, 0, TimeSpan.Zero) };

        var owned = AnimeScheduleMapper.ResolveEpisodeOwnership([second, first]);

        Assert.Equal([11, 12], owned.Select(o => o.Entry.EpisodeNumber));
        Assert.Equal([first, second], owned.Select(o => o.Entry));
    }

    [Fact]
    public void ResolveEpisodeOwnership_LaterEntryTakesPartOfARange_LeavesTheRestWithTheEarlierOne()
    {
        var premiere = new AnimeScheduleTimetableEntry
        {
            EpisodeNumber = 2,
            SubtractedEpisodeNumber = 1,
            EpisodeDate = new DateTimeOffset(2026, 1, 4, 15, 0, 0, TimeSpan.Zero),
        };
        var rerun = new AnimeScheduleTimetableEntry { EpisodeNumber = 2, EpisodeDate = new DateTimeOffset(2026, 1, 11, 15, 0, 0, TimeSpan.Zero) };

        var owned = AnimeScheduleMapper.ResolveEpisodeOwnership([premiere, rerun]);

        Assert.Equal(2, owned.Count);
        Assert.Equal([1], owned[0].EpisodeNumbers);
        Assert.Same(premiere, owned[0].Entry);
        Assert.Equal([2], owned[1].EpisodeNumbers);
        Assert.Same(rerun, owned[1].Entry);
    }

    #endregion

    #region ISO week

    [Fact]
    public void GetIsoWeek_KnownDate_MatchesExpectedIsoWeek()
    {
        // 2026-09-16 (a Wednesday) is ISO week 38 of 2026.
        var (year, week) = AnimeScheduleMapper.GetIsoWeek(new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2026, year);
        Assert.Equal(38, week);
    }

    [Fact]
    public void GetIsoWeek_YearBoundary_RollsOverCorrectly()
    {
        // 2026-01-01 is a Thursday, which ISO 8601 puts in week 1 of 2026.
        var (year, week) = AnimeScheduleMapper.GetIsoWeek(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2026, year);
        Assert.Equal(1, week);
    }

    #endregion
}
