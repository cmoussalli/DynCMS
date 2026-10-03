using System.Text.Json;

namespace DynCMS.Core.Models;

/// <summary>
/// A node in the content tree. Holds a draft (editable) state and, once published, a
/// published snapshot that the public site renders.
/// </summary>
/// <remarks>
/// When the document type <see cref="ContentType.VariesByCulture">varies by culture</see>, the name, URL
/// segment, publish state and the values of the varying properties live per language in <see cref="Cultures"/>.
/// The node-level <see cref="Name"/> and <see cref="UrlSegment"/> then mirror the default language (for the tree,
/// search and the API), <see cref="IsPublished"/> is true when any language is published, and
/// <see cref="DraftValues"/> / <see cref="PublishedValues"/> hold only the properties that are shared by every
/// language. Use the culture-aware members (<see cref="GetName"/>, <see cref="GetValue(string, string?)"/>,
/// <see cref="IsPublishedIn"/>) rather than the raw dictionaries when a culture is in play.
/// </remarks>
public class ContentNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public Guid ContentTypeId { get; set; }
    public ContentType ContentType { get; set; } = null!;

    /// <summary>Comma separated list of ancestor ids ending with this node's id.</summary>
    public string Path { get; set; } = string.Empty;
    public int Level { get; set; }
    public int SortOrder { get; set; }

    // Draft state (invariant: shared by every language)
    public string Name { get; set; } = string.Empty;
    public string UrlSegment { get; set; } = string.Empty;
    public string? TemplateAlias { get; set; }
    public Dictionary<string, string?> DraftValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Published snapshot (invariant)
    public bool IsPublished { get; set; }
    public string? PublishedName { get; set; }
    public string? PublishedUrlSegment { get; set; }
    public string? PublishedTemplateAlias { get; set; }
    public Dictionary<string, string?> PublishedValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-language state, keyed by ISO code. Empty unless the document type varies by culture.</summary>
    public Dictionary<string, ContentCulture> Cultures { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }

    /// <summary>Ids of all ancestors, root first.</summary>
    public IEnumerable<Guid> AncestorIds => Path
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(Guid.Parse)
        .Where(id => id != Id);

    /// <summary>True when the document type varies by culture (requires <see cref="ContentType"/> to be loaded).</summary>
    public bool VariesByCulture => ContentType?.VariesByCulture == true;

    /// <summary>True when the draft differs from the published snapshot (or is not published), in any language.</summary>
    public bool HasPendingChanges
    {
        get
        {
            if (!IsPublished) return true;
            if (VariesByCulture)
            {
                if (Cultures.Values.Any(c => c.Exists && c.HasPendingChanges)) return true;
                if (!string.Equals(TemplateAlias, PublishedTemplateAlias, StringComparison.Ordinal)) return true;
                return JsonSerializer.Serialize(Normalize(DraftValues)) != JsonSerializer.Serialize(Normalize(PublishedValues));
            }
            if (!string.Equals(Name, PublishedName, StringComparison.Ordinal)) return true;
            if (!string.Equals(UrlSegment, PublishedUrlSegment, StringComparison.Ordinal)) return true;
            if (!string.Equals(TemplateAlias, PublishedTemplateAlias, StringComparison.Ordinal)) return true;
            return JsonSerializer.Serialize(Normalize(DraftValues)) != JsonSerializer.Serialize(Normalize(PublishedValues));
        }
    }

    // ---- invariant access (unchanged API) ----

    public string? GetValue(string alias) => DraftValues.TryGetValue(alias, out var v) ? v : null;

    /// <summary>Sets a shared draft value. A property that varies by culture must be set with <see cref="SetValue(string, string?, string?)"/> and a culture.</summary>
    public void SetValue(string alias, string? value)
    {
        if (PropertyVaries(alias))
            throw new InvalidOperationException($"Property '{alias}' varies by culture: call SetValue(\"{alias}\", value, culture) with the language it belongs to.");
        DraftValues[alias] = value;
    }

    // ---- culture-aware access ----

    /// <summary>True when <paramref name="alias"/> is a property whose value is stored per language.</summary>
    public bool PropertyVaries(string alias) =>
        VariesByCulture && ContentType.Properties.Any(p => p.VariesByCulture && string.Equals(p.Alias, alias, StringComparison.OrdinalIgnoreCase));

    /// <summary>The state of the node in <paramref name="culture"/>, or null when the language has not been created for it.</summary>
    public ContentCulture? GetCulture(string? culture) =>
        culture is not null && Cultures.TryGetValue(culture, out var c) ? c : null;

    /// <summary>The state of the node in <paramref name="culture"/>, created (empty) when missing.</summary>
    public ContentCulture GetOrAddCulture(string culture)
    {
        if (!Cultures.TryGetValue(culture, out var c))
        {
            c = new ContentCulture();
            Cultures[culture] = c;
        }
        return c;
    }

    /// <summary>The languages that have been created for this node (have a name).</summary>
    public IEnumerable<string> ExistingCultures => Cultures.Where(kv => kv.Value.Exists).Select(kv => kv.Key);

    /// <summary>The languages this node is published in.</summary>
    public IEnumerable<string> PublishedCultures => Cultures.Where(kv => kv.Value.IsPublished).Select(kv => kv.Key);

    /// <summary>The draft name in <paramref name="culture"/>; the shared name for invariant content or when the language has no name yet.</summary>
    public string GetName(string? culture) =>
        VariesByCulture && GetCulture(culture) is { Exists: true } c ? c.Name : Name;

    public void SetName(string? culture, string name)
    {
        if (VariesByCulture && culture is not null) GetOrAddCulture(culture).Name = name;
        else Name = name;
    }

    /// <summary>The draft URL segment in <paramref name="culture"/> (the shared one for invariant content).</summary>
    public string GetUrlSegment(string? culture) =>
        VariesByCulture && GetCulture(culture) is { } c && !string.IsNullOrEmpty(c.UrlSegment) ? c.UrlSegment : UrlSegment;

    public void SetUrlSegment(string? culture, string segment)
    {
        if (VariesByCulture && culture is not null) GetOrAddCulture(culture).UrlSegment = segment;
        else UrlSegment = segment;
    }

    /// <summary>
    /// The draft value of <paramref name="alias"/>: the language's value when the property varies by culture,
    /// otherwise the shared value.
    /// </summary>
    public string? GetValue(string alias, string? culture) =>
        PropertyVaries(alias)
            ? GetCulture(culture)?.DraftValues.GetValueOrDefault(alias)
            : GetValue(alias);

    /// <summary>Sets the draft value in the right place: the language's dictionary for a varying property, the shared one otherwise.</summary>
    public void SetValue(string alias, string? value, string? culture)
    {
        if (PropertyVaries(alias) && culture is not null) GetOrAddCulture(culture).DraftValues[alias] = value;
        else DraftValues[alias] = value;
    }

    /// <summary>True when the node is live in <paramref name="culture"/> (for invariant content: when it is published at all).</summary>
    public bool IsPublishedIn(string? culture) =>
        VariesByCulture ? GetCulture(culture)?.IsPublished == true : IsPublished;

    /// <summary>True when the draft in <paramref name="culture"/> (plus the shared values) differs from what is published.</summary>
    public bool HasPendingChangesIn(string? culture)
    {
        if (!VariesByCulture) return HasPendingChanges;
        var c = GetCulture(culture);
        if (c is null || !c.Exists) return false;
        if (c.HasPendingChanges) return true;
        if (!string.Equals(TemplateAlias, PublishedTemplateAlias, StringComparison.Ordinal)) return true;
        return JsonSerializer.Serialize(Normalize(DraftValues)) != JsonSerializer.Serialize(Normalize(PublishedValues));
    }

    private static SortedDictionary<string, string?> Normalize(Dictionary<string, string?> values) => ContentCulture.Normalize(values);
}
