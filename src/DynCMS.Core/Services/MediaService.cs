using DynCMS.Core.Plugins;
using DynCMS.Core.Data;
using DynCMS.Plugins.Helpers;
using DynCMS.Plugins.Models;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DynCMS.Core.Services;

public sealed class FileSystemMediaStorage(DynCmsPaths paths) : IMediaStorage
{
    public async Task SaveAsync(string storedPath, Stream content, CancellationToken ct = default)
    {
        var full = Path.Combine(paths.MediaRootPath, storedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(fs, ct);
    }

    public Task DeleteAsync(string storedPath, CancellationToken ct = default)
    {
        var full = Path.Combine(paths.MediaRootPath, storedPath);
        if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }

    public string GetUrl(string storedPath) => $"{paths.MediaRequestPath}/{storedPath.Replace('\\', '/')}";
}

public sealed class MediaService(
    IDbContextFactory<DynCmsDbContext> factory,
    IMediaStorage storage,
    IOptions<DynCmsOptions> options,
    ICmsEventDispatcher? events = null) : IMediaService
{
    private Task EmitAsync(MediaEventKind kind, MediaItem item, CancellationToken ct) =>
        events is null ? Task.CompletedTask : events.PublishAsync(new MediaEvent(kind, item), ct);

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public async Task<IReadOnlyList<MediaItem>> GetChildrenAsync(Guid? folderId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MediaItems.AsNoTracking().Where(m => m.ParentId == folderId)
            .OrderByDescending(m => m.IsFolder).ThenBy(m => m.SortOrder).ThenBy(m => m.Name)
            .ToListAsync(ct);
    }

    public async Task<MediaItem?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    public async Task<IReadOnlyList<MediaItem>> GetAncestorsAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var chain = new List<MediaItem>();
        var current = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        while (current?.ParentId is Guid parentId)
        {
            current = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == parentId, ct);
            if (current is null) break;
            chain.Insert(0, current);
        }
        return chain;
    }

    public async Task<IReadOnlyList<MediaItem>> GetRecentAsync(int take = 12, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MediaItems.AsNoTracking().Where(m => !m.IsFolder)
            .OrderByDescending(m => m.CreatedAt).Take(take).ToListAsync(ct);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MediaItems.CountAsync(m => !m.IsFolder, ct);
    }

    public async Task<MediaItem> CreateFolderAsync(Guid? parentId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Folder needs a name.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var folder = new MediaItem { ParentId = parentId, Name = name.Trim(), IsFolder = true };
        db.MediaItems.Add(folder);
        await db.SaveChangesAsync(ct);
        await EmitAsync(MediaEventKind.FolderCreated, folder, ct);
        return folder;
    }

    public async Task<MediaItem> UploadAsync(Guid? folderId, string fileName, string? contentType, Stream content, CancellationToken ct = default)
    {
        var opts = options.Value;
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext) || !opts.AllowedUploadExtensions.Contains(ext))
            throw new InvalidOperationException($"Files of type '{ext}' are not allowed.");

        if (string.IsNullOrWhiteSpace(contentType) && !ContentTypes.TryGetContentType(fileName, out contentType))
            contentType = "application/octet-stream";

        var id = Guid.NewGuid();
        var safeName = Slug.ToUrlSegment(Path.GetFileNameWithoutExtension(fileName));
        if (string.IsNullOrEmpty(safeName)) safeName = "file";
        var storedPath = Path.Combine(id.ToString("N")[..2], $"{safeName}-{id.ToString("N")[..8]}{ext.ToLowerInvariant()}");

        long size;
        if (content.CanSeek)
        {
            size = content.Length;
            if (size > opts.MaxUploadBytes) throw new InvalidOperationException("The file is larger than the allowed upload size.");
            await storage.SaveAsync(storedPath, content, ct);
        }
        else
        {
            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            size = buffer.Length;
            if (size > opts.MaxUploadBytes) throw new InvalidOperationException("The file is larger than the allowed upload size.");
            buffer.Position = 0;
            await storage.SaveAsync(storedPath, buffer, ct);
        }

        var item = new MediaItem
        {
            Id = id,
            ParentId = folderId,
            Name = Path.GetFileNameWithoutExtension(fileName),
            FileName = fileName,
            Extension = ext.ToLowerInvariant(),
            MimeType = contentType,
            StoredPath = storedPath.Replace('\\', '/'),
            SizeBytes = size
        };

        await using var db = await factory.CreateDbContextAsync(ct);
        db.MediaItems.Add(item);
        await db.SaveChangesAsync(ct);
        await EmitAsync(MediaEventKind.Uploaded, item, ct);
        return item;
    }

    public async Task<MediaItem?> RenameAsync(Guid id, string name, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (item is null) return null;
        item.Name = name.Trim();
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await EmitAsync(MediaEventKind.Renamed, item, ct);
        return item;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (item is null) return;

        var toDelete = new List<MediaItem> { item };
        if (item.IsFolder)
        {
            var queue = new Queue<Guid>([item.Id]);
            while (queue.Count > 0)
            {
                var parentId = queue.Dequeue();
                var children = await db.MediaItems.Where(m => m.ParentId == parentId).ToListAsync(ct);
                foreach (var c in children)
                {
                    toDelete.Add(c);
                    if (c.IsFolder) queue.Enqueue(c.Id);
                }
            }
        }

        foreach (var m in toDelete.Where(m => !m.IsFolder && m.StoredPath is not null))
            await storage.DeleteAsync(m.StoredPath!, ct);

        db.MediaItems.RemoveRange(toDelete);
        await db.SaveChangesAsync(ct);
        await EmitAsync(MediaEventKind.Deleted, item, ct);
    }

    public string? GetUrl(MediaItem? item) =>
        item is { IsFolder: false, StoredPath: not null } ? storage.GetUrl(item.StoredPath) : null;
}
