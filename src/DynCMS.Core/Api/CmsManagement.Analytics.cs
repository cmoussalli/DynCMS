using System.Globalization;
using DynCMS.Core.Analytics;

namespace DynCMS.Core.Api;

public sealed partial class CmsManagement
{
    #region Analytics

    /// <summary>
    /// The overview report for a period: <paramref name="period"/> is <c>today</c>, <c>yesterday</c>, <c>7d</c>,
    /// <c>30d</c>, <c>90d</c> or <c>12m</c>; or pass <paramref name="from"/> and <paramref name="to"/> as local dates
    /// (<c>yyyy-MM-dd</c>, inclusive). Default: the last 30 days.
    /// </summary>
    public async Task<AnalyticsReportDto> GetAnalyticsReportAsync(string? period, string? from, string? to, CancellationToken ct)
    {
        RequireAnalytics();
        var range = ResolveRange(period, from, to);
        var offset = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes;

        var summary = await analytics.GetSummaryAsync(range, ct);
        var previous = await analytics.GetSummaryAsync(range.Previous(), ct);
        var active = await analytics.GetActiveVisitorsAsync(5, ct);
        var series = await analytics.GetTimeSeriesAsync(range, offset, ct);
        var pages = await analytics.GetPagesAsync(range, 10, null, ct);
        var referrers = await analytics.GetReferrersAsync(range, 10, ct);
        var countries = await analytics.GetCountriesAsync(range, 10, ct);
        var browsers = await analytics.GetBrowsersAsync(range, 5, ct);
        var os = await analytics.GetOperatingSystemsAsync(range, 5, ct);
        var devices = await analytics.GetDevicesAsync(range, ct);
        var languages = await analytics.GetLanguagesAsync(range, 5, ct);

        return new AnalyticsReportDto(
            new AnalyticsRangeDto(range.From, range.To, range.Hourly ? "hour" : "day"),
            AnalyticsSummaryDto.From(summary),
            AnalyticsSummaryDto.From(previous),
            active,
            series.Select(p => new AnalyticsTimePointDto(p.StartUtc, p.PageViews, p.Visitors)).ToList(),
            pages.Select(MapPage).ToList(),
            referrers.Select(r => new AnalyticsReferrerDto(r.Host, r.Channel, r.PageViews, r.Visitors)).ToList(),
            countries.Select(MapCount).ToList(),
            browsers.Select(MapCount).ToList(),
            os.Select(MapCount).ToList(),
            devices.Select(MapCount).ToList(),
            languages.Select(MapCount).ToList());
    }

    public async Task<IReadOnlyList<AnalyticsPageDto>> GetAnalyticsPagesAsync(string? period, string? from, string? to, int? take, string? search, CancellationToken ct)
    {
        RequireAnalytics();
        var range = ResolveRange(period, from, to);
        var pages = await analytics.GetPagesAsync(range, take ?? 50, search, ct);
        return pages.Select(MapPage).ToList();
    }

    public async Task<PageViewLogDto> GetAnalyticsViewsAsync(
        string? period, string? from, string? to, string? path, string? visitorId, string? sessionId, string? ip, string? country,
        string? search, bool? notFound, bool? bots, int? skip, int? take, CancellationToken ct)
    {
        RequireAnalytics();
        var range = period is null && from is null && to is null ? null : ResolveRange(period, from, to);
        var filter = new AnalyticsViewFilter(range, path, visitorId, sessionId, ip, country, search, notFound == true, bots == true, skip ?? 0, take ?? 50);
        var page = await analytics.GetViewsAsync(filter, ct);
        return new PageViewLogDto(page.Total, filter.Skip, Math.Clamp(filter.Take, 1, 500), page.Items.Select(PageViewDto.From).ToList());
    }

    public AnalyticsSettingsDto GetAnalyticsSettings()
    {
        access.RequireAdmin(ApiScopes.AnalyticsManage);
        return AnalyticsSettingsDto.From(analyticsSettings.Current, analyticsSettings.IsCustomized);
    }

