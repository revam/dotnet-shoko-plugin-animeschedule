using System;
using System.Collections.Generic;
using Shoko.Plugin.AnimeSchedule.Api;
using Shoko.Plugin.AnimeSchedule.Storage;
using Xunit;

namespace Shoko.Plugin.AnimeSchedule.Tests;

/// <summary>
/// Reading other sites' IDs out of AnimeSchedule.net's links, and matching a
/// library series against the catalog by them.
/// </summary>
public class CatalogTests
{
    #region Links

    [Theory]
    [InlineData("anidb.net/anime/69", 69)]
    [InlineData("https://anidb.net/anime/69", 69)]
    [InlineData("anilist.co/anime/1735/Naruto-Shippuuden/", 1735)]
    [InlineData("myanimelist.net/anime/21/One_Piece", 21)]
    [InlineData("anidb.net/perl-bin/animedb.pl?show=anime&aid=4880", 4880)]
    [InlineData("kitsu.io/anime/naruto-shippuden", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void An_anime_link_gives_its_numeric_id(string? link, int? expected)
        => Assert.Equal(expected, AnimeScheduleMapper.ParseAnimeLinkID(link));

    [Fact]
    public void A_catalog_entry_takes_the_ids_of_its_links()
    {
        var anime = new AnimeScheduleAnimeInfo
        {
            Route = "one-piece",
            Websites = new AnimeScheduleWebsites
            {
                Anidb = "anidb.net/anime/69",
                AniList = "anilist.co/anime/21/One-Piece",
                Mal = "",
            },
        };

        var entry = AnimeScheduleMapper.ToCatalogEntry(anime, DateTime.UtcNow);

        Assert.Equal(69, entry.AnidbID);
        Assert.Equal(21, entry.AnilistID);
        Assert.Null(entry.MalID);
    }

    [Theory]
    [InlineData(AnimeScheduleAirType.Raw)]
    [InlineData(AnimeScheduleAirType.Dub)]
    public void A_schedule_key_gives_back_its_air_type(AnimeScheduleAirType airType)
        => Assert.Equal(airType, AnimeScheduleMapper.GetAirTypeFromScheduleKey(AnimeScheduleMapper.BuildScheduleKey(airType, "crunchyroll")));

    #endregion

    #region Matching

    [Fact]
    public void The_anidb_id_wins_and_the_anilist_ids_are_not_read()
    {
        var index = new CatalogIndex([Entry("by-anidb", anidbId: 1), Entry("by-anilist", anilistId: 10)]);

        var match = index.Match(1, () => throw new InvalidOperationException("Read the AniList links."));

        Assert.Equal("by-anidb", match?.Route);
    }

    [Fact]
    public void The_anilist_id_comes_before_the_mal_id_of_the_anilist_entry()
    {
        var index = new CatalogIndex([Entry("by-anilist", anilistId: 10), Entry("by-mal", malId: 20)]);

        var match = index.Match(1, () => [10], () => [20]);

        Assert.Equal("by-anilist", match?.Route);
    }

    [Fact]
    public void The_mal_id_of_the_anilist_entry_is_the_last_resort()
    {
        var index = new CatalogIndex([Entry("by-mal", malId: 20)]);

        Assert.Equal("by-mal", index.Match(1, () => [10], () => [20])?.Route);
        Assert.Null(index.Match(1, () => [10]));
    }

    [Fact]
    public void Of_two_entries_sharing_an_id_the_one_seen_last_wins()
    {
        var index = new CatalogIndex(
        [
            Entry("new", anidbId: 1, seenAt: DateTime.UtcNow),
            Entry("old", anidbId: 1, seenAt: DateTime.UtcNow.AddDays(-3)),
        ]);

        Assert.Equal("new", index.Match(1, () => [])?.Route);
    }

    /// <summary>
    /// A catalog entry with the given IDs.
    /// </summary>
    /// <param name="route">The route.</param>
    /// <param name="anidbId">The AniDB anime ID, or <c>null</c>.</param>
    /// <param name="anilistId">The AniList anime ID, or <c>null</c>.</param>
    /// <param name="malId">The MyAnimeList anime ID, or <c>null</c>.</param>
    /// <param name="seenAt">When it was last seen; now when not given.</param>
    /// <returns>The entry.</returns>
    private static CatalogEntry Entry(string route, int? anidbId = null, int? anilistId = null, int? malId = null, DateTime? seenAt = null)
        => new()
        {
            Route = route,
            AnidbID = anidbId,
            AnilistID = anilistId,
            MalID = malId,
            LastSeenAt = seenAt ?? DateTime.UtcNow,
        };

    #endregion
}
