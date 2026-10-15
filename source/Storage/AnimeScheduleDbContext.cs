using System;
using Microsoft.EntityFrameworkCore;

namespace Shoko.Plugin.AnimeSchedule.Storage;

/// <summary>
/// The plugin's own database: the AnimeSchedule.net catalog and the lookup
/// state of each AniDB anime. The server configures it and applies its
/// migrations while it starts.
/// </summary>
/// <param name="options">The options the server built.</param>
public class AnimeScheduleDbContext(DbContextOptions<AnimeScheduleDbContext> options) : DbContext(options)
{
    /// <summary>
    /// The name the database is registered under, and its file name.
    /// </summary>
    public const string DatabaseName = "animeschedule";

    /// <summary>
    /// The AnimeSchedule.net anime the plugin has seen.
    /// </summary>
    public DbSet<CatalogEntry> Catalog => Set<CatalogEntry>();

    /// <summary>
    /// The lookup state of each AniDB anime.
    /// </summary>
    public DbSet<AnidbLookup> Lookups => Set<AnidbLookup>();

    /// <inheritdoc/>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CatalogEntry>(entry =>
        {
            entry.ToTable("Catalog");
            entry.HasKey(row => row.Route);
            entry.Property(row => row.Route).HasMaxLength(256);
            entry.Property(row => row.Status).HasMaxLength(32);
            entry.HasIndex(row => row.AnidbID);
            entry.HasIndex(row => row.AnilistID);
            entry.HasIndex(row => row.MalID);
        });

        modelBuilder.Entity<AnidbLookup>(lookup =>
        {
            lookup.ToTable("Lookups");
            lookup.HasKey(row => row.AnidbAnimeID);
            lookup.Property(row => row.AnidbAnimeID).ValueGeneratedNever();
            lookup.Property(row => row.Route).HasMaxLength(256);
            lookup.Property(row => row.State).HasConversion<string>().HasMaxLength(16);
            lookup.Property(row => row.Fingerprint).HasMaxLength(64);
        });
    }
}
