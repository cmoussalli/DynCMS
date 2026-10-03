using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using DynCMS.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace DynCMS.Core.Analytics;

#region Models

/// <summary>A half-open UTC interval <c>[From, To)</c> the reports are computed over.</summary>
public sealed record AnalyticsRange(DateTime From, DateTime To)
{
    public TimeSpan Length => To - From;

    /// <summary>The interval of the same length right before this one, for comparisons.</summary>
    public AnalyticsRange Previous() => new(From - Length, From);

    /// <summary>Ranges of two days or less are charted per hour, longer ones per day.</summary>
    public bool Hourly => Length <= TimeSpan.FromHours(49);

    /// <summary>The last <paramref name="days"/> local days including today, expressed in UTC.</summary>
    public static AnalyticsRange LastDays(int days, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Date;
        var start = todayLocal.AddDays(-(Math.Max(1, days) - 1));
        var end = todayLocal.AddDays(1);
        return new AnalyticsRange(ToUtc(start, zone), ToUtc(end, zone));
    }

    /// <summary>Whole local days from <paramref name="firstDay"/> to <paramref name="lastDay"/> inclusive, in UTC.</summary>
    public static AnalyticsRange Days(DateTime firstDay, DateTime lastDay, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        if (lastDay.Date < firstDay.Date) (firstDay, lastDay) = (lastDay, firstDay);
        return new AnalyticsRange(ToUtc(firstDay.Date, zone), ToUtc(lastDay.Date.AddDays(1), zone));
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
}

/// <summary>The headline numbers of a range.</summary>
public sealed record AnalyticsSummary(
    long PageViews,
    long Visitors,
    long Sessions,
    double PagesPerSession,
    double BounceRate,
    double AverageSessionSeconds,
    long NotFoundViews);

/// <summary>Page views and visitors per hour or day.</summary>
public sealed record AnalyticsTimePoint(DateTime StartUtc, long PageViews, long Visitors);

/// <summary>Views and visitors of one value of a dimension (a browser, a country, a language…).</summary>
public sealed record AnalyticsCount(string? Key, string? Label, long PageViews, long Visitors);

public sealed record AnalyticsPageStat(string Path, string? Title, long PageViews, long Visitors, bool NotFound);

/// <summary>Where sessions came from, grouped by referring host with its channel.</summary>
public sealed record AnalyticsReferrerStat(string? Host, string Channel, long PageViews, long Visitors);

/// <summary>Views from one place on the map.</summary>
public sealed record AnalyticsGeoPoint(double Latitude, double Longitude, string? City, string? Region, string? Country, string? CountryCode, long PageViews, long Visitors);

/// <summary>Filters for the page view log.</summary>
public sealed record AnalyticsViewFilter(
    AnalyticsRange? Range = null,
    string? Path = null,
    string? VisitorId = null,
    string? SessionId = null,
    string? IpAddress = null,
    string? CountryCode = null,
    string? Search = null,
    bool NotFoundOnly = false,
    bool IncludeBots = false,
    int Skip = 0,
    int Take = 50);

public sealed record AnalyticsViewPage(IReadOnlyList<PageView> Items, long Total);

/// <summary>What is stored and what the worker is doing.</summary>
public sealed record AnalyticsDataInfo(
    long TotalViews,
    long BotViews,
    DateTime? OldestUtc,
    DateTime? NewestUtc,
    long GeoCacheEntries,
    long ViewsAwaitingGeo,
    int QueueLength,
    long Dropped);

/// <summary>Channels a referrer is sorted into.</summary>
public static class ReferrerChannels
{
    public const string Direct = "Direct";
    public const string Search = "Search";
    public const string Social = "Social";
    public const string Referral = "Referral";
    public const string Internal = "Internal";

