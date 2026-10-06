using DynCMS.Core.Plugins;
using DynCMS.Core.Data;
using DynCMS.Plugins.Helpers;
using DynCMS.Plugins.Models;
using Microsoft.EntityFrameworkCore;

namespace DynCMS.Core.Services;

public sealed class ContentService(IDbContextFactory<DynCmsDbContext> factory, ContentCache cache, ICmsEventDispatcher? events = null) : IContentService
{
    private Task EmitAsync(ContentEventKind kind, ContentNode? node, IReadOnlyList<string>? cultures, CancellationToken ct) =>
        events is null || node is null ? Task.CompletedTask : events.PublishAsync(new ContentEvent(kind, node, cultures), ct);

    private static IQueryable<ContentNode> WithType(IQueryable<ContentNode> q) =>
        q.Include(n => n.ContentType).ThenInclude(t => t.Properties.OrderBy(p => p.SortOrder));

    public async Task<IReadOnlyList<ContentNode>> GetRootsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ContentNodes.AsNoTracking().Include(n => n.ContentType)
            .Where(n => n.ParentId == null)
            .OrderBy(n => n.SortOrder).ThenBy(n => n.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ContentNode>> GetChildrenAsync(Guid parentId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ContentNodes.AsNoTracking().Include(n => n.ContentType)
            .Where(n => n.ParentId == parentId)
            .OrderBy(n => n.SortOrder).ThenBy(n => n.Name)
            .ToListAsync(ct);
    }

    public async Task<bool> HasChildrenAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ContentNodes.AnyAsync(n => n.ParentId == id, ct);
    }

