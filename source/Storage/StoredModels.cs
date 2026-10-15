using System;

namespace Shoko.Plugin.AnimeSchedule.Storage;

/// <summary>
/// One AnimeSchedule.net anime the plugin has seen, in the airing lists or in
/// the answer to a lookup, with the IDs its links give it on other sites.
/// </summary>
public sealed class CatalogEntry
{
    /// <summary>
    /// AnimeSchedule.net's URL slug for the anime, which timetable entries
    /// are joined back to it by.
    /// </summary>
    public string Route { get; set; } = "";

    /// <summary>
    /// The AniDB anime ID, read from the anime's AniDB link, or <c>null</c>.
    /// </summary>
    public int? AnidbID { get; set; }

    /// <summary>
    /// The AniList anime ID, read from the anime's AniList link, or
    /// <c>null</c>.
    /// </summary>
    public int? AnilistID { get; set; }

    /// <summary>
    /// The MyAnimeList anime ID, read from the anime's MyAnimeList link, or
    /// <c>null</c>.
    /// </summary>
    public int? MalID { get; set; }

    /// <summary>
    /// The display title.
    /// </summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// The airing status as AnimeSchedule.net spells it, such as
    /// <c>Ongoing</c> or <c>Upcoming</c>.
    /// </summary>
    public string Status { get; set; } = "";

    /// <summary>
    /// The total number of episodes, or <c>null</c> when unknown.
    /// </summary>
    public int? Episodes { get; set; }

    /// <summary>
    /// When the anime was last seen in a list or a lookup, in UTC.
    /// </summary>
    public DateTime LastSeenAt { get; set; }
}

/// <summary>
/// Where the lookup of one AniDB anime on AnimeSchedule.net stands.
/// </summary>
public enum LookupState
{
    /// <summary>
    /// AnimeSchedule.net did not know the anime the last time it was asked.
    /// </summary>
    Unknown,

    /// <summary>
    /// AnimeSchedule.net knows the anime, under <see cref="AnidbLookup.Route"/>.
    /// </summary>
    Known,

    /// <summary>
    /// The anime is no longer looked up, until its AniDB data changes.
    /// </summary>
    GivenUp,
}

/// <summary>
/// What the plugin learned the last time it looked an AniDB anime up, and
/// when to look again.
/// </summary>
public sealed class AnidbLookup
{
    /// <summary>
    /// The AniDB anime ID.
    /// </summary>
    public int AnidbAnimeID { get; set; }

    /// <summary>
    /// AnimeSchedule.net's route for the anime, when it is known.
    /// </summary>
    public string? Route { get; set; }

    /// <summary>
    /// Where the lookup stands.
    /// </summary>
    public LookupState State { get; set; }

    /// <summary>
    /// When the anime was last looked up, in UTC.
    /// </summary>
    public DateTime LastCheckedAt { get; set; }

    /// <summary>
    /// When the anime is due another lookup, in UTC, or <c>null</c> once it
    /// is given up.
    /// </summary>
    public DateTime? NextCheckAt { get; set; }

    /// <summary>
    /// How many lookups in a row AnimeSchedule.net did not know the anime.
    /// </summary>
    public int ConsecutiveMisses { get; set; }

    /// <summary>
    /// The AniDB data the last decision was made on: its start date, end
    /// date and episode count. A change clears the backoff.
    /// </summary>
    public string Fingerprint { get; set; } = "";
}
