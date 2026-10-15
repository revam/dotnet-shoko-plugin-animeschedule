using System;
using System.Collections.Generic;
using System.Linq;
using Shoko.Plugin.AnimeSchedule.Storage;

namespace Shoko.Plugin.AnimeSchedule;

/// <summary>
/// The catalog, indexed by the IDs a library series is matched on.
/// </summary>
/// <remarks>
/// A series matches by its AniDB anime ID first, then by the AniList anime it
/// is linked to, then by the MyAnimeList ID of that AniList entry. AniDB's own
/// MyAnimeList links are never used: AniDB folds many MyAnimeList entries into
/// one anime and does not say which is the main one.
/// </remarks>
public sealed class CatalogIndex
{
    private readonly Dictionary<string, CatalogEntry> _byRoute;

    private readonly Dictionary<int, CatalogEntry> _byAnidb;

    private readonly Dictionary<int, CatalogEntry> _byAnilist;

    private readonly Dictionary<int, CatalogEntry> _byMal;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogIndex"/> class.
    /// Where two entries share an ID, the one seen last wins.
    /// </summary>
    /// <param name="entries">The catalog.</param>
    public CatalogIndex(IEnumerable<CatalogEntry> entries)
    {
        var ordered = entries.OrderBy(entry => entry.LastSeenAt).ToList();
        _byRoute = new(StringComparer.Ordinal);
        _byAnidb = [];
        _byAnilist = [];
        _byMal = [];
        foreach (var entry in ordered)
        {
            _byRoute[entry.Route] = entry;
            if (entry.AnidbID is { } anidbID)
                _byAnidb[anidbID] = entry;
            if (entry.AnilistID is { } anilistID)
                _byAnilist[anilistID] = entry;
            if (entry.MalID is { } malID)
                _byMal[malID] = entry;
        }
    }

    /// <summary>
    /// How many anime the catalog holds.
    /// </summary>
    public int Count => _byRoute.Count;

    /// <summary>
    /// Whether any entry carries an AniList ID, so matching on one can work.
    /// </summary>
    public bool HasAnilistIDs => _byAnilist.Count > 0;

    /// <summary>
    /// Whether any entry carries a MyAnimeList ID, so matching on one can
    /// work.
    /// </summary>
    public bool HasMalIDs => _byMal.Count > 0;

    /// <summary>
    /// The entry of a route.
    /// </summary>
    /// <param name="route">The route.</param>
    /// <returns>The entry, or <c>null</c>.</returns>
    public CatalogEntry? GetByRoute(string route)
        => _byRoute.GetValueOrDefault(route);

    /// <summary>
    /// Finds the entry of a library series. The AniList IDs are only asked
    /// for when the ones before them did not match.
    /// </summary>
    /// <param name="anidbAnimeID">The series' AniDB anime ID.</param>
    /// <param name="getAnilistIDs">Gets the AniList anime the series is linked to.</param>
    /// <param name="getAnilistMalIDs">Gets the MyAnimeList IDs of those AniList entries, or <c>null</c> to skip them.</param>
    /// <returns>The entry, or <c>null</c> when none matches.</returns>
    public CatalogEntry? Match(int anidbAnimeID, Func<IReadOnlyList<int>> getAnilistIDs, Func<IReadOnlyList<int>>? getAnilistMalIDs = null)
    {
        if (_byAnidb.TryGetValue(anidbAnimeID, out var entry))
            return entry;

        if (HasAnilistIDs && FindFirst(_byAnilist, getAnilistIDs()) is { } byAnilist)
            return byAnilist;

        if (HasMalIDs && getAnilistMalIDs is not null && FindFirst(_byMal, getAnilistMalIDs()) is { } byMal)
            return byMal;

        return null;
    }

    /// <summary>
    /// The entry of the first ID the index holds.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="ids">The IDs, in order of preference.</param>
    /// <returns>The entry, or <c>null</c>.</returns>
    private static CatalogEntry? FindFirst(Dictionary<int, CatalogEntry> index, IReadOnlyList<int> ids)
    {
        foreach (var id in ids)
        {
            if (index.TryGetValue(id, out var entry))
                return entry;
        }

        return null;
    }
}
