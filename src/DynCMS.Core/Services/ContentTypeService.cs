using DynCMS.Core.Data;
using DynCMS.Plugins.Helpers;
using DynCMS.Plugins.Models;
using Microsoft.EntityFrameworkCore;

namespace DynCMS.Core.Services;

public sealed class ContentTypeService(IDbContextFactory<DynCmsDbContext> factory, ContentCache cache) : IContentTypeService
{
    // Reads are answered from the content cache, as copies: the back office edits them in place before saving.
    public async Task<IReadOnlyList<ContentType>> GetAllAsync(CancellationToken ct = default) =>
        (await cache.GetSchemaAsync(ct)).ContentTypes.Select(t => t.Clone()).ToList();

    public async Task<ContentType?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await cache.GetSchemaAsync(ct)).Type(id)?.Clone();

    public async Task<ContentType?> GetByAliasAsync(string alias, CancellationToken ct = default) =>
        (await cache.GetSchemaAsync(ct)).TypeByAlias(alias)?.Clone();

    public async Task<bool> AliasExistsAsync(string alias, Guid? excludeId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ContentTypes.AnyAsync(t => t.Alias == alias && (excludeId == null || t.Id != excludeId), ct);
    }

    public async Task<ContentType> SaveAsync(ContentType contentType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contentType.Name))
            throw new InvalidOperationException("A document type needs a name.");
        if (string.IsNullOrWhiteSpace(contentType.Alias))
            contentType.Alias = Slug.ToAlias(contentType.Name);

        var propertyAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in contentType.Properties)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) throw new InvalidOperationException("Every property needs a name.");
            if (string.IsNullOrWhiteSpace(p.Alias)) p.Alias = Slug.ToAlias(p.Name);
            if (string.IsNullOrWhiteSpace(p.EditorAlias)) throw new InvalidOperationException($"Property '{p.Name}' needs a property editor.");
            if (!propertyAliases.Add(p.Alias)) throw new InvalidOperationException($"Property alias '{p.Alias}' is used more than once.");
            if (string.IsNullOrWhiteSpace(p.GroupName)) p.GroupName = "Content";
        }

        await using var db = await factory.CreateDbContextAsync(ct);

        if (await db.ContentTypes.AnyAsync(t => t.Alias == contentType.Alias && t.Id != contentType.Id, ct))
            throw new InvalidOperationException($"The alias '{contentType.Alias}' is already used by another document type.");

        var existing = await db.ContentTypes.Include(t => t.Properties).FirstOrDefaultAsync(t => t.Id == contentType.Id, ct);
        var now = DateTime.UtcNow;

        if (existing is null)
        {
            contentType.CreatedAt = now;
            contentType.UpdatedAt = now;
            var order = 0;
            foreach (var p in contentType.Properties)
            {
                p.ContentTypeId = contentType.Id;
                p.SortOrder = order++;
            }
            db.ContentTypes.Add(contentType);
        }
        else
        {
            existing.Alias = contentType.Alias;
            existing.Name = contentType.Name;
            existing.Description = contentType.Description;
            existing.Icon = contentType.Icon;
            existing.AllowedAsRoot = contentType.AllowedAsRoot;
            existing.AllowedChildTypeAliases = [.. contentType.AllowedChildTypeAliases];
            existing.AllowedTemplateAliases = [.. contentType.AllowedTemplateAliases];
            existing.DefaultTemplateAlias = contentType.DefaultTemplateAlias;
            existing.SortOrder = contentType.SortOrder;
            existing.UpdatedAt = now;

            var incomingIds = contentType.Properties.Select(p => p.Id).ToHashSet();
            foreach (var removed in existing.Properties.Where(p => !incomingIds.Contains(p.Id)).ToList())
            {
                existing.Properties.Remove(removed);
                db.PropertyTypes.Remove(removed);
            }

            var order = 0;
            foreach (var incoming in contentType.Properties)
            {
                var target = existing.Properties.FirstOrDefault(p => p.Id == incoming.Id);
                if (target is null)
                {
                    // Added explicitly: a navigation-discovered entity whose (client-generated) key is already set
                    // would be tracked as Modified and fail with a concurrency error on save.
                    target = new PropertyType { Id = incoming.Id, ContentTypeId = existing.Id };
                    db.PropertyTypes.Add(target);
                    existing.Properties.Add(target);
                }
                target.Alias = incoming.Alias;
                target.Name = incoming.Name;
                target.Description = incoming.Description;
                target.EditorAlias = incoming.EditorAlias;
                target.GroupName = incoming.GroupName;
                target.Mandatory = incoming.Mandatory;
                target.SortOrder = order++;
                target.Config = new Dictionary<string, string>(incoming.Config, StringComparer.OrdinalIgnoreCase);
            }
        }

        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        return (await GetAsync(contentType.Id, ct))!;
    }

    public async Task<int> CountContentAsync(Guid contentTypeId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ContentNodes.CountAsync(n => n.ContentTypeId == contentTypeId, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var type = await db.ContentTypes.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (type is null) return;

        var usage = await db.ContentNodes.CountAsync(n => n.ContentTypeId == id, ct);
        if (usage > 0)
            throw new InvalidOperationException($"'{type.Name}' is used by {usage} content item(s). Delete that content first.");

        db.ContentTypes.Remove(type);

        // Remove references to this alias from other document types.
        var others = await db.ContentTypes.Where(t => t.Id != id).ToListAsync(ct);
        foreach (var other in others)
        {
            if (other.AllowedChildTypeAliases.Remove(type.Alias))
                other.AllowedChildTypeAliases = [.. other.AllowedChildTypeAliases];
        }

        await db.SaveChangesAsync(ct);
        cache.Invalidate();
    }
}
