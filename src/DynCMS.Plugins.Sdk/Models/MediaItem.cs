namespace DynCMS.Plugins.Models;

/// <summary>A file or folder in the media library.</summary>
public class MediaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public string? FileName { get; set; }
    public string? Extension { get; set; }
    public string? MimeType { get; set; }
    public string? StoredPath { get; set; }
    public long SizeBytes { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsImage => MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>A detached copy that can be edited without touching the cached original.</summary>
    public MediaItem Clone() => (MediaItem)MemberwiseClone();
}
