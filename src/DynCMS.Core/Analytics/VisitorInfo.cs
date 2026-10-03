using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;

namespace DynCMS.Core.Analytics;

/// <summary>What a request tells about the visitor, captured once per HTTP request or Blazor circuit.</summary>
public sealed record VisitorInfo(
    string? IpAddress,
    string? UserAgent,
    string? Language,
    string? Referrer,
    string Host,
    string Scheme,
    bool IsAuthenticated)
{
    private static readonly string[] ProxyHeaders = ["CF-Connecting-IP", "X-Real-IP", "X-Forwarded-For"];

    /// <summary>Reads the visitor from a request. Returns null when there is no request.</summary>
    public static VisitorInfo? FromHttpContext(HttpContext? http, bool trustProxyHeaders)
    {
        if (http is null) return null;
        var request = http.Request;

        string? ip = null;
        if (trustProxyHeaders)
        {
            foreach (var header in ProxyHeaders)
            {
                var value = request.Headers[header].ToString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                // X-Forwarded-For lists client, proxy1, proxy2…; the first entry is the client.
                var first = value.Split(',')[0].Trim();
                ip = IpAddressHelper.Normalize(StripPort(first));
                if (ip is not null) break;
            }
        }
        ip ??= IpAddressHelper.Normalize(http.Connection.RemoteIpAddress?.ToString());

        var acceptLanguage = request.Headers.AcceptLanguage.ToString();
        string? language = null;
        if (!string.IsNullOrWhiteSpace(acceptLanguage))
        {
            language = acceptLanguage.Split(',')[0].Split(';')[0].Trim();
            if (language.Length > 20) language = language[..20];
            if (language.Length == 0 || language == "*") language = null;
        }

        var referrer = request.Headers.Referer.ToString();
        if (string.IsNullOrWhiteSpace(referrer)) referrer = null;
        else if (referrer.Length > 2000) referrer = referrer[..2000];

        var userAgent = request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent)) userAgent = null;

        return new VisitorInfo(
            ip,
            userAgent,
            language,
            referrer,
            request.Host.HasValue ? request.Host.Value! : string.Empty,
            string.IsNullOrEmpty(request.Scheme) ? "https" : request.Scheme,
            http.User.Identity?.IsAuthenticated == true);
    }

    private static string StripPort(string value)
    {
        // "1.2.3.4:5678" (some proxies) but not IPv6 "2001:db8::1"; bracketed IPv6 "[2001:db8::1]:443" is handled too.
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            return end > 0 ? value[1..end] : value;
        }
        var colon = value.IndexOf(':');
        if (colon > 0 && value.IndexOf(':', colon + 1) < 0) return value[..colon];
        return value;
    }
}

/// <summary>
/// The visitor behind the current scope: the HTTP request while prerendering, the connection behind the circuit
/// afterwards (captured by <see cref="AnalyticsCircuitHandler"/> when the circuit opens). Also remembers the last
/// page tracked in this scope so navigations inside the site get an internal referrer.
/// </summary>
public sealed class AnalyticsVisitorContext(IHttpContextAccessor httpContextAccessor, IAnalyticsSettingsService settings)
{
    private VisitorInfo? _visitor;
    private bool _captured;

    /// <summary>The visitor, or null when nothing about the request is known in this scope.</summary>
    public VisitorInfo? Visitor
    {
        get
        {
            if (!_captured) Capture(httpContextAccessor.HttpContext);
            return _visitor;
        }
    }

    /// <summary>The path of the last page view recorded in this scope, if any.</summary>
    public string? LastPath { get; set; }

    /// <summary>Reads the visitor from <paramref name="http"/>. A later capture with a real request replaces an earlier empty one.</summary>
    public void Capture(HttpContext? http)
    {
        var info = VisitorInfo.FromHttpContext(http, settings.Current.TrustProxyHeaders);
        if (info is null && _captured) return;
        _visitor = info;
        _captured = info is not null;
    }
}

/// <summary>
/// Copies the request details of the SignalR connection into the circuit's <see cref="AnalyticsVisitorContext"/>
/// when a circuit opens, so page views recorded during interactive navigation know the visitor's address and browser.
/// </summary>
public sealed class AnalyticsCircuitHandler(AnalyticsVisitorContext context, IHttpContextAccessor httpContextAccessor) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        context.Capture(httpContextAccessor.HttpContext);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // A reconnect may come from a new connection; keep the freshest request details.
        context.Capture(httpContextAccessor.HttpContext);
        return Task.CompletedTask;
    }
}