    private static readonly string[] SearchHosts = ["google.", "bing.com", "duckduckgo.com", "yahoo.", "yandex.", "baidu.com", "ecosia.org", "qwant.com", "search.brave.com", "ask.com", "startpage.com", "seznam.cz", "naver.com"];
    private static readonly string[] SocialHosts = ["facebook.com", "fb.com", "l.facebook.com", "lm.facebook.com", "m.facebook.com", "instagram.com", "twitter.com", "x.com", "t.co", "linkedin.com", "lnkd.in", "reddit.com", "pinterest.", "tiktok.com", "youtube.com", "youtu.be", "threads.net", "mastodon.", "bsky.app", "snapchat.com", "tumblr.com", "whatsapp.com", "telegram.", "discord.com", "quora.com", "vk.com", "weibo.com"];

    public static string Classify(string? host)
    {
        if (string.IsNullOrEmpty(host)) return Direct;
        if (SearchHosts.Any(s => host.Contains(s, StringComparison.OrdinalIgnoreCase))) return Search;
        if (SocialHosts.Any(s => host.Equals(s, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase) || (s.EndsWith('.') && host.Contains(s, StringComparison.OrdinalIgnoreCase)))) return Social;
        return Referral;
    }
}

#endregion

/// <summary>
/// The reports behind the Analytics section, the API and the MCP tools. Everything is computed by the database
/// over the <c>AnalyticsPageViews</c> table; bots are always excluded except from the raw page view log on request.
/// </summary>
public interface IAnalyticsService
{
    Task<AnalyticsSummary> GetSummaryAsync(AnalyticsRange range, CancellationToken ct = default);

    /// <summary>Visitors with a page view in the last <paramref name="minutes"/> minutes.</summary>
    Task<long> GetActiveVisitorsAsync(int minutes = 5, CancellationToken ct = default);

    /// <summary>Views and visitors per bucket (hour or day, see <see cref="AnalyticsRange.Hourly"/>). Buckets are aligned to local time using <paramref name="utcOffsetMinutes"/>; empty buckets are included.</summary>
    Task<IReadOnlyList<AnalyticsTimePoint>> GetTimeSeriesAsync(AnalyticsRange range, int utcOffsetMinutes, CancellationToken ct = default);

    Task<IReadOnlyList<AnalyticsPageStat>> GetPagesAsync(AnalyticsRange range, int take = 50, string? search = null, CancellationToken ct = default);
    Task<IReadOnlyList<AnalyticsReferrerStat>> GetReferrersAsync(AnalyticsRange range, int take = 50, CancellationToken ct = default);
    Task<IReadOnlyList<AnalyticsCount>> GetCountriesAsync(AnalyticsRange range, int take = 50, CancellationToken ct = default);
    Task<IReadOnlyList<AnalyticsGeoPoint>> GetGeoPointsAsync(AnalyticsRange range, int take = 500, CancellationToken ct = default);
    Task<IReadOnlyList<AnalyticsCount>> GetBrowsersAsync(AnalyticsRange range, int take = 20, CancellationToken ct = default);
    Task<IReadOnlyList<AnalyticsCount>> GetOperatingSystemsAsync(AnalyticsRange range, int take = 20, CancellationToken ct = default);
    Task<IReadOnlyList<AnalyticsCount>> GetDevicesAsync(AnalyticsRange range, CancellationToken ct = default);
    Task<IReadOnlyList<AnalyticsCount>> GetLanguagesAsync(AnalyticsRange range, int take = 20, CancellationToken ct = default);
    Task<IReadOnlyList<AnalyticsCount>> GetHostsAsync(AnalyticsRange range, int take = 20, CancellationToken ct = default);

    /// <summary>The raw page view log, newest first.</summary>
    Task<AnalyticsViewPage> GetViewsAsync(AnalyticsViewFilter filter, CancellationToken ct = default);

    Task<AnalyticsDataInfo> GetDataInfoAsync(CancellationToken ct = default);