    public async Task<AnalyticsSettingsDto> UpdateAnalyticsSettingsAsync(UpdateAnalyticsSettingsRequest request, CancellationToken ct)
    {
        access.RequireAdmin(ApiScopes.AnalyticsManage);
        var s = analyticsSettings.Current.Clone();
        if (request.Enabled is { } enabled) s.Enabled = enabled;
        if (request.StoreIpAddress is { } storeIp) s.StoreIpAddress = storeIp;
        if (request.AnonymizeIp is { } anonymize) s.AnonymizeIp = anonymize;
        if (request.TrackSignedInUsers is { } signedIn) s.TrackSignedInUsers = signedIn;
        if (request.TrackBots is { } bots) s.TrackBots = bots;
        if (request.RetentionDays is { } retention) s.RetentionDays = retention;
        if (request.GeoLookupEnabled is { } geo) s.GeoLookupEnabled = geo;
        if (request.GeoLookupUrl is { } url) s.GeoLookupUrl = url;
        if (request.TrustProxyHeaders is { } proxy) s.TrustProxyHeaders = proxy;
        if (request.ExcludedPaths is { } excluded) s.ExcludedPaths = [.. excluded];
        await Guard(() => analyticsSettings.SaveAsync(s, ct));
        return AnalyticsSettingsDto.From(analyticsSettings.Current, analyticsSettings.IsCustomized);
    }

    public async Task<AnalyticsDataInfoDto> GetAnalyticsDataInfoAsync(CancellationToken ct)
    {
        access.RequireAdmin(ApiScopes.AnalyticsManage);
        var info = await analytics.GetDataInfoAsync(ct);
        return new AnalyticsDataInfoDto(info.TotalViews, info.BotViews, info.OldestUtc, info.NewestUtc, info.GeoCacheEntries, info.ViewsAwaitingGeo, info.QueueLength, info.Dropped);
    }

    /// <summary>Deletes page views older than <paramref name="olderThanDays"/> days, or every page view when null.</summary>
    public async Task<int> DeleteAnalyticsViewsAsync(int? olderThanDays, CancellationToken ct)
    {
        access.RequireAdmin(ApiScopes.AnalyticsManage);
        if (olderThanDays is < 0) throw CmsApiException.BadRequest("olderThanDays must be zero or positive.");
        var cutoff = olderThanDays is { } days ? DateTime.UtcNow.AddDays(-days) : (DateTime?)null;
        return await analytics.DeleteViewsAsync(cutoff, ct);
    }

    private CmsCaller RequireAnalytics()
    {
        var caller = access.Require();
        if (caller.IsApiKey && !ApiScopes.Covers(caller.Scopes, ApiScopes.AnalyticsRead) && !ApiScopes.Covers(caller.Scopes, ApiScopes.AnalyticsManage))
            throw CmsApiException.Forbidden($"This API key does not have the '{ApiScopes.AnalyticsRead}' scope.");
        return caller;
    }

    private static AnalyticsRange ResolveRange(string? period, string? from, string? to)
    {
        if (!string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(to))
        {
            if (!TryParseDate(from, out var first)) throw CmsApiException.BadRequest("from must be a date (yyyy-MM-dd).");
            if (!TryParseDate(to, out var last)) last = first;
            if ((last - first).TotalDays > 3660) throw CmsApiException.BadRequest("The range may span at most ten years.");
            return AnalyticsRange.Days(first, last);
        }

        return (period ?? "30d").Trim().ToLowerInvariant() switch
        {
            "today" or "1d" => AnalyticsRange.LastDays(1),
            "yesterday" => Shift(AnalyticsRange.LastDays(1)),
            "7d" or "week" => AnalyticsRange.LastDays(7),
            "30d" or "month" => AnalyticsRange.LastDays(30),
            "90d" or "quarter" => AnalyticsRange.LastDays(90),
            "12m" or "365d" or "year" => AnalyticsRange.LastDays(365),
            _ => throw CmsApiException.BadRequest("period must be one of today, yesterday, 7d, 30d, 90d, 12m — or pass from/to dates.")
        };

        static AnalyticsRange Shift(AnalyticsRange r) => new(r.From - r.Length, r.From);
        static bool TryParseDate(string? value, out DateTime date) =>
            DateTime.TryParseExact(value, ["yyyy-MM-dd", "yyyy-M-d"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out date);
    }

    private static AnalyticsPageDto MapPage(AnalyticsPageStat p) => new(p.Path, p.Title, p.PageViews, p.Visitors, p.NotFound);
    private static AnalyticsCountDto MapCount(AnalyticsCount c) => new(c.Key, c.Label, c.PageViews, c.Visitors);

    #endregion
}
