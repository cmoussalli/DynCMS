using System.Text.RegularExpressions;

namespace DynCMS.Core.Analytics;

/// <summary>What a user agent string says about the client.</summary>
public sealed record UserAgentInfo(string Browser, string? BrowserVersion, string OperatingSystem, string DeviceType, bool IsBot)
{
    public static readonly UserAgentInfo Unknown = new("Unknown", null, "Unknown", DeviceTypes.Unknown, false);
}

/// <summary>
/// A small, dependency-free user agent parser: enough to name the browser family and major version, the operating
/// system, the device class and whether the client is a crawler or a script. It is not a full device database;
/// unknown clients come out as "Unknown" rather than wrong.
/// </summary>
public static partial class UserAgentParser
{
    private static readonly (Regex Pattern, string Name)[] Browsers =
    [
        (Re(@"EdgiOS/([\d.]+)"), "Edge"),
        (Re(@"EdgA?/([\d.]+)"), "Edge"),
        (Re(@"OPR/([\d.]+)"), "Opera"),
        (Re(@"Opera[ /]([\d.]+)"), "Opera"),
        (Re(@"SamsungBrowser/([\d.]+)"), "Samsung Internet"),
        (Re(@"Vivaldi/([\d.]+)"), "Vivaldi"),
        (Re(@"YaBrowser/([\d.]+)"), "Yandex Browser"),
        (Re(@"UCBrowser/([\d.]+)"), "UC Browser"),
        (Re(@"FxiOS/([\d.]+)"), "Firefox"),
        (Re(@"Firefox/([\d.]+)"), "Firefox"),
        (Re(@"CriOS/([\d.]+)"), "Chrome"),
        (Re(@"Chromium/([\d.]+)"), "Chromium"),
        (Re(@"Chrome/([\d.]+)"), "Chrome"),
        (Re(@"Version/([\d.]+).*Safari/"), "Safari"),
        (Re(@"Safari/([\d.]+)"), "Safari"),
        (Re(@"MSIE ([\d.]+)"), "Internet Explorer"),
        (Re(@"Trident/.*rv:([\d.]+)"), "Internet Explorer")
    ];

    private static readonly Regex BotPattern = Re(
        @"bot|crawl|spider|slurp|curl/|wget/|python-requests|python-urllib|httpclient|headless|lighthouse|pagespeed|" +
        @"facebookexternalhit|facebookcatalog|preview|monitor|scan|fetch|Go-http-client|Java/|okhttp|axios/|" +
        @"node-fetch|undici|Pingdom|UptimeRobot|Bytespider|GPTBot|ClaudeBot|Applebot|YandexBot|Baiduspider|DuckDuckBot|" +
        @"Sogou|Exabot|ia_archiver|Semrush|Ahrefs|MJ12|DotBot|PetalBot|archive\.org|WhatsApp|Slackbot|Discordbot|" +
        @"Twitterbot|LinkedInBot|Embedly|Quora Link Preview|Screaming Frog|dataminr|postman|insomnia|PowerShell");

    public static UserAgentInfo Parse(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return UserAgentInfo.Unknown with { IsBot = true, DeviceType = DeviceTypes.Bot };

        var ua = userAgent.Length > 1000 ? userAgent[..1000] : userAgent;
        var isBot = BotPattern.IsMatch(ua);

        string browser = "Unknown";
        string? version = null;
        foreach (var (pattern, name) in Browsers)
        {
            var m = pattern.Match(ua);
            if (!m.Success) continue;
            browser = name;
            version = MajorMinor(m.Groups[1].Value);
            break;
        }

        var os = DetectOs(ua);
        var device = isBot ? DeviceTypes.Bot : DetectDevice(ua);

        return new UserAgentInfo(browser, version, os, device, isBot);
    }

    private static string DetectOs(string ua)
    {
        if (ua.Contains("Windows Phone", StringComparison.OrdinalIgnoreCase)) return "Windows Phone";
        if (ua.Contains("Windows NT", StringComparison.OrdinalIgnoreCase) || ua.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows";
        if (ua.Contains("CrOS", StringComparison.Ordinal)) return "Chrome OS";
        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)) return "Android";
        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPod", StringComparison.OrdinalIgnoreCase)) return "iOS";
        if (ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) || ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)) return "macOS";
        if (ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) || ua.Contains("X11", StringComparison.Ordinal)) return "Linux";
        if (ua.Contains("PlayStation", StringComparison.OrdinalIgnoreCase)) return "PlayStation";
        if (ua.Contains("Xbox", StringComparison.OrdinalIgnoreCase)) return "Xbox";
        return "Unknown";
    }

    private static string DetectDevice(string ua)
    {
        if (ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) || ua.Contains("Tablet", StringComparison.OrdinalIgnoreCase) ||
            ua.Contains("Kindle", StringComparison.OrdinalIgnoreCase) || ua.Contains("Silk/", StringComparison.OrdinalIgnoreCase) ||
            ua.Contains("PlayBook", StringComparison.OrdinalIgnoreCase))
            return DeviceTypes.Tablet;
        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase) && !ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase))
            return DeviceTypes.Tablet;
        if (ua.Contains("Mobi", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
            ua.Contains("iPod", StringComparison.OrdinalIgnoreCase) || ua.Contains("Windows Phone", StringComparison.OrdinalIgnoreCase) ||
            ua.Contains("BlackBerry", StringComparison.OrdinalIgnoreCase) || ua.Contains("Opera Mini", StringComparison.OrdinalIgnoreCase))
            return DeviceTypes.Mobile;
        return DeviceTypes.Desktop;
    }

    private static string MajorMinor(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : parts[0];
    }

    private static Regex Re(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