    /// <summary>Deletes page views older than <paramref name="olderThanUtc"/>, or all of them when null. Returns how many.</summary>
    Task<int> DeleteViewsAsync(DateTime? olderThanUtc, CancellationToken ct = default);

    /// <summary>Deletes the cached IP geolocations so they are looked up again.</summary>
    Task<int> ClearGeoCacheAsync(CancellationToken ct = default);

    /// <summary>Writes the page views of a range as CSV (UTF-8, comma separated, header row).</summary>
    Task ExportCsvAsync(AnalyticsRange range, bool includeBots, TextWriter writer, CancellationToken ct = default);
}

internal sealed class AnalyticsService(IDbContextFactory<AnalyticsDbContext> factory, IAnalyticsTracker tracker) : IAnalyticsService
{
    // A host name can never contain parentheses, so this cannot collide with a real referrer.
    private const string InternalMarker = "(internal)";

    private static IQueryable<PageView> In(AnalyticsDbContext db, AnalyticsRange range) =>
        db.PageViews.AsNoTracking().Where(v => !v.IsBot && v.VisitedAt >= range.From && v.VisitedAt < range.To);

    public async Task<AnalyticsSummary> GetSummaryAsync(AnalyticsRange range, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var views = In(db, range);

        var pageViews = await views.LongCountAsync(ct);
        if (pageViews == 0) return new AnalyticsSummary(0, 0, 0, 0, 0, 0, 0);

        var visitors = await views.Select(v => v.VisitorId).Distinct().LongCountAsync(ct);
        var notFound = await views.LongCountAsync(v => v.NotFound, ct);

        var sessions = await views
            .GroupBy(v => v.SessionId)
            .Select(g => new { Views = g.Count(), First = g.Min(v => v.VisitedAt), Last = g.Max(v => v.VisitedAt) })
            .ToListAsync(ct);

        var sessionCount = sessions.Count;
        var bounces = sessions.Count(s => s.Views == 1);
        var averageSeconds = sessionCount == 0 ? 0 : sessions.Average(s => (s.Last - s.First).TotalSeconds);

        return new AnalyticsSummary(
            pageViews,
            visitors,
            sessionCount,
            sessionCount == 0 ? 0 : (double)pageViews / sessionCount,
            sessionCount == 0 ? 0 : (double)bounces / sessionCount,
            averageSeconds,
            notFound);
    }

    public async Task<long> GetActiveVisitorsAsync(int minutes = 5, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var since = DateTime.UtcNow.AddMinutes(-Math.Max(1, minutes));
        return await db.PageViews.AsNoTracking()
            .Where(v => !v.IsBot && v.VisitedAt >= since)
            .Select(v => v.VisitorId).Distinct().LongCountAsync(ct);
    }

    public async Task<IReadOnlyList<AnalyticsTimePoint>> GetTimeSeriesAsync(AnalyticsRange range, int utcOffsetMinutes, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var views = In(db, range);
        var offset = TimeSpan.FromMinutes(utcOffsetMinutes);

        // Buckets are computed in the caller's local time (the offset shifts the timestamps before truncation) so a
        // "day" on the chart is a calendar day, then shifted back to UTC for the result.
        Dictionary<DateTime, (long Views, long Visitors)> buckets;
        if (range.Hourly)
        {
            var rows = await views
                .GroupBy(v => new { Day = v.VisitedAt.AddMinutes(utcOffsetMinutes).Date, Hour = v.VisitedAt.AddMinutes(utcOffsetMinutes).Hour })
                .Select(g => new { g.Key.Day, g.Key.Hour, Views = g.LongCount(), Visitors = g.Select(v => v.VisitorId).Distinct().LongCount() })
                .ToListAsync(ct);
            buckets = rows.ToDictionary(r => r.Day.AddHours(r.Hour) - offset, r => (r.Views, r.Visitors));
        }
        else
        {
            var rows = await views
                .GroupBy(v => v.VisitedAt.AddMinutes(utcOffsetMinutes).Date)
                .Select(g => new { Day = g.Key, Views = g.LongCount(), Visitors = g.Select(v => v.VisitorId).Distinct().LongCount() })
                .ToListAsync(ct);
            buckets = rows.ToDictionary(r => r.Day - offset, r => (r.Views, r.Visitors));
        }

        var result = new List<AnalyticsTimePoint>();
        var step = range.Hourly ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        var localStart = range.From + offset;
        var first = range.Hourly
            ? new DateTime(localStart.Year, localStart.Month, localStart.Day, localStart.Hour, 0, 0)
            : localStart.Date;
        for (var start = first - offset; start < range.To; start += step)
        {
            buckets.TryGetValue(start, out var b);
            result.Add(new AnalyticsTimePoint(DateTime.SpecifyKind(start, DateTimeKind.Utc), b.Views, b.Visitors));
        }
        return result;
    }

