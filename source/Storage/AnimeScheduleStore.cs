using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;

namespace Shoko.Plugin.AnimeSchedule.Storage;

/// <summary>
/// Reads and writes the plugin's own database. Every call opens a context of
/// its own, and writes are serialized, so a sweep and a refresh of one series
/// never race to insert the same row.
/// </summary>
/// <remarks>
/// The server hands out no context before it has migrated the database, so
/// nothing here may be called from a constructor or from the plugin's setup.
/// </remarks>
public sealed class AnimeScheduleStore
{
    private readonly IDbContextFactory<AnimeScheduleDbContext> _contexts;

    private readonly Lock _writeLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeScheduleStore"/>
    /// class.
    /// </summary>
    /// <param name="contexts">Creates contexts on the plugin's own database.</param>
    /// <exception cref="ArgumentNullException"><paramref name="contexts"/> is <c>null</c>.</exception>
    public AnimeScheduleStore(IDbContextFactory<AnimeScheduleDbContext> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);

        _contexts = contexts;
    }

    #region Catalog

    /// <summary>
    /// Every anime in the catalog.
    /// </summary>
    /// <returns>The entries, by route.</returns>
    public IReadOnlyList<CatalogEntry> GetCatalog()
    {
        using var context = _contexts.CreateDbContext();
        return [.. context.Catalog.AsNoTracking().OrderBy(entry => entry.Route)];
    }

    /// <summary>
    /// Adds the entries to the catalog, replacing the stored ones of the
    /// same route.
    /// </summary>
    /// <param name="entries">The entries.</param>
    public void UpsertCatalog(IEnumerable<CatalogEntry> entries)
    {
        var byRoute = entries
            .Where(entry => !string.IsNullOrEmpty(entry.Route))
            .GroupBy(entry => entry.Route, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        if (byRoute.Count == 0)
            return;

        lock (_writeLock)
        {
            using var context = _contexts.CreateDbContext();
            var routes = byRoute.Keys.ToList();
            var stored = context.Catalog
                .Where(entry => routes.Contains(entry.Route))
                .ToDictionary(entry => entry.Route, StringComparer.Ordinal);
            foreach (var (route, entry) in byRoute)
            {
                if (stored.TryGetValue(route, out var row))
                    context.Entry(row).CurrentValues.SetValues(entry);
                else
                    context.Catalog.Add(Copy(entry));
            }

            context.SaveChanges();
        }
    }

    /// <summary>
    /// Removes the anime not seen since the given time.
    /// </summary>
    /// <param name="seenBefore">The cut-off, in UTC.</param>
    /// <returns>How many entries were removed.</returns>
    public int PruneCatalog(DateTime seenBefore)
    {
        lock (_writeLock)
        {
            using var context = _contexts.CreateDbContext();
            return context.Catalog.Where(entry => entry.LastSeenAt < seenBefore).ExecuteDelete();
        }
    }

    #endregion

    #region Lookups

    /// <summary>
    /// The lookup state of every AniDB anime that has one.
    /// </summary>
    /// <returns>The rows, by AniDB anime ID.</returns>
    public Dictionary<int, AnidbLookup> GetLookups()
    {
        using var context = _contexts.CreateDbContext();
        return context.Lookups.AsNoTracking().ToDictionary(lookup => lookup.AnidbAnimeID);
    }

    /// <summary>
    /// The lookup state of one AniDB anime.
    /// </summary>
    /// <param name="anidbAnimeID">The AniDB anime ID.</param>
    /// <returns>The row, or <c>null</c> when the anime was never looked up.</returns>
    public AnidbLookup? GetLookup(int anidbAnimeID)
    {
        using var context = _contexts.CreateDbContext();
        return context.Lookups.AsNoTracking().FirstOrDefault(lookup => lookup.AnidbAnimeID == anidbAnimeID);
    }

    /// <summary>
    /// Stores the rows, replacing the stored ones of the same anime.
    /// </summary>
    /// <param name="lookups">The rows.</param>
    public void SaveLookups(IEnumerable<AnidbLookup> lookups)
    {
        var byID = lookups
            .GroupBy(lookup => lookup.AnidbAnimeID)
            .ToDictionary(group => group.Key, group => group.Last());
        if (byID.Count == 0)
            return;

        lock (_writeLock)
        {
            using var context = _contexts.CreateDbContext();
            var ids = byID.Keys.ToList();
            var stored = context.Lookups
                .Where(lookup => ids.Contains(lookup.AnidbAnimeID))
                .ToDictionary(lookup => lookup.AnidbAnimeID);
            foreach (var (id, lookup) in byID)
            {
                if (stored.TryGetValue(id, out var row))
                    context.Entry(row).CurrentValues.SetValues(lookup);
                else
                    context.Lookups.Add(Copy(lookup));
            }

            context.SaveChanges();
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// A copy of the entry, so the caller's instance is never tracked.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The copy.</returns>
    private static CatalogEntry Copy(CatalogEntry entry)
        => new()
        {
            Route = entry.Route,
            AnidbID = entry.AnidbID,
            AnilistID = entry.AnilistID,
            MalID = entry.MalID,
            Title = entry.Title,
            Status = entry.Status,
            Episodes = entry.Episodes,
            LastSeenAt = entry.LastSeenAt,
        };

    /// <summary>
    /// A copy of the row, so the caller's instance is never tracked.
    /// </summary>
    /// <param name="lookup">The row.</param>
    /// <returns>The copy.</returns>
    private static AnidbLookup Copy(AnidbLookup lookup)
        => new()
        {
            AnidbAnimeID = lookup.AnidbAnimeID,
            Route = lookup.Route,
            State = lookup.State,
            LastCheckedAt = lookup.LastCheckedAt,
            NextCheckAt = lookup.NextCheckAt,
            ConsecutiveMisses = lookup.ConsecutiveMisses,
            Fingerprint = lookup.Fingerprint,
        };

    #endregion
}
