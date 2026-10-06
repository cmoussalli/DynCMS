using System.Collections.Concurrent;
using DynCMS.Core.Data;
using DynCMS.Plugins.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynCMS.Core.Services;

/// <summary>
/// The languages and the document types (with their properties) as last loaded. The objects are shared by every
/// request: read them, never change them, and hand out copies (<c>Clone()</c>) rather than the instances themselves.
/// </summary>
internal sealed class SchemaSnapshot
{
    private readonly Dictionary<Guid, ContentType> _typesById;

    /// <param name="languages">Default language first, as <see cref="LanguageService.LoadAsync"/> orders them.</param>
    /// <param name="types">In back-office order, properties in sort order.</param>
    public SchemaSnapshot(List<Language> languages, List<ContentType> types)
    {
        Languages = languages;
        ContentTypes = types;
        _typesById = types.ToDictionary(t => t.Id);
    }

    public IReadOnlyList<Language> Languages { get; }
    public IReadOnlyList<ContentType> ContentTypes { get; }

    public ContentType? Type(Guid id) => _typesById.GetValueOrDefault(id);

    public ContentType? TypeByAlias(string? alias)
    {
        if (string.IsNullOrEmpty(alias)) return null;
        return ContentTypes.FirstOrDefault(t => string.Equals(t.Alias, alias, StringComparison.Ordinal))
            ?? ContentTypes.FirstOrDefault(t => string.Equals(t.Alias, alias, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The whole content tree as last loaded, on top of the <see cref="SchemaSnapshot"/> it was loaded with, indexed for
/// the lookups the public site makes. Like the schema, the nodes are shared and read-only: results leave as a
/// mapped <see cref="PublishedContent"/>.
/// </summary>
internal sealed class ContentSnapshot
{
    private readonly Dictionary<Guid, ContentNode> _nodes;
    private readonly ILookup<Guid?, ContentNode> _children;
    private readonly ILookup<string, ContentNode> _byType;

    /// <param name="nodes">In sibling order (sort order, then name), so the per-parent lists come out sorted.</param>
    public ContentSnapshot(SchemaSnapshot schema, List<ContentNode> nodes)
    {
        Schema = schema;

        // Every node points at the one cached instance of its document type. A node whose type is gone (which the
        // foreign key rules out) could not be rendered and is left out.
        var usable = new List<ContentNode>(nodes.Count);
        foreach (var node in nodes)
        {
            if (schema.Type(node.ContentTypeId) is not { } type) continue;
            node.ContentType = type;
            usable.Add(node);
        }

        _nodes = usable.ToDictionary(n => n.Id);
        _children = usable.ToLookup(n => n.ParentId);
        _byType = usable.ToLookup(n => n.ContentType.Alias, StringComparer.OrdinalIgnoreCase);
    }

    public SchemaSnapshot Schema { get; }
    public IReadOnlyList<Language> Languages => Schema.Languages;
    public int NodeCount => _nodes.Count;

    public ContentNode? Node(Guid id) => _nodes.GetValueOrDefault(id);

    /// <summary>The children of <paramref name="parentId"/> (the roots for null) in sibling order.</summary>
    public IEnumerable<ContentNode> Children(Guid? parentId) => _children[parentId];

    /// <summary>Every node of the document type with <paramref name="alias"/>.</summary>
    public IEnumerable<ContentNode> OfType(string? alias) => string.IsNullOrEmpty(alias) ? [] : _byType[alias];
}

/// <summary>
/// The data cache: singleton holder of what the public site is resolved and rendered from, so a warm site answers
/// a page without a database round trip. Three parts, each loaded on first use and dropped by the writes that
/// affect it: the schema (languages and document types), the content tree, and the media items looked up by id.
/// </summary>
/// <remarks>
/// Every write through the content, document type, language, template and media services invalidates its part, and
/// the next reader loads it again, so a change is visible at once in every request and circuit. Changes made outside
/// this process are picked up by <see cref="DynCmsCacheOptions.RefreshInterval"/> or <see cref="Clear"/>.
/// Stored templates and the dictionary have caches of their own (<c>ITemplateRegistry</c>, <see cref="DictionaryCache"/>).
/// </remarks>
public sealed class ContentCache(
    IDbContextFactory<DynCmsDbContext> factory,
    IOptions<DynCmsOptions> options,
    ILogger<ContentCache> logger)
{
    /// <summary>Lookups of ids that do not exist are remembered too; past this many entries the media cache starts over.</summary>
    private const int MaxMediaEntries = 20_000;

    private sealed record Entry<T>(T Snapshot, long Version, long LoadedAt);

    private readonly DynCmsCacheOptions _options = options.Value.Cache;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile Entry<SchemaSnapshot>? _schema;
    private volatile Entry<ContentSnapshot>? _content;
    private long _schemaVersion;
    private long _contentVersion;

    private readonly ConcurrentDictionary<Guid, MediaItem?> _media = new();
    private long _mediaVersion;
    private long _mediaSince = Environment.TickCount64;

    /// <summary>False when <see cref="DynCmsCacheOptions.Enabled"/> is off: every lookup then reads the database.</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>The languages and document types, loaded when there are none cached or they were invalidated or have aged out.</summary>
    internal ValueTask<SchemaSnapshot> GetSchemaAsync(CancellationToken ct = default) =>
        CurrentSchema() is { } hit ? new ValueTask<SchemaSnapshot>(hit) : new ValueTask<SchemaSnapshot>(LoadSchemaAsync(ct));

    /// <summary>The content tree (with its schema), loaded when there is none cached or it was invalidated or has aged out.</summary>
    internal ValueTask<ContentSnapshot> GetAsync(CancellationToken ct = default) =>
        CurrentContent() is { } hit ? new ValueTask<ContentSnapshot>(hit) : new ValueTask<ContentSnapshot>(LoadContentAsync(ct));

    /// <summary>
    /// Drops the cached content tree; the next reader loads it again. Called after every content write, before
    /// events are raised, so whoever reacts to the change already sees it.
    /// </summary>
    public void InvalidateContent() => Interlocked.Increment(ref _contentVersion);

    /// <summary>Drops the cached languages and document types, and the content tree that is built on them.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _schemaVersion);
        Interlocked.Increment(ref _contentVersion);
    }

    /// <summary>Drops the cached media lookups.</summary>
    public void InvalidateMedia()
    {
        Interlocked.Increment(ref _mediaVersion);
        _media.Clear();
        Volatile.Write(ref _mediaSince, Environment.TickCount64);
    }

    /// <summary>Drops everything, for example after the database was switched, restored or changed behind DynCMS's back.</summary>
    public void Clear()
    {
        Invalidate();
        _schema = null;
        _content = null;
        InvalidateMedia();
    }

    private SchemaSnapshot? CurrentSchema() =>
        _schema is { } entry && Usable(entry.Version, ref _schemaVersion, entry.LoadedAt) ? entry.Snapshot : null;

    private ContentSnapshot? CurrentContent() =>
        _content is { } entry && Usable(entry.Version, ref _contentVersion, entry.LoadedAt) ? entry.Snapshot : null;

    private bool Usable(long version, ref long current, long loadedAt) =>
        _options.Enabled && version == Interlocked.Read(ref current) && !Expired(loadedAt);

    private bool Expired(long since) =>
        _options.RefreshInterval is { } interval && Environment.TickCount64 - since >= (long)interval.TotalMilliseconds;

    private async Task<SchemaSnapshot> LoadSchemaAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await ReadSchemaAsync(db, ct);
        }

        // One load at a time: requests that arrive while it runs wait for it and share the result.
        await _gate.WaitAsync(ct);
        try
        {
            if (CurrentSchema() is { } loaded) return loaded;
            await using var db = await factory.CreateDbContextAsync(ct);
            return await RefreshSchemaAsync(db, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ContentSnapshot> LoadContentAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return new ContentSnapshot(await ReadSchemaAsync(db, ct), await ReadNodesAsync(db, ct));
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (CurrentContent() is { } loaded) return loaded;

            await using var db = await factory.CreateDbContextAsync(ct);
            var schema = CurrentSchema() ?? await RefreshSchemaAsync(db, ct);

            // The version is read before the rows are: a write that lands while loading leaves this snapshot marked
            // as outdated, so the reader after this one loads again.
            var version = Interlocked.Read(ref _contentVersion);
            var snapshot = new ContentSnapshot(schema, await ReadNodesAsync(db, ct));
            _content = new Entry<ContentSnapshot>(snapshot, version, Environment.TickCount64);
            logger.LogDebug("Content cache loaded: {Nodes} content items", snapshot.NodeCount);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Loads and stores the schema. Call with the gate held.</summary>
    private async Task<SchemaSnapshot> RefreshSchemaAsync(DynCmsDbContext db, CancellationToken ct)
    {
        var version = Interlocked.Read(ref _schemaVersion);
        var schema = await ReadSchemaAsync(db, ct);
        _schema = new Entry<SchemaSnapshot>(schema, version, Environment.TickCount64);
        // The content tree points at the document types it was loaded with; it follows the new ones on its next load.
        Interlocked.Increment(ref _contentVersion);
        logger.LogDebug("Schema cache loaded: {Types} document types, {Languages} languages", schema.ContentTypes.Count, schema.Languages.Count);
        return schema;
    }

    private static async Task<SchemaSnapshot> ReadSchemaAsync(DynCmsDbContext db, CancellationToken ct)
    {
        var languages = await LanguageService.LoadAsync(db, ct);
        var types = await db.ContentTypes.AsNoTracking()
            .Include(t => t.Properties.OrderBy(p => p.SortOrder))
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .ToListAsync(ct);
        return new SchemaSnapshot(languages, types);
    }

    // Without the document type: each node is wired to the cached instance instead of a copy per row.
    private static Task<List<ContentNode>> ReadNodesAsync(DynCmsDbContext db, CancellationToken ct) =>
        db.ContentNodes.AsNoTracking()
            .OrderBy(n => n.SortOrder).ThenBy(n => n.Name)
            .ToListAsync(ct);

    // ---- media ----

    /// <summary>True when the item (or the fact that there is none) is cached. The item is the shared instance: copy before handing it out.</summary>
    internal bool TryGetMedia(Guid id, out MediaItem? item)
    {
        item = null;
        if (!_options.Enabled) return false;
        if (Expired(Volatile.Read(ref _mediaSince))) InvalidateMedia();
        return _media.TryGetValue(id, out item);
    }

    /// <summary>Read before querying the database and passed to <see cref="SetMedia"/>, so a result that a write has overtaken is not cached.</summary>
    internal long MediaVersion => Interlocked.Read(ref _mediaVersion);

    internal void SetMedia(Guid id, MediaItem? item, long version)
    {
        if (!_options.Enabled || version != MediaVersion) return;
        if (_media.Count >= MaxMediaEntries) _media.Clear();
        _media[id] = item;
        if (version != MediaVersion) _media.TryRemove(id, out _);
    }
}
