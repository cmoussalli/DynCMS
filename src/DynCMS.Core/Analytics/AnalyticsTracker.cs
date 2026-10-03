using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using DynCMS.Core.Data;
using DynCMS.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynCMS.Core.Analytics;

/// <summary>A page view as captured at render time, before the worker enriches and stores it.</summary>
public sealed record PageViewHit(
    DateTime VisitedAt,
    string Host,
    string Path,
    string? Query,
    string? Title,
    Guid? ContentId,
    string? Culture,
    bool NotFound,
    string? Referrer,
    string? IpAddress,
    string? UserAgent,
    string? Language);

/// <summary>
/// The queue between the renderer and the database: page views are handed over in memory and written in batches by
/// <see cref="AnalyticsWorker"/>, so recording a view never slows a page down.
/// </summary>
public interface IAnalyticsTracker
{
    /// <summary>Queues a page view. Returns false when the queue is full and the view was dropped.</summary>
    bool TryEnqueue(PageViewHit hit);

    /// <summary>Page views waiting to be written.</summary>
    int QueueLength { get; }

    /// <summary>Page views dropped because the queue was full since the application started.</summary>
    long Dropped { get; }

    /// <summary>IP addresses waiting for a geolocation lookup.</summary>
    int PendingGeoLookups { get; }
}

internal sealed class AnalyticsQueue : IAnalyticsTracker
{
    private readonly Channel<PageViewHit> _channel;
    private int _length;
    private long _dropped;

    public AnalyticsQueue(IOptions<DynCmsOptions> options)
    {
        var capacity = Math.Max(100, options.Value.Analytics.QueueCapacity);
        _channel = Channel.CreateBounded<PageViewHit>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    }

    private int _generation;

    public ChannelReader<PageViewHit> Reader => _channel.Reader;
    public int QueueLength => Volatile.Read(ref _length);
    public long Dropped => Interlocked.Read(ref _dropped);
    public int PendingGeoLookups { get; internal set; }

    /// <summary>Incremented whenever the analytics store moves or is restored, so the worker drops its caches.</summary>
    public int StoreGeneration => Volatile.Read(ref _generation);

    /// <summary>While true the worker leaves page views in the queue (set while the store is being moved).</summary>
    public volatile bool Paused;

    public void NotifyStoreChanged() => Interlocked.Increment(ref _generation);

    public bool TryEnqueue(PageViewHit hit)
    {
        if (_channel.Writer.TryWrite(hit))
        {
            Interlocked.Increment(ref _length);
            return true;
        }
        Interlocked.Increment(ref _dropped);
        return false;
    }

    internal void MarkRead(int count) => Interlocked.Add(ref _length, -count);
}

