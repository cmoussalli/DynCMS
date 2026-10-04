namespace DynCMS.Plugins.Models;

/// <summary>
/// A document type: the schema that describes which properties a content node has,
/// where it may live in the tree and which templates can render it.
/// </summary>
public class ContentType
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Alias { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Icon { get; set; } = "document";
    public bool AllowedAsRoot { get; set; }
    public List<string> AllowedChildTypeAliases { get; set; } = [];
    public List<string> AllowedTemplateAliases { get; set; } = [];
    public string? DefaultTemplateAlias { get; set; }
    /// <summary>
    /// Content of this type has a name, URL segment and publish state per language (Settings → Languages), and
    /// the properties flagged <see cref="PropertyType.VariesByCulture"/> hold a value per language. Off by default:
    /// the content is then the same in every language (invariant).
    /// </summary>
    public bool VariesByCulture { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<PropertyType> Properties { get; set; } = [];

    /// <summary>Property groups (tabs) in display order.</summary>
    public IEnumerable<string> Groups => Properties
        .OrderBy(p => p.SortOrder)
        .Select(p => string.IsNullOrWhiteSpace(p.GroupName) ? "Content" : p.GroupName)
        .Distinct(StringComparer.OrdinalIgnoreCase);
}
