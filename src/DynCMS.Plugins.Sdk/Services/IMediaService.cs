using DynCMS.Plugins.Models;

namespace DynCMS.Plugins.Services;

public interface IMediaService
{
    Task<IReadOnlyList<MediaItem>> GetChildrenAsync(Guid? folderId, CancellationToken ct = default);
    Task<MediaItem?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<MediaItem>> GetAncestorsAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<MediaItem>> GetRecentAsync(int take = 12, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task<MediaItem> CreateFolderAsync(Guid? parentId, string name, CancellationToken ct = default);
    Task<MediaItem> UploadAsync(Guid? folderId, string fileName, string? contentType, Stream content, CancellationToken ct = default);
    Task<MediaItem?> RenameAsync(Guid id, string name, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    /// <summary>Public URL for a media file, or null for folders.</summary>
    string? GetUrl(MediaItem? item);
}

/// <summary>Physical storage for uploaded files.</summary>
public interface IMediaStorage
{
    Task SaveAsync(string storedPath, Stream content, CancellationToken ct = default);
    Task DeleteAsync(string storedPath, CancellationToken ct = default);
    string GetUrl(string storedPath);
}
