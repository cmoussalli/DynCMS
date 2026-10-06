namespace DynCMS.Plugins.Models;

/// <summary>A single property (field) on a <see cref="ContentType"/>.</summary>
public class PropertyType
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContentTypeId { get; set; }
    public string Alias { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string EditorAlias { get; set; } = string.Empty;
    public string GroupName { get; set; } = "Content";
    public bool Mandatory { get; set; }
    /// <summary>
    /// The value is stored per language. Only effective when the document type <see cref="ContentType.VariesByCulture">varies by culture</see>;
    /// a property that does not vary is shared by every language of the content item.
    /// </summary>
    public bool VariesByCulture { get; set; }
    public int SortOrder { get; set; }
    public Dictionary<string, string> Config { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? GetConfig(string key) => Config.TryGetValue(key, out var v) ? v : null;

    /// <summary>A detached copy that can be edited without touching the cached original.</summary>
    public PropertyType Clone()
    {
        var copy = (PropertyType)MemberwiseClone();
        copy.Config = new Dictionary<string, string>(Config, StringComparer.OrdinalIgnoreCase);
        return copy;
    }
}