/// <summary>
/// Drains the page view queue into the database, assigns visitor and session ids, applies the IP settings, resolves
/// geolocation through the cache and the lookup service, and purges data past its retention. One instance per application.
/// </summary>
internal sealed class AnalyticsWorker(
    AnalyticsQueue queue,
    IDbContextFactory<AnalyticsDbContext> factory,
    IDynCmsRuntime runtime,
    IAnalyticsSettingsService settings,
    IGeoLocator geoLocator,
    IOptions<DynCmsOptions> options,
    ILogger<AnalyticsWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(1);
    private const int MaxBatch = 200;
    private const int MaxLookupsPerCycle = 25;

    private readonly Dictionary<string, (string SessionId, DateTime LastSeen)> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GeoIpEntry> _geoCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<long>> _pendingGeo = new(StringComparer.Ordinal);
    private readonly Queue<string> _pendingGeoOrder = new();
    private readonly byte[] _hashKey = Encoding.UTF8.GetBytes("dyncms-analytics:" + options.Value.Identity.TokenEncryptionKey);
    private DateTime _lastMaintenance = DateTime.MinValue;
    private bool _storePrepared;
    private int _generation;

    private DynCmsAnalyticsOptions Options => options.Value.Analytics;
    private TimeSpan SessionTimeout => TimeSpan.FromMinutes(Math.Max(1, Options.SessionTimeoutMinutes));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (queue.Paused)
                {
                    // The store is being moved; keep the views queued until it is done.
                    await Task.Delay(500, stoppingToken);
                    continue;
                }

                if (queue.StoreGeneration != _generation)
                {
                    // The store moved or was restored: everything cached about it (sessions, geolocation, pending ids) is stale.
                    _generation = queue.StoreGeneration;
                    _sessions.Clear();
                    _geoCache.Clear();
                    _pendingGeo.Clear();
                    _pendingGeoOrder.Clear();
                    queue.PendingGeoLookups = 0;
                    _storePrepared = false;
                }

                var batch = await ReadBatchAsync(stoppingToken);
                if (!runtime.IsReady)
                {
                    // Nothing can be written before the database is set up; the views are dropped rather than kept forever.
                    continue;
                }

                if (!_storePrepared)
                {
                    await PrepareStoreAsync(stoppingToken);
                    _storePrepared = true;
                }

                if (batch.Count > 0) await WriteBatchAsync(batch, stoppingToken);
                await ResolveGeoAsync(stoppingToken);

                if (DateTime.UtcNow - _lastMaintenance > MaintenanceInterval)
                {
                    _lastMaintenance = DateTime.UtcNow;
                    await MaintainAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (DynCmsNotConfiguredException)
            {
                // The database was reset while we were working; wait for it to come back.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Analytics worker failed; continuing");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    // ---- queue -------------------------------------------------------------------------------------------------

    private async Task<List<PageViewHit>> ReadBatchAsync(CancellationToken ct)
    {
        var batch = new List<PageViewHit>();
        var reader = queue.Reader;

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(IdleWait);
        try
        {
            if (!await reader.WaitToReadAsync(idle.Token)) return batch;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return batch; // idle timeout: run housekeeping
        }

        // Let a burst accumulate briefly so it is written in one round trip.
        await Task.Delay(BatchWindow, ct);
        while (batch.Count < MaxBatch && reader.TryRead(out var hit)) batch.Add(hit);
        queue.MarkRead(batch.Count);
        return batch;
    }

    // ---- writing -----------------------------------------------------------------------------------------------

    private async Task WriteBatchAsync(List<PageViewHit> batch, CancellationToken ct)
    {
        var current = settings.Current;
        if (!current.Enabled) return;

        await using var db = await factory.CreateDbContextAsync(ct);
        var views = new List<(PageView View, string? LookupIp)>(batch.Count);

        foreach (var hit in batch)
        {
            var ua = UserAgentParser.Parse(hit.UserAgent);
            if (ua.IsBot && !current.TrackBots) continue;

            var ip = IpAddressHelper.Normalize(hit.IpAddress);
            var visitorId = VisitorIdFor(ip, hit.UserAgent);
            var sessionId = await SessionFor(db, visitorId, hit.VisitedAt, ct);
            var storedIp = current.StoreIpAddress ? (current.AnonymizeIp ? IpAddressHelper.Anonymize(ip) : ip) : null;
            var (referrer, referrerHost) = NormalizeReferrer(hit.Referrer);

            var view = new PageView
            {
                VisitedAt = hit.VisitedAt,
                Host = Truncate(hit.Host, 255) ?? string.Empty,
                Path = Truncate(hit.Path, 2000) ?? "/",
                Query = Truncate(hit.Query, 1000),
                Title = Truncate(hit.Title, 255),
                ContentId = hit.ContentId,
                Culture = Truncate(hit.Culture, 20),
                NotFound = hit.NotFound,
                Referrer = referrer,
                ReferrerHost = referrerHost,
                VisitorId = visitorId,
                SessionId = sessionId,
                IpAddress = Truncate(storedIp, 45),
                UserAgent = Truncate(hit.UserAgent, 1000),
                Browser = ua.Browser,
                BrowserVersion = ua.BrowserVersion,
                OperatingSystem = ua.OperatingSystem,
                DeviceType = ua.DeviceType,
                IsBot = ua.IsBot,
                Language = Truncate(hit.Language, 20),
                GeoResolved = true
            };

            string? lookupIp = null;
            if (current.GeoLookupEnabled && ip is not null && !IpAddressHelper.IsPrivate(ip))
            {
                var cached = await CachedGeoAsync(db, ip, ct);
                if (cached is null)
                {
                    view.GeoResolved = false;
                    lookupIp = ip;
                }
                else if (cached.Success)
                {
                    Apply(view, cached);
                }
            }

            db.PageViews.Add(view);
            views.Add((view, lookupIp));
        }

        if (views.Count == 0) return;
        await db.SaveChangesAsync(ct);

        foreach (var (view, lookupIp) in views)
        {
            if (lookupIp is not null) EnqueueLookup(lookupIp, view.Id);
        }
    }

    private string VisitorIdFor(string? ip, string? userAgent)
    {
        var material = Encoding.UTF8.GetBytes((ip ?? "unknown") + "|" + (userAgent ?? string.Empty));
        var hash = HMACSHA256.HashData(_hashKey, material);
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private async Task<string> SessionFor(AnalyticsDbContext db, string visitorId, DateTime at, CancellationToken ct)
    {
        if (_sessions.TryGetValue(visitorId, out var session) && at - session.LastSeen <= SessionTimeout)
        {
            _sessions[visitorId] = (session.SessionId, at);
            return session.SessionId;
        }

        // After a restart the map is empty; continue a session the database still knows about.
        var since = at - SessionTimeout;
        var previous = await db.PageViews.AsNoTracking()
            .Where(v => v.VisitorId == visitorId && v.VisitedAt >= since)
            .OrderByDescending(v => v.VisitedAt)
            .Select(v => v.SessionId)
            .FirstOrDefaultAsync(ct);

        var id = previous ?? Guid.NewGuid().ToString("N")[..16];
        _sessions[visitorId] = (id, at);
        return id;
    }

    private static (string? Referrer, string? Host) NormalizeReferrer(string? referrer)
    {
        if (string.IsNullOrWhiteSpace(referrer)) return (null, null);
        var value = Truncate(referrer.Trim(), 2000);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return (value, null);
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        return (value, Truncate(host, 255));
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    // ---- geolocation -------------------------------------------------------------------------------------------

    private async Task<GeoIpEntry?> CachedGeoAsync(AnalyticsDbContext db, string ip, CancellationToken ct)
    {
        var freshAfter = DateTime.UtcNow.AddDays(-Math.Max(1, Options.GeoCacheDays));
        if (_geoCache.TryGetValue(ip, out var entry) && entry.ResolvedAt >= freshAfter) return entry;

        entry = await db.GeoIpEntries.AsNoTracking().FirstOrDefaultAsync(g => g.IpAddress == ip, ct);
        if (entry is null) return null;

        // A failed lookup is retried after a day, a successful one after the cache period.
        var validFor = entry.Success ? TimeSpan.FromDays(Math.Max(1, Options.GeoCacheDays)) : TimeSpan.FromDays(1);
        if (DateTime.UtcNow - entry.ResolvedAt > validFor) return null;

        if (_geoCache.Count > 5000) _geoCache.Clear();
        _geoCache[ip] = entry;
        return entry;
    }

    private void EnqueueLookup(string ip, long viewId)
    {
        if (_pendingGeo.TryGetValue(ip, out var ids))
        {
            ids.Add(viewId);
            return;
        }
        _pendingGeo[ip] = [viewId];
        _pendingGeoOrder.Enqueue(ip);
        queue.PendingGeoLookups = _pendingGeoOrder.Count;
    }

    /// <summary>Makes sure the tables exist on the current store and resumes lookups a previous run left unfinished.</summary>
    private async Task PrepareStoreAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await DatabaseSchema.EnsureTablesAsync(db, ct);
            var pending = await db.PageViews.AsNoTracking()
                .Where(v => !v.GeoResolved && v.IpAddress != null)
                .Select(v => new { v.IpAddress, v.Id })
                .Take(2000)
                .ToListAsync(ct);
            foreach (var item in pending) EnqueueLookup(item.IpAddress!, item.Id);
            if (pending.Count > 0) logger.LogInformation("Resuming geolocation of {Count} page views", pending.Count);
        }
        catch (DynCmsNotConfiguredException) { }
    }

    private async Task ResolveGeoAsync(CancellationToken ct)
    {
        if (_pendingGeoOrder.Count == 0) return;
        var current = settings.Current;

        var done = 0;
        while (_pendingGeoOrder.Count > 0 && done < MaxLookupsPerCycle && !ct.IsCancellationRequested)
        {
            var ip = _pendingGeoOrder.Dequeue();
            if (!_pendingGeo.Remove(ip, out var ids)) continue;
            queue.PendingGeoLookups = _pendingGeoOrder.Count;
            done++;

            await using var db = await factory.CreateDbContextAsync(ct);

            var entry = current.GeoLookupEnabled ? await CachedGeoAsync(db, ip, ct) : null;
            if (entry is null && current.GeoLookupEnabled)
            {
                var location = await geoLocator.LookupAsync(ip, current.GeoLookupUrl, ct);
                entry = new GeoIpEntry
                {
                    IpAddress = ip,
                    ResolvedAt = DateTime.UtcNow,
                    Success = location is not null,
                    CountryCode = location?.CountryCode,
                    Country = Truncate(location?.Country, 100),
                    Region = Truncate(location?.Region, 100),
                    City = Truncate(location?.City, 100),
                    Latitude = location?.Latitude,
                    Longitude = location?.Longitude,
                    Timezone = Truncate(location?.Timezone, 50),
                    Organization = Truncate(location?.Organization, 200)
                };
                await UpsertGeoAsync(db, entry, ct);
                _geoCache[ip] = entry;

                if (Options.GeoLookupDelayMilliseconds > 0)
                    await Task.Delay(Options.GeoLookupDelayMilliseconds, ct);
            }

            if (entry is { Success: true })
            {
                await db.PageViews.Where(v => ids.Contains(v.Id))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(v => v.CountryCode, entry.CountryCode)
                        .SetProperty(v => v.Country, entry.Country)
                        .SetProperty(v => v.Region, entry.Region)
                        .SetProperty(v => v.City, entry.City)
                        .SetProperty(v => v.Latitude, entry.Latitude)
                        .SetProperty(v => v.Longitude, entry.Longitude)
                        .SetProperty(v => v.GeoResolved, true), ct);
            }
            else
            {
                await db.PageViews.Where(v => ids.Contains(v.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(v => v.GeoResolved, true), ct);
            }
        }
    }

    private static async Task UpsertGeoAsync(AnalyticsDbContext db, GeoIpEntry entry, CancellationToken ct)
    {
        var existing = await db.GeoIpEntries.FirstOrDefaultAsync(g => g.IpAddress == entry.IpAddress, ct);
        if (existing is null)
        {
            db.GeoIpEntries.Add(entry);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(entry);
        }
        await db.SaveChangesAsync(ct);
    }

    private static void Apply(PageView view, GeoIpEntry geo)
    {
        view.CountryCode = geo.CountryCode;
        view.Country = geo.Country;
        view.Region = geo.Region;
        view.City = geo.City;
        view.Latitude = geo.Latitude;
        view.Longitude = geo.Longitude;
        view.GeoResolved = true;
    }

    // ---- housekeeping ------------------------------------------------------------------------------------------

    private async Task MaintainAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Sessions that timed out are of no use any more.
        foreach (var key in _sessions.Where(s => now - s.Value.LastSeen > SessionTimeout).Select(s => s.Key).ToList())
            _sessions.Remove(key);

        await using var db = await factory.CreateDbContextAsync(ct);

        var retention = settings.Current.RetentionDays;
        if (retention > 0)
        {
            var cutoff = now.AddDays(-retention);
            var removed = await db.PageViews.Where(v => v.VisitedAt < cutoff).ExecuteDeleteAsync(ct);
            if (removed > 0) logger.LogInformation("Analytics retention removed {Count} page views older than {Days} days", removed, retention);
        }

        var geoCutoff = now.AddDays(-Math.Max(1, Options.GeoCacheDays) * 2);
        await db.GeoIpEntries.Where(g => g.ResolvedAt < geoCutoff).ExecuteDeleteAsync(ct);
    }
}
