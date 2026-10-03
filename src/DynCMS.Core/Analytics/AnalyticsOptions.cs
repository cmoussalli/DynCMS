namespace DynCMS.Core.Analytics;

/// <summary>
/// The analytics settings an administrator can change in the back office (Analytics → Settings). Stored as JSON
/// in the <c>Settings</c> table; <see cref="DynCmsAnalyticsOptions.Defaults"/> applies until they are saved.
/// </summary>
public sealed class AnalyticsSettings
{
    /// <summary>Record page views at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Keep the visitor's IP address on each page view. When off, only the pseudonymous visitor id is stored.</summary>
    public bool StoreIpAddress { get; set; } = true;

    /// <summary>Zero the last octet of IPv4 addresses (last 80 bits of IPv6) before storing them, like Google Analytics' IP anonymisation.</summary>
    public bool AnonymizeIp { get; set; }

    /// <summary>Record the page views of signed-in back-office users (editors browsing their own site).</summary>
    public bool TrackSignedInUsers { get; set; }

    /// <summary>Store page views made by crawlers and scripts. They are flagged and left out of the reports either way.</summary>
    public bool TrackBots { get; set; }

    /// <summary>Delete page views older than this many days. 0 keeps them forever.</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>Look up the country, region, city and coordinates of visitor IP addresses (needed for the map).</summary>
    public bool GeoLookupEnabled { get; set; } = true;

    /// <summary>
    /// The lookup service. <c>{ip}</c> is replaced by the address. The JSON answer is read for the usual field names
    /// (<c>country</c>/<c>country_name</c>, <c>country_code</c>/<c>countryCode</c>, <c>region</c>/<c>regionName</c>,
    /// <c>city</c>, <c>latitude</c>/<c>lat</c>, <c>longitude</c>/<c>lon</c>, <c>timezone</c>, <c>organization_name</c>/<c>org</c>/<c>isp</c>),
    /// so GeoJS, ip-api.com, ipapi.co, ipwho.is and similar services work without code. Visitor addresses are sent to it.
    /// </summary>
    public string GeoLookupUrl { get; set; } = "https://get.geojs.io/v1/ip/geo/{ip}.json";

    /// <summary>Read the real client address from <c>X-Forwarded-For</c>, <c>CF-Connecting-IP</c> or <c>X-Real-IP</c> when a reverse proxy sets them.</summary>
    public bool TrustProxyHeaders { get; set; } = true;

    /// <summary>Site paths (or prefixes ending in <c>*</c>) that are never recorded, one per entry, for example <c>/health</c> or <c>/internal/*</c>.</summary>
    public List<string> ExcludedPaths { get; set; } = [];

    public AnalyticsSettings Clone() => new()
    {
        Enabled = Enabled,
        StoreIpAddress = StoreIpAddress,
        AnonymizeIp = AnonymizeIp,
        TrackSignedInUsers = TrackSignedInUsers,
        TrackBots = TrackBots,
        RetentionDays = RetentionDays,
        GeoLookupEnabled = GeoLookupEnabled,
        GeoLookupUrl = GeoLookupUrl,
        TrustProxyHeaders = TrustProxyHeaders,
        ExcludedPaths = [.. ExcludedPaths]
    };

    /// <summary>True when <paramref name="path"/> matches one of <see cref="ExcludedPaths"/>.</summary>
    public bool IsExcluded(string path)
    {
        foreach (var rule in ExcludedPaths)
        {
            var r = rule.Trim();
            if (r.Length == 0) continue;
            if (r.EndsWith('*'))
            {
                if (path.StartsWith(r[..^1], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(path, r, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(path.TrimEnd('/'), r.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Analytics configuration from <c>appsettings.json</c> (<c>DynCms:Analytics</c>): the defaults for the editable
/// settings plus the knobs that are not meant to change at runtime.
/// </summary>
public sealed class DynCmsAnalyticsOptions
{
    /// <summary>The settings used until an administrator saves their own in the back office.</summary>
    public AnalyticsSettings Defaults { get; set; } = new();

    /// <summary>A session ends after this long without a page view.</summary>
    public int SessionTimeoutMinutes { get; set; } = 30;

    /// <summary>How long a cached IP geolocation is trusted before it is looked up again.</summary>
    public int GeoCacheDays { get; set; } = 30;

    /// <summary>Pause between two geolocation lookups, to stay inside free-tier rate limits.</summary>
    public int GeoLookupDelayMilliseconds { get; set; } = 250;

    /// <summary>Page views waiting to be written when the database is slow; older ones are dropped beyond this.</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>The tile server of the map in the back office (Leaflet URL template).</summary>
    public string MapTileUrl { get; set; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    /// <summary>Attribution shown on the map, as required by the tile provider.</summary>
    public string MapTileAttribution { get; set; } = "&copy; <a href=\"https://www.openstreetmap.org/copyright\">OpenStreetMap</a> contributors";
}
