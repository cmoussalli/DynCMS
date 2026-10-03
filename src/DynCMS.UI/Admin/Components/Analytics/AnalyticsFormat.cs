using System.Globalization;
using DynCMS.Core.Analytics;

namespace DynCMS.UI.Admin.Components.Analytics;

/// <summary>One row of an <see cref="AnalyticsBarList"/>.</summary>
public sealed record AnalyticsBarItem(
    string Label,
    long Value,
    long? Visitors = null,
    string? Detail = null,
    string? Icon = null,
    string? Prefix = null,
    string? Href = null,
    string? Badge = null,
    string? BadgeClass = null);

/// <summary>Formatting helpers shared by the Analytics section.</summary>
public static class AnalyticsFormat
{
    public static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Number(double value) => value >= 100 ? value.ToString("N0") : value.ToString("0.#");

    /// <summary>12,345 → "12.3k", 1,234,567 → "1.2M".</summary>
    public static string Compact(long value) => value switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000d).ToString("0.#") + "B",
        >= 1_000_000 => (value / 1_000_000d).ToString("0.#") + "M",
        >= 10_000 => (value / 1_000d).ToString("0.#") + "k",
        _ => value.ToString("N0")
    };

    public static string Percent(double ratio) => (ratio * 100).ToString("0.#") + "%";

    public static string Share(long part, long total) => total == 0 ? "0%" : Percent((double)part / total);

    public static string Duration(double seconds)
    {
        if (seconds < 1) return "0s";
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{span.Minutes}m {span.Seconds}s";
        return $"{span.Seconds}s";
    }

    /// <summary>Change from the previous period as text and a class (<c>dc-delta-up</c>, <c>dc-delta-down</c>, <c>dc-delta-flat</c>).</summary>
    public static (string Text, string Class) Delta(double current, double previous, bool lowerIsBetter = false)
    {
        if (previous == 0 && current == 0) return ("—", "dc-delta-flat");
        if (previous == 0) return ("new", "dc-delta-up");
        var change = (current - previous) / previous;
        if (Math.Abs(change) < 0.0005) return ("0%", "dc-delta-flat");
        var up = change > 0;
        var good = lowerIsBetter ? !up : up;
        return ((up ? "+" : "−") + Percent(Math.Abs(change)), good ? "dc-delta-up" : "dc-delta-down");
    }

    /// <summary>The flag emoji of an ISO 3166-1 alpha-2 code (two regional indicator symbols).</summary>
    public static string Flag(string? countryCode)
    {
        if (countryCode is not { Length: 2 }) return "🌐";
        var upper = countryCode.ToUpperInvariant();
        if (upper[0] < 'A' || upper[0] > 'Z' || upper[1] < 'A' || upper[1] > 'Z') return "🌐";
        return char.ConvertFromUtf32(0x1F1E6 + upper[0] - 'A') + char.ConvertFromUtf32(0x1F1E6 + upper[1] - 'A');
    }

    public static string CountryName(string? countryCode, string? fallback = null)
    {
        if (countryCode is not { Length: 2 }) return fallback ?? "Unknown";
        try
        {
            return new RegionInfo(countryCode.ToUpperInvariant()).EnglishName;
        }
        catch (ArgumentException)
        {
            return fallback ?? countryCode.ToUpperInvariant();
        }
    }

    public static string LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "Unknown";
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            return culture.EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>City, region and country on one line, as far as they are known.</summary>
    public static string Place(string? city, string? region, string? country, string? countryCode)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(city)) parts.Add(city);
        if (!string.IsNullOrWhiteSpace(region) && !string.Equals(region, city, StringComparison.OrdinalIgnoreCase)) parts.Add(region);
        var countryName = country ?? (countryCode is null ? null : CountryName(countryCode));
        if (!string.IsNullOrWhiteSpace(countryName)) parts.Add(countryName);
        return parts.Count == 0 ? "Unknown location" : string.Join(", ", parts);
    }

    public static string Local(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy, HH:mm:ss");

    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalSeconds < 5) return "just now";
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s ago";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} d ago";
        return utc.ToLocalTime().ToString("d MMM yyyy");
    }

    public static string DeviceIcon(string? deviceType) => deviceType switch
    {
        DeviceTypes.Mobile => "smartphone",
        DeviceTypes.Tablet => "tablet",
        DeviceTypes.Bot => "cpu",
        DeviceTypes.Desktop => "monitor",
        _ => "help-circle"
    };

    public static string ChannelClass(string channel) => channel switch
    {
        ReferrerChannels.Search => "dc-badge-info",
        ReferrerChannels.Social => "dc-badge-violet",
        ReferrerChannels.Referral => "dc-badge-success",
        ReferrerChannels.Internal => "dc-badge-muted",
        _ => "dc-badge-warn"
    };

    public static string ChannelIcon(string channel) => channel switch
    {
        ReferrerChannels.Search => "search",
        ReferrerChannels.Social => "users",
        ReferrerChannels.Referral => "link",
        ReferrerChannels.Internal => "layers",
        _ => "mouse-pointer"
    };

    public static string RangeLabel(AnalyticsRange range)
    {
        var from = range.From.ToLocalTime();
        var to = range.To.AddSeconds(-1).ToLocalTime();
        if (from.Date == to.Date) return from.ToString("d MMM yyyy");
        return from.Year == to.Year
            ? $"{from:d MMM} – {to:d MMM yyyy}"
            : $"{from:d MMM yyyy} – {to:d MMM yyyy}";
    }
}
