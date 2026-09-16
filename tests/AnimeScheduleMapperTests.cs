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

    [Fact]
    public void GetPlatforms_NoStreams_ReturnsSingleNoPlatformFallback()
    {
        var entries = new[] { new AnimeScheduleTimetableEntry { Streams = new AnimeScheduleStreams() } };

        var platforms = AnimeScheduleMapper.GetPlatforms(entries);

        var platform = Assert.Single(platforms);
        Assert.Equal(AnimeScheduleMapper.NoPlatform, platform);
    }

    [Fact]
    public void GetPlatforms_ReadsEveryNonNullPlatform()
    {
        var entries = new[]
        {
            new AnimeScheduleTimetableEntry
            {
                Streams = new AnimeScheduleStreams
                {
                    Crunchyroll = "https://crunchyroll.example/1",
                    Netflix = "https://netflix.example/1",
                },
            },
        };

        var platforms = AnimeScheduleMapper.GetPlatforms(entries);

        Assert.Equal(2, platforms.Count);
        Assert.Contains(platforms, p => p.Key == "crunchyroll" && p.DisplayName == "Crunchyroll" && p.Url == "https://crunchyroll.example/1");
        Assert.Contains(platforms, p => p.Key == "netflix" && p.DisplayName == "Netflix" && p.Url == "https://netflix.example/1");
    }

    [Fact]
    public void GetPlatforms_AcrossMultipleEntries_KeepsLatestUrlPerPlatform()
    {
        var entries = new[]
        {
            new AnimeScheduleTimetableEntry { Streams = new AnimeScheduleStreams { Crunchyroll = "https://crunchyroll.example/old" } },
            new AnimeScheduleTimetableEntry { Streams = new AnimeScheduleStreams { Crunchyroll = "https://crunchyroll.example/new" } },
        };

        var platforms = AnimeScheduleMapper.GetPlatforms(entries);

        var platform = Assert.Single(platforms);
        Assert.Equal("https://crunchyroll.example/new", platform.Url);
    }

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
    public void ResolveTiming_DelayedWithNewDate_ReportsBothSlots()
    {
        var delayedFrom = new DateTimeOffset(2026, 9, 20, 15, 0, 0, TimeSpan.Zero);
        var delayedUntil = new DateTimeOffset(2026, 9, 27, 15, 0, 0, TimeSpan.Zero);
        var entry = new AnimeScheduleTimetableEntry
        {
            AiringStatus = "delayed-air",
            EpisodeDate = delayedFrom,
            DelayedFrom = delayedFrom,
            DelayedUntil = delayedUntil,
        };

        var (airedAt, originalAiredAt, isDelayed) = AnimeScheduleMapper.ResolveTiming(entry);

        Assert.True(isDelayed);
        Assert.Equal(delayedFrom.UtcDateTime, originalAiredAt);
        Assert.Equal(delayedUntil.UtcDateTime, airedAt);
    }

    [Fact]
    public void ResolveTiming_DelayedWithNoNewDateYet_IsSlotless()
    {
        // AnimeSchedule.net reports delays itself, so an indefinite
        // postponement (no delayedUntil yet) must produce a slotless airing
        // rather than keeping the old, now-wrong, time.
        var delayedFrom = new DateTimeOffset(2026, 9, 20, 15, 0, 0, TimeSpan.Zero);
        var entry = new AnimeScheduleTimetableEntry
        {
            AiringStatus = "delayed-air",
            EpisodeDate = delayedFrom,
            DelayedFrom = delayedFrom,
            DelayedUntil = null,
        };

        var (airedAt, originalAiredAt, isDelayed) = AnimeScheduleMapper.ResolveTiming(entry);

        Assert.True(isDelayed);
        Assert.Equal(delayedFrom.UtcDateTime, originalAiredAt);
        Assert.Null(airedAt);
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
