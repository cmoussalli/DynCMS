namespace DynCMS.Core.Analytics;

/// <summary>Device classes a page view is attributed to, derived from the user agent.</summary>
public static class DeviceTypes
{
    public const string Desktop = "Desktop";
    public const string Mobile = "Mobile";
    public const string Tablet = "Tablet";
    public const string Bot = "Bot";
    public const string Unknown = "Unknown";
}

/// <summary>
/// One page view on the public site: what was requested, by whom (pseudonymous visitor and session ids), from where
/// (IP address, geolocation) and with what (browser, operating system, device). Written by the analytics worker,
/// read by the back office Analytics section, the API and the MCP tools. Table <c>AnalyticsPageViews</c>.
/// </summary>
public class PageView
{
    public long Id { get; set; }

    /// <summary>When the page was viewed (UTC).</summary>
    public DateTime VisitedAt { get; set; }

    /// <summary>Host header of the request, for sites served under several domains.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The site path, for example <c>/blog/my-post</c> (no query string).</summary>
    public string Path { get; set; } = "/";

    /// <summary>The query string, without the leading <c>?</c>, when there was one.</summary>
    public string? Query { get; set; }

    /// <summary>The name of the content that rendered, when the path resolved.</summary>
    public string? Title { get; set; }

    /// <summary>Id of the content node that rendered, when the path resolved.</summary>
    public Guid? ContentId { get; set; }

    /// <summary>ISO code of the language the page was served in.</summary>
    public string? Culture { get; set; }

    /// <summary>True when nothing was published at the path (the visitor saw the not-found page).</summary>
    public bool NotFound { get; set; }

    /// <summary>
    /// Where the visitor came from: the <c>Referer</c> header of the first request, or the previous page of the site
    /// for navigations inside a Blazor circuit (then <see cref="ReferrerHost"/> equals <see cref="Host"/>).
    /// </summary>
    public string? Referrer { get; set; }

    /// <summary>Host of <see cref="Referrer"/>, lower case, without <c>www.</c>.</summary>
    public string? ReferrerHost { get; set; }

    /// <summary>Pseudonymous visitor id: a keyed hash of IP address and user agent. Never reversible to the IP.</summary>
    public string VisitorId { get; set; } = string.Empty;

    /// <summary>Groups the page views of one visit: a new session starts after 30 minutes without a page view.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>The client IP address, possibly anonymised (last octet / last 80 bits zeroed) or omitted per settings.</summary>
    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }
    public string? Browser { get; set; }
    public string? BrowserVersion { get; set; }
    public string? OperatingSystem { get; set; }

    /// <summary>One of <see cref="DeviceTypes"/>.</summary>
    public string DeviceType { get; set; } = DeviceTypes.Unknown;

    /// <summary>True when the user agent looks like a crawler or a script. Bots are excluded from every report.</summary>
    public bool IsBot { get; set; }

    /// <summary>The visitor's preferred language (first entry of <c>Accept-Language</c>), for example <c>de-DE</c>.</summary>
    public string? Language { get; set; }

    public string? CountryCode { get; set; }
    public string? Country { get; set; }
    public string? Region { get; set; }
    public string? City { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>True once the geolocation lookup for <see cref="IpAddress"/> has run (successfully or not).</summary>
    public bool GeoResolved { get; set; }
}

/// <summary>
/// Cached geolocation of one IP address so the lookup service is asked once per address. Table <c>AnalyticsGeoIp</c>.
/// Entries expire (see <see cref="DynCmsAnalyticsOptions.GeoCacheDays"/>) because addresses move between networks.
/// </summary>
public class GeoIpEntry
{
    public string IpAddress { get; set; } = string.Empty;
    public DateTime ResolvedAt { get; set; }

    /// <summary>False when the lookup failed or returned nothing usable; the failure is cached briefly too.</summary>
    public bool Success { get; set; }

    public string? CountryCode { get; set; }
    public string? Country { get; set; }
    public string? Region { get; set; }
    public string? City { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Timezone { get; set; }
    public string? Organization { get; set; }
}
