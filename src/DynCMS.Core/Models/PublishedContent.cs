using DynCMS.Core.Helpers;

namespace DynCMS.Core.Models;

/// <summary>
/// Read-only view of a content node as the public site sees it, in one language. Values come from the
/// published snapshot, or from the draft when rendered in preview mode. For content that varies by culture the
/// name, URL and varying values are those of <see cref="Culture"/> (with fallback languages applied to empty
/// values); shared values are merged in.
/// </summary>
public sealed class PublishedContent
{
    public required Guid Id { get; init; }
    public Guid? ParentId { get; init; }
    public required string Name { get; init; }
    public required string UrlSegment { get; init; }
    public required string Url { get; init; }
    public required string ContentTypeAlias { get; init; }
    public required string ContentTypeName { get; init; }
    public string? TemplateAlias { get; init; }
    public int Level { get; init; }
    public int SortOrder { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? PublishedAt { get; init; }
    public bool IsPreview { get; init; }

    /// <summary>The ISO code of the language this view is in (the default language for a site without variants).</summary>
    public required string Culture { get; init; }

    /// <summary>True when the document type varies by culture, i.e. other languages may show different content.</summary>
    public bool VariesByCulture { get; init; }

    /// <summary>The languages this item is available in (published, or created when previewing).</summary>
    public IReadOnlyList<string> Cultures { get; init; } = [];

    public required IReadOnlyDictionary<string, string?> Values { get; init; }

    public string? this[string alias] => Values.TryGetValue(alias, out var v) ? v : null;

    public bool HasValue(string alias) => !string.IsNullOrWhiteSpace(this[alias]);

    /// <summary>Converts the stored value for <paramref name="alias"/> to <typeparamref name="T"/>.</summary>
    public T? Value<T>(string alias, T? fallback = default) => ContentValueConverter.Convert(this[alias], fallback);

    public bool Is(string contentTypeAlias) => string.Equals(ContentTypeAlias, contentTypeAlias, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the item is available in <paramref name="culture"/>.</summary>
    public bool IsAvailableIn(string culture) => Cultures.Contains(culture, StringComparer.OrdinalIgnoreCase);
}