    public async Task<ContentNode?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await WithType(db.ContentNodes.AsNoTracking()).FirstOrDefaultAsync(n => n.Id == id, ct);
    }

    public async Task<IReadOnlyList<ContentNode>> GetAncestorsAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var node = await db.ContentNodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
        if (node is null) return [];
        var ids = node.AncestorIds.ToList();
        if (ids.Count == 0) return [];
        var ancestors = await db.ContentNodes.AsNoTracking().Include(n => n.ContentType)
            .Where(n => ids.Contains(n.Id)).ToListAsync(ct);
        return ids.Select(i => ancestors.First(a => a.Id == i)).ToList();
    }

    public async Task<IReadOnlyList<ContentNode>> GetDescendantsAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var node = await db.ContentNodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
        if (node is null) return [];
        var prefix = node.Path + ",";
        return await db.ContentNodes.AsNoTracking().Include(n => n.ContentType)
            .Where(n => n.Path.StartsWith(prefix))
            .OrderBy(n => n.Level).ThenBy(n => n.SortOrder)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ContentNode>> GetTreeAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var all = await db.ContentNodes.AsNoTracking().Include(n => n.ContentType).ToListAsync(ct);
        var byParent = all.ToLookup(n => n.ParentId);
        var result = new List<ContentNode>(all.Count);
        void Walk(Guid? parentId)
        {
            foreach (var n in byParent[parentId].OrderBy(n => n.SortOrder).ThenBy(n => n.Name))
            {
                result.Add(n);
                Walk(n.Id);
            }
        }
        Walk(null);
        return result;
    }

    public async Task<IReadOnlyList<ContentNode>> GetRecentAsync(int take = 10, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ContentNodes.AsNoTracking().Include(n => n.ContentType)
            .OrderByDescending(n => n.UpdatedAt).Take(take).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ContentNode>> SearchAsync(string term, int take = 25, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(term)) return [];
        await using var db = await factory.CreateDbContextAsync(ct);
        var t = term.Trim().ToLowerInvariant();
        // The node name mirrors the default language; the other languages live in JSON, so they are matched in memory.
        var byName = await db.ContentNodes.AsNoTracking().Include(n => n.ContentType)
            .Where(n => n.Name.ToLower().Contains(t) || n.UrlSegment.Contains(t))
            .OrderBy(n => n.Name).Take(take).ToListAsync(ct);
        if (byName.Count >= take) return byName;

        var ids = byName.Select(n => n.Id).ToHashSet();
        var variants = await db.ContentNodes.AsNoTracking().Include(n => n.ContentType)
            .Where(n => n.ContentType.VariesByCulture && !ids.Contains(n.Id))
            .ToListAsync(ct);
        var byCulture = variants
            .Where(n => n.Cultures.Values.Any(c => c.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || c.UrlSegment.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n.Name)
            .Take(take - byName.Count);
        return byName.Concat(byCulture).ToList();
    }

    public async Task<int> CountAsync(bool? published = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.ContentNodes.AsQueryable();
        if (published is not null) q = q.Where(n => n.IsPublished == published);
        return await q.CountAsync(ct);
    }

    public async Task<IReadOnlyList<ContentType>> GetAllowedChildTypesAsync(Guid? parentId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var types = await db.ContentTypes.AsNoTracking().OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
        if (parentId is null) return types.Where(t => t.AllowedAsRoot).ToList();

        var parent = await db.ContentNodes.AsNoTracking().Include(n => n.ContentType).FirstOrDefaultAsync(n => n.Id == parentId, ct);
        if (parent is null) return [];
        var allowed = parent.ContentType.AllowedChildTypeAliases.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return types.Where(t => allowed.Contains(t.Alias)).ToList();
    }

    public async Task<ContentNode> CreateAsync(Guid contentTypeId, Guid? parentId, string name, string? culture = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Content needs a name.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var type = await db.ContentTypes.Include(t => t.Properties).FirstOrDefaultAsync(t => t.Id == contentTypeId, ct)
            ?? throw new InvalidOperationException("Document type not found.");

        ContentNode? parent = null;
        if (parentId is not null)
        {
            parent = await db.ContentNodes.Include(n => n.ContentType).FirstOrDefaultAsync(n => n.Id == parentId, ct)
                ?? throw new InvalidOperationException("Parent content not found.");
            if (!parent.ContentType.AllowedChildTypeAliases.Contains(type.Alias, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{type.Name}' is not allowed under '{parent.ContentType.Name}'.");
        }
        else if (!type.AllowedAsRoot)
        {
            throw new InvalidOperationException($"'{type.Name}' is not allowed at the root.");
        }

        var maxSort = await db.ContentNodes.Where(n => n.ParentId == parentId).Select(n => (int?)n.SortOrder).MaxAsync(ct) ?? -1;
        var node = new ContentNode
        {
            ParentId = parentId,
            ContentTypeId = type.Id,
            ContentType = type,
            Name = name.Trim(),
            Level = parent is null ? 0 : parent.Level + 1,
            SortOrder = maxSort + 1,
            TemplateAlias = type.DefaultTemplateAlias ?? type.AllowedTemplateAliases.FirstOrDefault()
        };
        node.Path = parent is null ? node.Id.ToString() : $"{parent.Path},{node.Id}";
        foreach (var p in type.Properties)
        {
            if (!(type.VariesByCulture && p.VariesByCulture)) node.DraftValues[p.Alias] = null;
        }

        var languages = await LanguageService.LoadAsync(db, ct);
        if (type.VariesByCulture)
        {
            var language = ResolveLanguage(languages, culture);
            var state = node.GetOrAddCulture(language.IsoCode);
            state.Name = node.Name;
            state.UrlSegment = await UniqueSegmentAsync(db, parentId, Slug.ToUrlSegment(node.Name), node.Id, language.IsoCode, ct);
            foreach (var p in type.Properties.Where(p => p.VariesByCulture)) state.DraftValues[p.Alias] = null;
        }
        node.UrlSegment = await UniqueSegmentAsync(db, parentId, Slug.ToUrlSegment(node.Name), node.Id, null, ct);
        MirrorDefaultCulture(node, languages);

        // Detach the type: it was loaded tracked and must not be re-inserted.
        db.Entry(type).State = EntityState.Unchanged;
        db.ContentNodes.Add(node);
        await CommitAsync(db, ct);
        var created = (await GetAsync(node.Id, ct))!;
        await EmitAsync(ContentEventKind.Created, created, null, ct);
        return created;
    }

    public async Task<ContentNode> SaveAsync(ContentNode node, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var target = await WithType(db.ContentNodes).FirstOrDefaultAsync(n => n.Id == node.Id, ct)
            ?? throw new InvalidOperationException("Content not found.");
        var languages = await LanguageService.LoadAsync(db, ct);
        var now = DateTime.UtcNow;

        if (target.VariesByCulture)
        {
            // Every language that has a name is saved; the node-level name follows the default language.
            var incoming = node.Cultures.Where(kv => kv.Value.Exists || (target.Cultures.TryGetValue(kv.Key, out var old) && old.Exists)).ToList();
            if (incoming.Count == 0 && string.IsNullOrWhiteSpace(node.Name))
                throw new InvalidOperationException("Content needs a name in at least one language.");

            var cultures = new Dictionary<string, ContentCulture>(target.Cultures, StringComparer.OrdinalIgnoreCase);
            foreach (var (iso, state) in incoming)
            {
                var language = languages.Match(iso) ?? throw new InvalidOperationException($"'{iso}' is not a configured language.");
                var existing = cultures.TryGetValue(language.IsoCode, out var e) ? e : new ContentCulture();
                var name = state.Name?.Trim() ?? string.Empty;
                if (name.Length == 0)
                {
                    if (existing.IsPublished) throw new InvalidOperationException($"The name in {language.Name} cannot be empty while that language is published.");
                    cultures.Remove(language.IsoCode);
                    continue;
                }
                var segment = Slug.ToUrlSegment(string.IsNullOrWhiteSpace(state.UrlSegment) ? name : state.UrlSegment);
                var changed = existing.Name != name || existing.UrlSegment != segment
                    || System.Text.Json.JsonSerializer.Serialize(ContentCulture.Normalize(existing.DraftValues)) != System.Text.Json.JsonSerializer.Serialize(ContentCulture.Normalize(state.DraftValues));
                existing.Name = name;
                existing.UrlSegment = await UniqueSegmentAsync(db, target.ParentId, segment, target.Id, language.IsoCode, ct);
                existing.DraftValues = new Dictionary<string, string?>(state.DraftValues, StringComparer.OrdinalIgnoreCase);
                if (changed) existing.UpdatedAt = now;
                cultures[language.IsoCode] = existing;
            }
            target.Cultures = cultures;
            MirrorDefaultCulture(target, languages);
            if (string.IsNullOrWhiteSpace(target.Name)) target.Name = node.Name?.Trim() ?? string.Empty;
            target.UrlSegment = await UniqueSegmentAsync(db, target.ParentId, Slug.ToUrlSegment(string.IsNullOrWhiteSpace(target.UrlSegment) ? target.Name : target.UrlSegment), target.Id, null, ct);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(node.Name)) throw new InvalidOperationException("Content needs a name.");
            var segment = Slug.ToUrlSegment(string.IsNullOrWhiteSpace(node.UrlSegment) ? node.Name : node.UrlSegment);
            target.Name = node.Name.Trim();
            target.UrlSegment = await UniqueSegmentAsync(db, target.ParentId, segment, target.Id, null, ct);
        }

        target.TemplateAlias = node.TemplateAlias;
        target.DraftValues = new Dictionary<string, string?>(node.DraftValues, StringComparer.OrdinalIgnoreCase);
        target.UpdatedAt = now;

        await CommitAsync(db, ct);
        var saved = (await GetAsync(node.Id, ct))!;
        await EmitAsync(ContentEventKind.Saved, saved, null, ct);
        return saved;
    }

    public IReadOnlyList<ContentValidationError> Validate(ContentNode node, string? culture = null)
    {
        var errors = new List<ContentValidationError>();
        var varies = node.VariesByCulture;
        var scope = varies ? culture : null;

        if (string.IsNullOrWhiteSpace(node.GetName(scope))) errors.Add(new("name", "Name is required.", scope));
        foreach (var p in node.ContentType?.Properties ?? [])
        {
            if (!p.Mandatory) continue;
            var propertyVaries = varies && p.VariesByCulture;
            if (string.IsNullOrWhiteSpace(node.GetValue(p.Alias, scope)))
                errors.Add(new(p.Alias, $"{p.Name} is required.", propertyVaries ? scope : null));
        }
        return errors;
    }

    public async Task<PublishResult> PublishAsync(Guid id, IReadOnlyList<string>? cultures = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var node = await WithType(db.ContentNodes).FirstOrDefaultAsync(n => n.Id == id, ct)
            ?? throw new InvalidOperationException("Content not found.");
        var now = DateTime.UtcNow;

        if (!node.VariesByCulture)
        {
            var errors = Validate(node);
            if (errors.Count > 0) return PublishResult.Failed(errors);

            node.IsPublished = true;
            node.PublishedName = node.Name;
            node.PublishedUrlSegment = node.UrlSegment;
            node.PublishedTemplateAlias = node.TemplateAlias;
            node.PublishedValues = new Dictionary<string, string?>(node.DraftValues, StringComparer.OrdinalIgnoreCase);
            node.PublishedAt = now;
            node.UpdatedAt = now;
            await CommitAsync(db, ct);
            var live = (await GetAsync(id, ct))!;
            await EmitAsync(ContentEventKind.Published, live, null, ct);
            return PublishResult.Ok(live);
        }

        var languages = await LanguageService.LoadAsync(db, ct);
        var requested = (cultures is { Count: > 0 } ? cultures : node.ExistingCultures.ToList())
            .Select(c => languages.Match(c) ?? throw new InvalidOperationException($"'{c}' is not a configured language."))
            .DistinctBy(l => l.IsoCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requested.Count == 0)
            return PublishResult.Failed([new ContentValidationError("name", "Name is required.", languages.Default()?.IsoCode)]);

        var allErrors = new List<ContentValidationError>();
        foreach (var language in requested)
        {
            if (node.GetCulture(language.IsoCode) is not { Exists: true })
                allErrors.Add(new("name", $"The content has no name in {language.Name}.", language.IsoCode));
            else
                allErrors.AddRange(Validate(node, language.IsoCode));
        }

        // A mandatory language has to be live before (or together with) any other one.
        var afterPublish = new HashSet<string>(node.PublishedCultures, StringComparer.OrdinalIgnoreCase);
        foreach (var language in requested) afterPublish.Add(language.IsoCode);
        foreach (var mandatory in languages.Where(l => l.IsMandatory && !afterPublish.Contains(l.IsoCode)))
            allErrors.Add(new("name", $"{mandatory.Name} is a mandatory language and must be published as well.", mandatory.IsoCode));

        if (allErrors.Count > 0) return PublishResult.Failed(allErrors.DistinctBy(e => (e.PropertyAlias, e.Culture)).ToList());

        var updated = new Dictionary<string, ContentCulture>(node.Cultures, StringComparer.OrdinalIgnoreCase);
        foreach (var language in requested)
        {
            var state = updated[language.IsoCode];
            state.IsPublished = true;
            state.PublishedName = state.Name;
            state.PublishedUrlSegment = state.UrlSegment;
            state.PublishedValues = new Dictionary<string, string?>(state.DraftValues, StringComparer.OrdinalIgnoreCase);
            state.PublishedAt = now;
            state.UpdatedAt = now;
        }
        node.Cultures = updated;

        // The shared values go live with every publish, whichever language triggered it.
        node.IsPublished = true;
        node.PublishedName = node.Name;
        node.PublishedUrlSegment = node.UrlSegment;
        node.PublishedTemplateAlias = node.TemplateAlias;
        node.PublishedValues = new Dictionary<string, string?>(node.DraftValues, StringComparer.OrdinalIgnoreCase);
        node.PublishedAt = now;
        node.UpdatedAt = now;
        await CommitAsync(db, ct);
        var published = (await GetAsync(id, ct))!;
        await EmitAsync(ContentEventKind.Published, published, requested.Select(l => l.IsoCode).ToList(), ct);
        return PublishResult.Ok(published);
    }

    public async Task<ContentNode?> UnpublishAsync(Guid id, string? culture = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var node = await WithType(db.ContentNodes).FirstOrDefaultAsync(n => n.Id == id, ct);
        if (node is null) return null;

        if (node.VariesByCulture && culture is not null)
        {
            var languages = await LanguageService.LoadAsync(db, ct);
            var language = languages.Match(culture) ?? throw new InvalidOperationException($"'{culture}' is not a configured language.");
            var updated = new Dictionary<string, ContentCulture>(node.Cultures, StringComparer.OrdinalIgnoreCase);
            if (updated.TryGetValue(language.IsoCode, out var state))
            {
                state.IsPublished = false;
                state.UpdatedAt = DateTime.UtcNow;
            }
            node.Cultures = updated;
            node.IsPublished = updated.Values.Any(c => c.IsPublished);
        }
        else
        {
            if (node.VariesByCulture)
            {
                var updated = new Dictionary<string, ContentCulture>(node.Cultures, StringComparer.OrdinalIgnoreCase);
                foreach (var state in updated.Values) state.IsPublished = false;
                node.Cultures = updated;
            }
            node.IsPublished = false;
        }

        node.UpdatedAt = DateTime.UtcNow;
        await CommitAsync(db, ct);
        var taken = await GetAsync(id, ct);
        await EmitAsync(ContentEventKind.Unpublished, taken, culture is null ? null : [culture], ct);
        return taken;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var node = await db.ContentNodes.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (node is null) return;
        var prefix = node.Path + ",";
        var descendants = await db.ContentNodes.Where(n => n.Path.StartsWith(prefix)).ToListAsync(ct);
        db.ContentNodes.RemoveRange(descendants);
        db.ContentNodes.Remove(node);
        await CommitAsync(db, ct);
        await EmitAsync(ContentEventKind.Deleted, node, null, ct);
    }

    public async Task MoveAsync(Guid id, int direction, CancellationToken ct = default)
    {
        if (direction == 0) return;
        await using var db = await factory.CreateDbContextAsync(ct);
        var node = await db.ContentNodes.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (node is null) return;
        var siblings = await db.ContentNodes.Where(n => n.ParentId == node.ParentId)
            .OrderBy(n => n.SortOrder).ThenBy(n => n.Name).ToListAsync(ct);
        var index = siblings.FindIndex(n => n.Id == id);
        var newIndex = Math.Clamp(index + Math.Sign(direction), 0, siblings.Count - 1);
        if (newIndex == index) return;
        siblings.RemoveAt(index);
        siblings.Insert(newIndex, node);
        for (var i = 0; i < siblings.Count; i++) siblings[i].SortOrder = i;
        await CommitAsync(db, ct);
        await EmitAsync(ContentEventKind.Moved, node, null, ct);
    }

    // ---- helpers ----

    /// <summary>Saves and drops the content cache, so the public site (and whoever handles the event raised next) sees the change.</summary>
    private async Task CommitAsync(DynCmsDbContext db, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        cache.InvalidateContent();
    }

    private static Language ResolveLanguage(IReadOnlyList<Language> languages, string? culture)
    {
        if (culture is not null)
            return languages.Match(culture) ?? throw new InvalidOperationException($"'{culture}' is not a configured language.");
        return languages.Default() ?? throw new InvalidOperationException("No language has been configured. Add one under Settings → Languages.");
    }

    /// <summary>Keeps the node-level name and URL segment in step with the default language, so the tree, search and the API show it.</summary>
    private static void MirrorDefaultCulture(ContentNode node, IReadOnlyList<Language> languages)
    {
        if (!node.VariesByCulture) return;
        var iso = languages.Default()?.IsoCode;
        var state = node.GetCulture(iso) ?? node.Cultures.Values.FirstOrDefault(c => c.Exists);
        if (state is null || !state.Exists) return;
        node.Name = state.Name;
        node.UrlSegment = state.UrlSegment;
    }

    /// <summary>
    /// A segment no sibling uses in <paramref name="culture"/> (the shared segment when null). Siblings that vary by
    /// culture are compared in that language; invariant siblings by their one segment.
    /// </summary>
    private static async Task<string> UniqueSegmentAsync(DynCmsDbContext db, Guid? parentId, string segment, Guid selfId, string? culture, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(segment)) segment = "page";
        var siblings = await db.ContentNodes.AsNoTracking().Include(n => n.ContentType)
            .Where(n => n.ParentId == parentId && n.Id != selfId)
            .ToListAsync(ct);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in siblings)
        {
            taken.Add(s.UrlSegment);
            if (culture is not null && s.GetCulture(culture) is { } c && !string.IsNullOrEmpty(c.UrlSegment)) taken.Add(c.UrlSegment);
        }
        if (!taken.Contains(segment)) return segment;
        var i = 2;
        while (taken.Contains($"{segment}-{i}")) i++;
        return $"{segment}-{i}";
    }
}
