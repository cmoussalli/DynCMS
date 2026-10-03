namespace DynCMS.Core.Analytics;

/// <summary>What the renderer knows about a page view when it records it.</summary>
public sealed record PageTrackingRequest(
    string Path,
    string? Query,
    string? Title,
    Guid? ContentId,
    string? Culture,
    bool NotFound,
    bool IsBackOfficeUser);

/// <summary>
/// Records page views from inside a render (prerender or circuit). Scoped: it pairs the current
/// <see cref="AnalyticsVisitorContext"/> with the settings and hands the result to <see cref="IAnalyticsTracker"/>.
/// </summary>
public interface IAnalyticsPageTracker
{
    /// <summary>Records a page view, unless the settings exclude it. Never throws and never blocks on the database.</summary>
    void Track(PageTrackingRequest request);

    /// <summary>
    /// Remembers <paramref name="path"/> as the current page without recording a view (the prerender already did),
    /// so the next navigation in this circuit gets it as its internal referrer.
    /// </summary>
    void NotePath(string path);
}

internal sealed class AnalyticsPageTracker(
    AnalyticsVisitorContext context,
    IAnalyticsSettingsService settings,
    IAnalyticsTracker tracker) : IAnalyticsPageTracker
{
    public void Track(PageTrackingRequest request)
    {
        var current = settings.Current;
        var path = NormalizePath(request.Path);

        try
        {
            if (!current.Enabled) return;
            if (request.IsBackOfficeUser && !current.TrackSignedInUsers) return;
            if (current.IsExcluded(path)) return;

            var visitor = context.Visitor;
            if (visitor is { IsAuthenticated: true } && !current.TrackSignedInUsers) return;

            string? referrer;
            if (context.LastPath is { } last)
            {
                var host = visitor?.Host;
                referrer = string.IsNullOrEmpty(host) ? last : $"{visitor!.Scheme}://{host}{last}";
            }
            else
            {
                referrer = visitor?.Referrer;
            }

            tracker.TryEnqueue(new PageViewHit(
                DateTime.UtcNow,
                visitor?.Host ?? string.Empty,
                path,
                string.IsNullOrEmpty(request.Query) ? null : request.Query.TrimStart('?'),
                request.Title,
                request.ContentId,
                request.Culture,
                request.NotFound,
                referrer,
                visitor?.IpAddress,
                visitor?.UserAgent,
                visitor?.Language));
        }
        finally
        {
            context.LastPath = path;
        }
    }

    public void NotePath(string path) => context.LastPath = NormalizePath(path);

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var p = path.Trim();
        var q = p.IndexOf('?');
        if (q >= 0) p = p[..q];
        if (!p.StartsWith('/')) p = "/" + p;
        if (p.Length > 1) p = p.TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }
}
