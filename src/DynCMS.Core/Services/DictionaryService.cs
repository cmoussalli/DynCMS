using DynCMS.Core.Data;
using DynCMS.Plugins.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DynCMS.Core.Services;

/// <summary>The dictionary and the languages as last loaded, so lookups while rendering do not touch the database.</summary>
public sealed class DictionarySnapshot
{
    public static readonly DictionarySnapshot Empty = new([], []);

    public DictionarySnapshot(IReadOnlyList<DictionaryItem> items, IReadOnlyList<Language> languages)
    {
        Items = items;
        Languages = languages;
        ByKey = items.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<DictionaryItem> Items { get; }
    public IReadOnlyList<Language> Languages { get; }
    public IReadOnlyDictionary<string, DictionaryItem> ByKey { get; }

    public string? Resolve(string? key, string? culture)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return ByKey.TryGetValue(key.Trim(), out var item) ? item.Resolve(culture, Languages) : null;
    }

    public IReadOnlyDictionary<string, string> ResolveAll(string? culture)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
        {
            if (item.Resolve(culture, Languages) is { } text) result[item.Key] = text;
        }
        return result;
    }
}

/// <summary>
/// Singleton holder of the <see cref="DictionarySnapshot"/>. Writes through <see cref="DictionaryService"/> and
/// <see cref="LanguageService"/> reload it, so every circuit and request sees a change at once.
/// </summary>
public sealed class DictionaryCache(IDbContextFactory<DynCmsDbContext> factory, ILogger<DictionaryCache> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile DictionarySnapshot? _snapshot;
    private int _loading;

    /// <summary>The snapshot as last loaded, or null before the first load.</summary>
    public DictionarySnapshot? Current => _snapshot;

    public async Task<DictionarySnapshot> GetAsync(CancellationToken ct = default) =>
        _snapshot ?? await ReloadAsync(ct);

    public async Task<DictionarySnapshot> ReloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var languages = await LanguageService.LoadAsync(db, ct);
            var items = DictionaryService.Order(await DictionaryService.LoadAsync(db, ct));
            var snapshot = new DictionarySnapshot(items, languages);
            _snapshot = snapshot;
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts a load in the background when nothing is loaded yet (used by the synchronous lookups).</summary>
    public void EnsureLoading()
    {
        if (_snapshot is not null || Interlocked.Exchange(ref _loading, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await ReloadAsync(); }
            catch (Exception ex) { logger.LogWarning(ex, "The dictionary could not be loaded"); }
            finally { Interlocked.Exchange(ref _loading, 0); }
        });
    }

    public void Invalidate() => _snapshot = null;
}

