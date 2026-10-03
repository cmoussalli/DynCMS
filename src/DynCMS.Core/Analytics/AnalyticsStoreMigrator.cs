using DynCMS.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DynCMS.Core.Analytics;

/// <summary>What to do with recorded history that sits in a database analytics is leaving (or no longer uses).</summary>
public enum AnalyticsHistoryAction
{
    /// <summary>Copy the page views and cached locations into the analytics store, then remove them from the old one.</summary>
    Move,

    /// <summary>Delete them; the reports start over. Keeps the old database small.</summary>
    Delete
}

/// <summary>Analytics rows found in a database (typically old history left in the primary database).</summary>
public sealed record AnalyticsHistoryInfo(long PageViews, long GeoEntries)
{
    public bool IsEmpty => PageViews == 0 && GeoEntries == 0;
}

/// <summary>What moving the analytics store did.</summary>
public sealed record AnalyticsMigrationResult(long PageViewsCopied, long GeoEntriesCopied, bool SourceCleared);

/// <summary>
/// Moves the analytics tables between databases when the analytics store is switched: creates the schema on the
/// target, copies page views and the geolocation cache in batches, and clears (or drops) them from the source.
/// </summary>
internal static class AnalyticsStoreMigrator
{
    private const int BatchSize = 500;

    /// <summary>Creates the analytics tables on <paramref name="config"/> if they are missing.</summary>
    public static async Task EnsureSchemaAsync(DatabaseConfiguration config, string basePath, CancellationToken ct)
    {
        if (config.Provider == DatabaseProvider.Sqlite)
            Directory.CreateDirectory(Path.GetDirectoryName(config.Sqlite!.ResolvePath(basePath))!);
        await using var db = new AnalyticsDbContext(AnalyticsDbContext.OptionsFor(config, basePath));
        await DatabaseSchema.EnsureTablesAsync(db, ct);
    }

    /// <summary>How many analytics rows <paramref name="config"/> holds, or null when it has no analytics tables.</summary>
    public static async Task<AnalyticsHistoryInfo?> CountAsync(DatabaseConfiguration config, string basePath, CancellationToken ct)
    {
        await using var db = new AnalyticsDbContext(AnalyticsDbContext.OptionsFor(config, basePath));
        if (!await DatabaseSchema.TableExistsAsync(db, "AnalyticsPageViews", ct)) return null;
        var views = await db.PageViews.LongCountAsync(ct);
        var geo = await DatabaseSchema.TableExistsAsync(db, "AnalyticsGeoIp", ct) ? await db.GeoIpEntries.LongCountAsync(ct) : 0;
        return new AnalyticsHistoryInfo(views, geo);
    }

    /// <summary>Removes the analytics rows from <paramref name="config"/>: drops the tables, or only empties them when <paramref name="dropTables"/> is false.</summary>
    public static async Task ClearAsync(DatabaseConfiguration config, string basePath, bool dropTables, CancellationToken ct)
    {
        await using var db = new AnalyticsDbContext(AnalyticsDbContext.OptionsFor(config, basePath));
        await ClearAsync(db, dropTables, ct);
    }

    private static async Task ClearAsync(AnalyticsDbContext db, bool dropTables, CancellationToken ct)
    {
        if (dropTables)
        {
            foreach (var table in AnalyticsDbContext.TableNames)
            {
                if (await DatabaseSchema.TableExistsAsync(db, table, ct))
                    await db.Database.ExecuteSqlRawAsync($"DROP TABLE \"{table}\"", ct);
            }
            return;
        }
        if (await DatabaseSchema.TableExistsAsync(db, "AnalyticsPageViews", ct)) await db.PageViews.ExecuteDeleteAsync(ct);
        if (await DatabaseSchema.TableExistsAsync(db, "AnalyticsGeoIp", ct)) await db.GeoIpEntries.ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Copies every page view and cached geolocation from <paramref name="source"/> to <paramref name="target"/>.
    /// Rows already in the target are kept (page views get new ids; geolocation entries are replaced by address).
    /// </summary>
    public static async Task<AnalyticsMigrationResult> CopyAsync(
        DatabaseConfiguration source, DatabaseConfiguration target, string basePath, bool clearSource, bool dropSourceTables, ILogger logger, CancellationToken ct)
    {
        await using var from = new AnalyticsDbContext(AnalyticsDbContext.OptionsFor(source, basePath));
        await using var to = new AnalyticsDbContext(AnalyticsDbContext.OptionsFor(target, basePath));
        to.ChangeTracker.AutoDetectChangesEnabled = false;

        long views = 0, geo = 0;

        if (await DatabaseSchema.TableExistsAsync(from, "AnalyticsGeoIp", ct))
        {
            var existing = await to.GeoIpEntries.Select(g => g.IpAddress).ToListAsync(ct);
            var known = new HashSet<string>(existing, StringComparer.Ordinal);
            await foreach (var entry in from.GeoIpEntries.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
            {
                if (!known.Add(entry.IpAddress)) continue;
                to.GeoIpEntries.Add(entry);
                geo++;
                if (geo % BatchSize == 0) await FlushAsync(to, ct);
            }
            await FlushAsync(to, ct);
        }

        if (await DatabaseSchema.TableExistsAsync(from, "AnalyticsPageViews", ct))
        {
            long lastId = 0;
            while (true)
            {
                var batch = await from.PageViews.AsNoTracking()
                    .Where(v => v.Id > lastId)
                    .OrderBy(v => v.Id)
                    .Take(BatchSize)
                    .ToListAsync(ct);
                if (batch.Count == 0) break;
                lastId = batch[^1].Id;
                foreach (var view in batch)
                {
                    view.Id = 0; // let the target assign ids
                    to.PageViews.Add(view);
                }
                views += batch.Count;
                await FlushAsync(to, ct);
            }
        }

        logger.LogInformation("Copied {Views} page views and {Geo} geolocation entries from {Source} to {Target}",
            views, geo, source.Describe(basePath), target.Describe(basePath));

        var cleared = false;
        if (clearSource)
        {
            // Leaving the primary database: take the analytics tables out of it altogether so backups of the content
            // stay small and a later "share again" starts from a clean schema. Leaving a dedicated database: empty it.
            await ClearAsync(from, dropSourceTables, ct);
            cleared = true;
        }

        return new AnalyticsMigrationResult(views, geo, cleared);
    }

    private static async Task FlushAsync(AnalyticsDbContext db, CancellationToken ct)
    {
        if (!db.ChangeTracker.HasChanges()) return;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }
}