    public async Task<IReadOnlyList<AnalyticsPageStat>> GetPagesAsync(AnalyticsRange range, int take = 50, string? search = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var views = In(db, range);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            views = views.Where(v => v.Path.Contains(term) || (v.Title != null && v.Title.Contains(term)));
        }

        var rows = await views
            .GroupBy(v => v.Path)
            .Select(g => new
            {
                Path = g.Key,
                Title = g.Max(v => v.Title),
                Views = g.LongCount(),
                Visitors = g.Select(v => v.VisitorId).Distinct().LongCount(),
                NotFound = g.Max(v => v.NotFound ? 1 : 0)
            })
            .OrderByDescending(r => r.Views).ThenBy(r => r.Path)
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(ct);

        return rows.Select(r => new AnalyticsPageStat(r.Path, r.Title, r.Views, r.Visitors, r.NotFound == 1)).ToList();
    }

    public async Task<IReadOnlyList<AnalyticsReferrerStat>> GetReferrersAsync(AnalyticsRange range, int take = 50, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // Navigations inside the site carry the site's own host as referrer; they are reported as one "Internal" row.
        var rows = await In(db, range)
            .Select(v => new { Host = v.ReferrerHost != null && v.Host.Contains(v.ReferrerHost) ? InternalMarker : v.ReferrerHost, v.VisitorId })
            .GroupBy(v => v.Host)
            .Select(g => new { Host = g.Key, Views = g.LongCount(), Visitors = g.Select(v => v.VisitorId).Distinct().LongCount() })
            .OrderByDescending(r => r.Views)
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(ct);

        return rows
            .Select(r => r.Host == InternalMarker
                ? new AnalyticsReferrerStat(null, ReferrerChannels.Internal, r.Views, r.Visitors)
                : new AnalyticsReferrerStat(r.Host, ReferrerChannels.Classify(r.Host), r.Views, r.Visitors))
            .ToList();
    }

    public async Task<IReadOnlyList<AnalyticsCount>> GetCountriesAsync(AnalyticsRange range, int take = 50, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await In(db, range)
            .GroupBy(v => v.CountryCode)
            .Select(g => new { Key = g.Key, Label = g.Max(v => v.Country), Views = g.LongCount(), Visitors = g.Select(v => v.VisitorId).Distinct().LongCount() })
            .OrderByDescending(r => r.Views)
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(ct);
        return rows.Select(r => new AnalyticsCount(r.Key, r.Label, r.Views, r.Visitors)).ToList();
    }

