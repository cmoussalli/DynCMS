using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynCMS.Core.Models;

/// <summary>
/// The state of a content node in one language: its name, URL segment, publish state and the values of the
/// properties that vary by culture. Stored as JSON on the node, keyed by ISO code. Properties that do not vary
/// live on the node itself (<see cref="ContentNode.DraftValues"/>) and are shared by every language.
/// </summary>
public sealed class ContentCulture
{
    // Draft state
    public string Name { get; set; } = string.Empty;
    public string UrlSegment { get; set; } = string.Empty;
    public Dictionary<string, string?> DraftValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Published snapshot
    public bool IsPublished { get; set; }
    public string? PublishedName { get; set; }
    public string? PublishedUrlSegment { get; set; }
    public Dictionary<string, string?> PublishedValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime? PublishedAt { get; set; }

    /// <summary>True when a name has been entered, i.e. the language has been "created" for the node.</summary>
    [JsonIgnore]
    public bool Exists => !string.IsNullOrWhiteSpace(Name);

    /// <summary>True when the draft differs from the published snapshot (or the language is not published).</summary>
    [JsonIgnore]
    public bool HasPendingChanges
    {
        get
        {
            if (!IsPublished) return true;
            if (!string.Equals(Name, PublishedName, StringComparison.Ordinal)) return true;
            if (!string.Equals(UrlSegment, PublishedUrlSegment, StringComparison.Ordinal)) return true;
            return JsonSerializer.Serialize(Normalize(DraftValues)) != JsonSerializer.Serialize(Normalize(PublishedValues));
        }
    }

    /// <summary>Re-wraps the dictionaries so lookups ignore case after JSON deserialisation.</summary>
    internal void NormalizeComparers()
    {
        DraftValues = new Dictionary<string, string?>(DraftValues, StringComparer.OrdinalIgnoreCase);
        PublishedValues = new Dictionary<string, string?>(PublishedValues, StringComparer.OrdinalIgnoreCase);
    }

    internal static SortedDictionary<string, string?> Normalize(Dictionary<string, string?> values)
    {
        var sorted = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values)
        {
            if (!string.IsNullOrEmpty(v)) sorted[k] = v;
        }
        return sorted;
    }
}
