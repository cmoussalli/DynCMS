using DynCMS.Plugins.Models;

namespace DynCMS.Plugins.Services;

/// <summary>
/// Read side used by the public website: resolves routes and navigates published content, in one language at a
/// time. Every method takes an optional <c>culture</c> (an ISO code such as <c>de-DE</c>); when it is null the
/// current language is used: the one set on <see cref="ICultureContext"/>, else the language prefix of the current
/// path (<c>/de/…</c>), else the default language. Content whose document type varies by culture is only returned
/// when it is published (or, in preview, created) in that language.
/// </summary>
public interface IPublishedContentQuery
{
    /// <summary>
    /// Resolves a request path such as "/blog/my-post" or "/de/blog/mein-beitrag" to published content. A language
    /// prefix in the path decides the language; otherwise <paramref name="culture"/> or the current language applies.
    /// </summary>
    Task<PublishedContent?> GetByRouteAsync(string path, bool preview = false, string? culture = null, CancellationToken ct = default);
    Task<PublishedContent?> GetByIdAsync(Guid id, bool preview = false, string? culture = null, CancellationToken ct = default);
    /// <summary>The primary root node (the site home).</summary>
    Task<PublishedContent?> GetRootAsync(bool preview = false, string? culture = null, CancellationToken ct = default);
    Task<IReadOnlyList<PublishedContent>> GetChildrenAsync(Guid parentId, bool preview = false, string? culture = null, CancellationToken ct = default);
    Task<IReadOnlyList<PublishedContent>> GetAncestorsAsync(Guid id, bool preview = false, string? culture = null, CancellationToken ct = default);
    Task<IReadOnlyList<PublishedContent>> GetByContentTypeAsync(string contentTypeAlias, bool preview = false, string? culture = null, CancellationToken ct = default);
    /// <summary>The URL of a node in <paramref name="culture"/> (draft segments when it is not published there).</summary>
    Task<string?> GetUrlAsync(Guid id, string? culture = null, CancellationToken ct = default);
    /// <summary>
    /// The same item in every language it is available in, with URLs, for a language switcher.
    /// <paramref name="culture"/> (or the current language) is flagged <see cref="CultureLink.IsCurrent"/>.
    /// </summary>
    Task<IReadOnlyList<CultureLink>> GetCultureLinksAsync(Guid id, bool preview = false, string? culture = null, CancellationToken ct = default);
    /// <summary>The ISO code the query would use for <paramref name="culture"/> (the current language when null).</summary>
    Task<string> ResolveCultureAsync(string? culture = null, CancellationToken ct = default);
}