    public async Task<IReadOnlyList<AnalyticsGeoPoint>> GetGeoPointsAsync(AnalyticsRange range, int take = 500, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await In(db, range)
            .Where(v => v.Latitude != null && v.Longitude != null)
            .GroupBy(v => new { v.Latitude, v.Longitude, v.City, v.Region, v.Country, v.CountryCode })
            .Select(g => new
            {
                g.Key.Latitude, g.Key.Longitude, g.Key.City, g.Key.Region, g.Key.Country, g.Key.CountryCode,
                Views = g.LongCount(),
                Visitors = g.Select(v => v.VisitorId).Distinct().LongCount()
            })
            .OrderByDescending(r => r.Views)
            .Take(Math.Clamp(take, 1, 5000))
            .ToListAsync(ct);

        return rows.Select(r => new AnalyticsGeoPoint(r.Latitude!.Value, r.Longitude!.Value, r.City, r.Region, r.Country, r.CountryCode, r.Views, r.Visitors)).ToList();
    }

    public Task<IReadOnlyList<AnalyticsCount>> GetBrowsersAsync(AnalyticsRange range, int take = 20, CancellationToken ct = default) =>
        GroupAsync(range, v => v.Browser, take, ct);

    public Task<IReadOnlyList<AnalyticsCount>> GetOperatingSystemsAsync(AnalyticsRange range, int take = 20, CancellationToken ct = default) =>
        GroupAsync(range, v => v.OperatingSystem, take, ct);

    public Task<IReadOnlyList<AnalyticsCount>> GetDevicesAsync(AnalyticsRange range, CancellationToken ct = default) =>
        GroupAsync(range, v => v.DeviceType, 10, ct);

    public Task<IReadOnlyList<AnalyticsCount>> GetLanguagesAsync(AnalyticsRange range, int take = 20, CancellationToken ct = default) =>
        GroupAsync(range, v => v.Language, take, ct);

    public Task<IReadOnlyList<AnalyticsCount>> GetHostsAsync(AnalyticsRange range, int take = 20, CancellationToken ct = default) =>
        GroupAsync(range, v => v.Host, take, ct);

    private async Task<IReadOnlyList<AnalyticsCount>> GroupAsync(AnalyticsRange range, Expression<Func<PageView, string?>> key, int take, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await In(db, range)
            .GroupBy(key)
            .Select(g => new { Key = g.Key, Views = g.LongCount(), Visitors = g.Select(v => v.VisitorId).Distinct().LongCount() })
            .OrderByDescending(r => r.Views)
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(ct);
        return rows.Select(r => new AnalyticsCount(r.Key, null, r.Views, r.Visitors)).ToList();
    }

    public async Task<AnalyticsViewPage> GetViewsAsync(AnalyticsViewFilter filter, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.PageViews.AsNoTracking().AsQueryable();

        if (!filter.IncludeBots) query = query.Where(v => !v.IsBot);
        if (filter.Range is { } range) query = query.Where(v => v.VisitedAt >= range.From && v.VisitedAt < range.To);
        if (!string.IsNullOrWhiteSpace(filter.Path))
        {
            var path = filter.Path.Trim();
            query = path.EndsWith('*') ? query.Where(v => v.Path.StartsWith(path.Substring(0, path.Length - 1))) : query.Where(v => v.Path == path);
        }
        if (!string.IsNullOrWhiteSpace(filter.VisitorId)) query = query.Where(v => v.VisitorId == filter.VisitorId.Trim());
        if (!string.IsNullOrWhiteSpace(filter.SessionId)) query = query.Where(v => v.SessionId == filter.SessionId.Trim());
        if (!string.IsNullOrWhiteSpace(filter.IpAddress)) query = query.Where(v => v.IpAddress == filter.IpAddress.Trim());
        if (!string.IsNullOrWhiteSpace(filter.CountryCode)) query = query.Where(v => v.CountryCode == filter.CountryCode.Trim().ToUpper());
        if (filter.NotFoundOnly) query = query.Where(v => v.NotFound);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(v =>
                v.Path.Contains(term) || (v.Title != null && v.Title.Contains(term)) || (v.IpAddress != null && v.IpAddress.Contains(term)) ||
                (v.City != null && v.City.Contains(term)) || (v.Country != null && v.Country.Contains(term)) ||
                (v.Referrer != null && v.Referrer.Contains(term)) || (v.Browser != null && v.Browser.Contains(term)) ||
                (v.OperatingSystem != null && v.OperatingSystem.Contains(term)) || v.VisitorId == term);
        }

