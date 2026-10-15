using System;
using System.Linq;
using Shoko.Abstractions.Metadata;
using Shoko.Plugin.AnimeSchedule.Storage;
using Xunit;

namespace Shoko.Plugin.AnimeSchedule.Tests;

/// <summary>
/// The rules deciding when an AniDB anime is looked up: the airing window,
/// the backoff after misses, giving up, and the reset when AniDB's data moves.
/// </summary>
public class LookupPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

    #region Window

    [Theory]
    [InlineData(null, null, AiringWindow.Open)]
    [InlineData(-30, null, AiringWindow.Open)]
    [InlineData(-120, -14, AiringWindow.Open)]
    [InlineData(-120, -60, AiringWindow.Ended)]
    [InlineData(30, null, AiringWindow.Open)]
    [InlineData(120, null, AiringWindow.NotStarted)]
    public void The_window_is_open_while_an_anime_airs_ended_lately_or_starts_soon(int? startInDays, int? endInDays, AiringWindow expected)
    {
        var airDate = startInDays is { } start ? new PartialDateOnly(Today.AddDays(start)) : (PartialDateOnly?)null;
        var endDate = endInDays is { } end ? new PartialDateOnly(Today.AddDays(end)) : (PartialDateOnly?)null;

        Assert.Equal(expected, LookupPolicy.GetWindow(airDate, endDate, Today));
    }

    [Fact]
    public void An_end_date_known_only_to_the_year_counts_as_its_last_day()
        => Assert.Equal(AiringWindow.Open, LookupPolicy.GetWindow(null, new PartialDateOnly(Today.Year), Today));

    #endregion

    #region Backoff

    [Fact]
    public void Each_miss_doubles_the_wait_up_to_a_cap()
    {
        var delays = Enumerable.Range(1, 7).Select(misses => LookupPolicy.GetMissDelay(misses).TotalDays).ToList();

        Assert.Equal([1, 2, 4, 8, 16, LookupPolicy.MaxMissDelay.TotalDays, LookupPolicy.MaxMissDelay.TotalDays], delays);
    }

    [Fact]
    public void Misses_in_a_row_give_the_anime_up()
    {
        var lookup = new AnidbLookup { AnidbAnimeID = 1 };

        for (var i = 1; i < LookupPolicy.MaxConsecutiveMisses; i++)
        {
            LookupPolicy.RecordMiss(lookup, "data", Now);
            Assert.Equal(LookupState.Unknown, lookup.State);
            Assert.Equal(Now + LookupPolicy.GetMissDelay(i), lookup.NextCheckAt);
        }

        LookupPolicy.RecordMiss(lookup, "data", Now);

        Assert.Equal(LookupState.GivenUp, lookup.State);
        Assert.Null(lookup.NextCheckAt);
        Assert.False(LookupPolicy.IsDue(lookup, DateTime.MaxValue));
    }

    [Fact]
    public void A_hit_clears_the_misses_and_is_rechecked_after_a_week()
    {
        var lookup = new AnidbLookup { AnidbAnimeID = 1, ConsecutiveMisses = 3 };

        LookupPolicy.RecordHit(lookup, "route", "data", Now);

        Assert.Equal(LookupState.Known, lookup.State);
        Assert.Equal(0, lookup.ConsecutiveMisses);
        Assert.False(LookupPolicy.IsDue(lookup, Now.AddDays(6)));
        Assert.True(LookupPolicy.IsDue(lookup, Now + LookupPolicy.KnownRecheck));
    }

    [Fact]
    public void Changed_anidb_data_clears_the_backoff()
    {
        var lookup = new AnidbLookup { AnidbAnimeID = 1 };
        for (var i = 0; i < 4; i++)
            LookupPolicy.RecordMiss(lookup, "old", Now);

        Assert.False(LookupPolicy.ResetIfChanged(lookup, "old", Now));
        Assert.True(LookupPolicy.ResetIfChanged(lookup, "new", Now));

        Assert.Equal(0, lookup.ConsecutiveMisses);
        Assert.True(LookupPolicy.IsDue(lookup, Now));
    }

    [Fact]
    public void A_given_up_anime_gets_one_fresh_check_when_its_data_changes()
    {
        var lookup = new AnidbLookup { AnidbAnimeID = 1 };
        for (var i = 0; i < LookupPolicy.MaxConsecutiveMisses; i++)
            LookupPolicy.RecordMiss(lookup, "old", Now);

        LookupPolicy.ResetIfChanged(lookup, "new", Now);
        Assert.True(LookupPolicy.IsDue(lookup, Now));
        LookupPolicy.RecordMiss(lookup, "new", Now);

        Assert.Equal(LookupState.GivenUp, lookup.State);
    }

    [Fact]
    public void The_fingerprint_follows_every_field_it_is_made_of()
    {
        var start = new PartialDateOnly(2026, 10, 1);
        var end = new PartialDateOnly(2026, 12, 20);
        var fingerprint = LookupPolicy.GetFingerprint(start, end, 12);

        Assert.NotEqual(fingerprint, LookupPolicy.GetFingerprint(start, null, 12));
        Assert.NotEqual(fingerprint, LookupPolicy.GetFingerprint(null, end, 12));
        Assert.NotEqual(fingerprint, LookupPolicy.GetFingerprint(start, end, 13));
    }

    #endregion
}
