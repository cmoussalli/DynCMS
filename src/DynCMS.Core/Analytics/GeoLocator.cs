using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DynCMS.Core.Analytics;

/// <summary>Where an IP address is, as far as a geolocation service knows.</summary>
public sealed record GeoLocation(
    string? CountryCode,
    string? Country,
    string? Region,
    string? City,
    double? Latitude,
    double? Longitude,
    string? Timezone,
    string? Organization)
{
    public bool HasCoordinates => Latitude is not null && Longitude is not null;
    public bool IsEmpty => CountryCode is null && Country is null && !HasCoordinates;
}

/// <summary>
/// Resolves an IP address to a location. The built-in implementation calls an HTTP JSON service; replace it
/// (register your own before <c>AddDynCms</c>) to use an offline database such as MaxMind GeoLite2.
/// </summary>
public interface IGeoLocator
{
    /// <summary>The location of <paramref name="ipAddress"/>, or null when the service could not tell.</summary>
    Task<GeoLocation?> LookupAsync(string ipAddress, string serviceUrl, CancellationToken ct = default);
}

/// <summary>IP address helpers shared by the tracker and the worker.</summary>
public static class IpAddressHelper
{
    /// <summary>True for loopback, link-local, private-range and unspecified addresses, which no service can locate.</summary>
    public static bool IsPrivate(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out var address)) return true;
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || b[0] == 0;
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast;
    }

    /// <summary>Zeroes the last octet of an IPv4 address or the last 80 bits of an IPv6 address.</summary>
    public static string? Anonymize(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out var address)) return ip;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            bytes[3] = 0;
        }
        else
        {
            for (var i = 6; i < bytes.Length; i++) bytes[i] = 0;
        }
        return new IPAddress(bytes).ToString();
    }

    /// <summary>Normalises the textual form (IPv4-mapped IPv6 becomes plain IPv4).</summary>
    public static string? Normalize(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip.Trim(), out var address)) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }
}

/// <summary>
/// Looks an address up at a JSON HTTP service and reads the answer for the field names the common free services use,
/// so switching between GeoJS, ip-api.com, ipapi.co, ipwho.is and friends is a matter of changing the URL.
/// </summary>
internal sealed class HttpGeoLocator(IHttpClientFactory httpClientFactory, ILogger<HttpGeoLocator> logger) : IGeoLocator
{
    public const string HttpClientName = "DynCMS.GeoLookup";

    private static readonly string[] CountryCodeKeys = ["country_code", "countryCode", "country_code2", "countryCode2", "country_iso_code"];
    private static readonly string[] CountryKeys = ["country_name", "countryName", "country"];
    private static readonly string[] RegionKeys = ["region", "regionName", "region_name", "state", "state_prov", "subdivision"];
    private static readonly string[] CityKeys = ["city", "city_name"];
    private static readonly string[] LatitudeKeys = ["latitude", "lat"];
    private static readonly string[] LongitudeKeys = ["longitude", "lon", "lng", "long"];
    private static readonly string[] TimezoneKeys = ["timezone", "time_zone", "tz"];
    private static readonly string[] OrganizationKeys = ["organization_name", "organization", "org", "isp", "as_name", "connection"];

    public async Task<GeoLocation?> LookupAsync(string ipAddress, string serviceUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serviceUrl) || !serviceUrl.Contains("{ip}", StringComparison.OrdinalIgnoreCase)) return null;
        var url = serviceUrl.Replace("{ip}", Uri.EscapeDataString(ipAddress), StringComparison.OrdinalIgnoreCase);

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Geolocation lookup of {Ip} answered {Status}", ipAddress, (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // ip-api.com: {"status":"fail"}; ipwho.is: {"success":false}; ipapi.co: {"error":true}
            if (TryGetString(root, "status") is { } status && status.Equals("fail", StringComparison.OrdinalIgnoreCase)) return null;
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) return null;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True) return null;

            var location = new GeoLocation(
                Upper(First(root, CountryCodeKeys)),
                First(root, CountryKeys),
                First(root, RegionKeys),
                First(root, CityKeys),
                FirstDouble(root, LatitudeKeys),
                FirstDouble(root, LongitudeKeys),
                First(root, TimezoneKeys),
                First(root, OrganizationKeys));

            if (location.CountryCode is { Length: not 2 }) location = location with { CountryCode = null };
            return location.IsEmpty ? null : location;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Geolocation lookup of {Ip} failed", ipAddress);
            return null;
        }
    }

    private static string? First(JsonElement root, string[] keys)
    {
        foreach (var key in keys)
        {
            var value = TryGetString(root, key);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    private static double? FirstDouble(JsonElement root, string[] keys)
    {
        foreach (var key in keys)
        {
            if (!TryGetProperty(root, key, out var element)) continue;
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var d)) return d;
            if (element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }
        return null;
    }

    private static string? TryGetString(JsonElement root, string key)
    {
        if (!TryGetProperty(root, key, out var element)) return null;
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            // Nested objects such as ipwho.is "timezone": {"id": "Europe/Berlin"} or "connection": {"org": "..."}
            JsonValueKind.Object => TryGetString(element, "id") ?? TryGetString(element, "name") ?? TryGetString(element, "org") ?? TryGetString(element, "isp"),
            _ => null
        };
    }

    private static bool TryGetProperty(JsonElement root, string key, out JsonElement element)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                element = property.Value;
                return true;
            }
        }
        element = default;
        return false;
    }

    private static string? Upper(string? value) => value?.ToUpperInvariant();
}