        var total = await query.LongCountAsync(ct);
        var items = await query
            .OrderByDescending(v => v.VisitedAt).ThenByDescending(v => v.Id)
            .Skip(Math.Max(0, filter.Skip))
            .Take(Math.Clamp(filter.Take, 1, 500))
            .ToListAsync(ct);
        return new AnalyticsViewPage(items, total);
    }

    public async Task<AnalyticsDataInfo> GetDataInfoAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var all = db.PageViews.AsNoTracking();
        var total = await all.LongCountAsync(ct);
        var bots = await all.LongCountAsync(v => v.IsBot, ct);
        DateTime? oldest = total == 0 ? null : await all.MinAsync(v => v.VisitedAt, ct);
        DateTime? newest = total == 0 ? null : await all.MaxAsync(v => v.VisitedAt, ct);
        var geo = await db.GeoIpEntries.LongCountAsync(ct);
        var pending = await all.LongCountAsync(v => !v.GeoResolved, ct);
        return new AnalyticsDataInfo(total, bots, oldest, newest, geo, pending, tracker.QueueLength, tracker.Dropped);
    }

    public async Task<int> DeleteViewsAsync(DateTime? olderThanUtc, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.PageViews.AsQueryable();
        if (olderThanUtc is { } cutoff) query = query.Where(v => v.VisitedAt < cutoff);
        return await query.ExecuteDeleteAsync(ct);
    }

    public async Task<int> ClearGeoCacheAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.GeoIpEntries.ExecuteDeleteAsync(ct);
    }

    public async Task ExportCsvAsync(AnalyticsRange range, bool includeBots, TextWriter writer, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.PageViews.AsNoTracking().Where(v => v.VisitedAt >= range.From && v.VisitedAt < range.To);
        if (!includeBots) query = query.Where(v => !v.IsBot);

        await writer.WriteLineAsync("visitedAtUtc,host,path,query,title,contentId,culture,notFound,referrer,referrerHost,visitorId,sessionId,ipAddress,browser,browserVersion,operatingSystem,deviceType,isBot,language,countryCode,country,region,city,latitude,longitude,userAgent");
        await foreach (var v in query.OrderBy(v => v.VisitedAt).AsAsyncEnumerable().WithCancellation(ct))
        {
            var line = new StringBuilder(512);
            Append(line, v.VisitedAt.ToString("O", CultureInfo.InvariantCulture));
            Append(line, v.Host); Append(line, v.Path); Append(line, v.Query); Append(line, v.Title);
            Append(line, v.ContentId?.ToString()); Append(line, v.Culture); Append(line, v.NotFound ? "true" : "false");
            Append(line, v.Referrer); Append(line, v.ReferrerHost); Append(line, v.VisitorId); Append(line, v.SessionId);
            Append(line, v.IpAddress); Append(line, v.Browser); Append(line, v.BrowserVersion); Append(line, v.OperatingSystem);
            Append(line, v.DeviceType); Append(line, v.IsBot ? "true" : "false"); Append(line, v.Language);
            Append(line, v.CountryCode); Append(line, v.Country); Append(line, v.Region); Append(line, v.City);
            Append(line, v.Latitude?.ToString(CultureInfo.InvariantCulture)); Append(line, v.Longitude?.ToString(CultureInfo.InvariantCulture));
            Append(line, v.UserAgent, last: true);
            await writer.WriteLineAsync(line, ct);
        }

        static void Append(StringBuilder sb, string? value, bool last = false)
        {
            if (value is not null)
            {
                var needsQuotes = value.IndexOfAny([',', '"', '\n', '\r']) >= 0;
                if (needsQuotes) sb.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
                else sb.Append(value);
            }
            if (!last) sb.Append(',');
        }
    }
}
