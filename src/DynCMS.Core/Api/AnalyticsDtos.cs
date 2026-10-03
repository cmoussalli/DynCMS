using DynCMS.Core.Analytics;

namespace DynCMS.Core.Api;

// Wire model of the analytics part of the management API and the MCP tools (see Dtos.cs for the rest).

public sealed record AnalyticsRangeDto(DateTime FromUtc, DateTime ToUtc, string Granularity);

public sealed record AnalyticsSummaryDto(
    long PageViews,
    long Visitors,
    long Sessions,
    double PagesPerSession,
    double BounceRate,
    double AverageSessionSeconds,
    long NotFoundViews)
{
    public static AnalyticsSummaryDto From(AnalyticsSummary s) =>
        new(s.PageViews, s.Visitors, s.Sessions, Math.Round(s.PagesPerSession, 2), Math.Round(s.BounceRate, 4), Math.Round(s.AverageSessionSeconds, 1), s.NotFoundViews);
}

public sealed record AnalyticsTimePointDto(DateTime StartUtc, long PageViews, long Visitors);

public sealed record AnalyticsCountDto(string? Key, string? Label, long PageViews, long Visitors);

public sealed record AnalyticsPageDto(string Path, string? Title, long PageViews, long Visitors, bool NotFound);

public sealed record AnalyticsReferrerDto(string? Host, string Channel, long PageViews, long Visitors);

/// <summary>The overview report: headline numbers with the previous period for comparison, the chart and the top lists.</summary>
public sealed record AnalyticsReportDto(
    AnalyticsRangeDto Range,
    AnalyticsSummaryDto Summary,
    AnalyticsSummaryDto Previous,
    long ActiveVisitors,
    IReadOnlyList<AnalyticsTimePointDto> Series,
    IReadOnlyList<AnalyticsPageDto> TopPages,
    IReadOnlyList<AnalyticsReferrerDto> Referrers,
    IReadOnlyList<AnalyticsCountDto> Countries,
    IReadOnlyList<AnalyticsCountDto> Browsers,
    IReadOnlyList<AnalyticsCountDto> OperatingSystems,
    IReadOnlyList<AnalyticsCountDto> Devices,
    IReadOnlyList<AnalyticsCountDto> Languages);

/// <summary>One row of the page view log.</summary>
public sealed record PageViewDto(
    long Id,
    DateTime VisitedAtUtc,
    string Host,
    string Path,
    string? Query,
    string? Title,
    Guid? ContentId,
    string? Culture,
    bool NotFound,
    string? Referrer,
    string? ReferrerHost,
    string VisitorId,
    string SessionId,
    string? IpAddress,
    string? Browser,
    string? BrowserVersion,
    string? OperatingSystem,
    string DeviceType,
    bool IsBot,
    string? Language,
    string? CountryCode,
    string? Country,
    string? Region,
    string? City,
    double? Latitude,
    double? Longitude)
{
    public static PageViewDto From(PageView v) => new(
        v.Id, v.VisitedAt, v.Host, v.Path, v.Query, v.Title, v.ContentId, v.Culture, v.NotFound, v.Referrer, v.ReferrerHost,
        v.VisitorId, v.SessionId, v.IpAddress, v.Browser, v.BrowserVersion, v.OperatingSystem, v.DeviceType, v.IsBot, v.Language,
        v.CountryCode, v.Country, v.Region, v.City, v.Latitude, v.Longitude);
}

public sealed record PageViewLogDto(long Total, int Skip, int Take, IReadOnlyList<PageViewDto> Items);

public sealed record AnalyticsSettingsDto(
    bool Enabled,
    bool StoreIpAddress,
    bool AnonymizeIp,
    bool TrackSignedInUsers,
    bool TrackBots,
    int RetentionDays,
    bool GeoLookupEnabled,
    string GeoLookupUrl,
    bool TrustProxyHeaders,
    IReadOnlyList<string> ExcludedPaths,
    bool IsCustomized)
{
    public static AnalyticsSettingsDto From(AnalyticsSettings s, bool customized) => new(
        s.Enabled, s.StoreIpAddress, s.AnonymizeIp, s.TrackSignedInUsers, s.TrackBots, s.RetentionDays,
        s.GeoLookupEnabled, s.GeoLookupUrl, s.TrustProxyHeaders, s.ExcludedPaths, customized);
}

/// <summary>Partial update of the analytics settings: only the fields sent are changed.</summary>
public sealed record UpdateAnalyticsSettingsRequest(
    bool? Enabled = null,
    bool? StoreIpAddress = null,
    bool? AnonymizeIp = null,
    bool? TrackSignedInUsers = null,
    bool? TrackBots = null,
    int? RetentionDays = null,
    bool? GeoLookupEnabled = null,
    string? GeoLookupUrl = null,
    bool? TrustProxyHeaders = null,
    IReadOnlyList<string>? ExcludedPaths = null);

public sealed record AnalyticsDataInfoDto(
    long TotalViews,
    long BotViews,
    DateTime? OldestUtc,
    DateTime? NewestUtc,
    long GeoCacheEntries,
    long ViewsAwaitingGeo,
    int QueueLength,
    long Dropped);