public sealed class DictionaryService(
    IDbContextFactory<DynCmsDbContext> factory,
    DictionaryCache cache,
    ICultureContext cultureContext) : IDictionaryService
{
    public async Task<IReadOnlyList<DictionaryItem>> GetAllAsync(CancellationToken ct = default) =>
        (await cache.GetAsync(ct)).Items;

    /// <summary>Loads every item with a context the caller already has open, translations re-wrapped to ignore case.</summary>
    internal static async Task<List<DictionaryItem>> LoadAsync(DynCmsDbContext db, CancellationToken ct)
    {
        var items = await db.DictionaryItems.AsNoTracking().ToListAsync(ct);
        foreach (var item in items) Normalize(item);
        return items;
    }

    /// <summary>Tree order: parents before their children, siblings by sort order then key, with <see cref="DictionaryItem.Level"/> set.</summary>
    internal static List<DictionaryItem> Order(List<DictionaryItem> items)
    {
        var byParent = items.ToLookup(i => i.ParentId);
        var ids = items.Select(i => i.Id).ToHashSet();
        var result = new List<DictionaryItem>(items.Count);
        var seen = new HashSet<Guid>();

        void Walk(Guid? parentId, int level)
        {
            foreach (var item in byParent[parentId].OrderBy(i => i.SortOrder).ThenBy(i => i.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!seen.Add(item.Id)) continue;
                item.Level = level;
                result.Add(item);
                Walk(item.Id, level + 1);
            }
        }

        Walk(null, 0);
        // Orphans (a parent that no longer exists) are listed at the root rather than lost.
        foreach (var item in items.Where(i => i.ParentId is { } p && !ids.Contains(p)).OrderBy(i => i.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add(item.Id)) continue;
            item.Level = 0;
            result.Add(item);
            Walk(item.Id, 1);
        }
        return result;
    }

    private static void Normalize(DictionaryItem item)
    {
        if (item.Translations.Comparer != StringComparer.OrdinalIgnoreCase)
            item.Translations = new Dictionary<string, string?>(item.Translations, StringComparer.OrdinalIgnoreCase);
    }

    // Single items are handed out as copies: the back office edits them in place before saving.
    public async Task<DictionaryItem?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await cache.GetAsync(ct)).Items.FirstOrDefault(i => i.Id == id)?.Clone();

    public async Task<DictionaryItem?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return (await cache.GetAsync(ct)).ByKey.GetValueOrDefault(key.Trim())?.Clone();
    }

    public async Task<int> CountAsync(CancellationToken ct = default) => (await cache.GetAsync(ct)).Items.Count;

    public async Task<DictionaryItem> SaveAsync(DictionaryItem item, CancellationToken ct = default)
    {
        var key = item.Key?.Trim() ?? string.Empty;
        if (key.Length == 0) throw new InvalidOperationException("A dictionary item needs a key, for example 'blog.readMore'.");
        if (key.Length > DictionaryItem.MaxKeyLength) throw new InvalidOperationException($"The key may be at most {DictionaryItem.MaxKeyLength} characters long.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var all = await db.DictionaryItems.ToListAsync(ct);
        foreach (var i in all) Normalize(i);
        var languages = await LanguageService.LoadAsync(db, ct);

        var existing = all.FirstOrDefault(i => i.Id == item.Id);
        if (all.Any(i => i.Id != item.Id && i.Is(key)))
            throw new InvalidOperationException($"A dictionary item with the key '{key}' already exists.");

        if (item.ParentId is { } parentId)
        {
            var parent = all.FirstOrDefault(i => i.Id == parentId)
                ?? throw new InvalidOperationException("The parent item does not exist.");
            if (parent.Id == item.Id) throw new InvalidOperationException("An item cannot be filed under itself.");
            if (existing is not null && DescendantIds(all, existing.Id).Contains(parent.Id))
                throw new InvalidOperationException($"'{parent.Key}' is filed under '{existing.Key}', so it cannot become its parent.");
        }

        if (existing is null)
        {
            existing = new DictionaryItem
            {
                Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id,
                CreatedAt = DateTime.UtcNow,
                SortOrder = item.SortOrder != 0 ? item.SortOrder : NextSortOrder(all, item.ParentId)
            };
            db.DictionaryItems.Add(existing);
        }
        else if (item.SortOrder != 0 || existing.ParentId != item.ParentId)
        {
            existing.SortOrder = item.SortOrder != 0 ? item.SortOrder : NextSortOrder(all, item.ParentId);
        }

        existing.Key = key;
        existing.ParentId = item.ParentId;
        existing.UpdatedAt = DateTime.UtcNow;

        // Keep only real languages, with the ISO code spelled as the language is; drop empty texts.
        var translations = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, text) in item.Translations)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var language = languages.FirstOrDefault(l => l.Is(code)) ?? languages.Match(code);
            if (language is null) continue;
            translations[language.IsoCode] = text.Trim();
        }
        existing.Translations = translations;

        await db.SaveChangesAsync(ct);
        var snapshot = await cache.ReloadAsync(ct);
        return snapshot.Items.First(i => i.Id == existing.Id);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var all = await db.DictionaryItems.ToListAsync(ct);
        var target = all.FirstOrDefault(i => i.Id == id);
        if (target is null) return;

        var doomed = DescendantIds(all, id);
        doomed.Add(id);
        db.DictionaryItems.RemoveRange(all.Where(i => doomed.Contains(i.Id)));
        await db.SaveChangesAsync(ct);
        await cache.ReloadAsync(ct);
    }

    public async Task<string?> GetValueAsync(string key, string? culture = null, CancellationToken ct = default)
    {
        var snapshot = await cache.GetAsync(ct);
        return snapshot.Resolve(key, Culture(snapshot, culture));
    }

    public async Task<IReadOnlyDictionary<string, string>> GetValuesAsync(string? culture = null, CancellationToken ct = default)
    {
        var snapshot = await cache.GetAsync(ct);
        return snapshot.ResolveAll(Culture(snapshot, culture));
    }

    public string? GetValue(string key, string? culture = null)
    {
        var snapshot = cache.Current;
        if (snapshot is null)
        {
            cache.EnsureLoading();
            return null;
        }
        return snapshot.Resolve(key, Culture(snapshot, culture));
    }

    public IReadOnlyDictionary<string, string> GetValues(string? culture = null)
    {
        var snapshot = cache.Current;
        if (snapshot is null)
        {
            cache.EnsureLoading();
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        return snapshot.ResolveAll(Culture(snapshot, culture));
    }

    public Task RefreshAsync(CancellationToken ct = default) => cache.ReloadAsync(ct);

    /// <summary>The explicit culture, else the language of the current request (see <see cref="ICultureContext"/>).</summary>
    private string Culture(DictionarySnapshot snapshot, string? culture) =>
        cultureContext.ResolveLanguage(snapshot.Languages, culture).IsoCode;

    private static int NextSortOrder(IEnumerable<DictionaryItem> all, Guid? parentId)
    {
        var siblings = all.Where(i => i.ParentId == parentId).ToList();
        return siblings.Count == 0 ? 0 : siblings.Max(i => i.SortOrder) + 1;
    }

    /// <summary>The ids of everything filed under <paramref name="id"/>, at any depth.</summary>
    internal static HashSet<Guid> DescendantIds(IReadOnlyList<DictionaryItem> all, Guid id)
    {
        var result = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(id);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var child in all.Where(i => i.ParentId == current))
            {
                if (result.Add(child.Id)) queue.Enqueue(child.Id);
            }
        }
        return result;
    }
}
