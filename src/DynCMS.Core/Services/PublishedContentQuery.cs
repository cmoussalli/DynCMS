using DynCMS.Core.Data;
using DynCMS.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DynCMS.Core.Services;

public sealed class PublishedContentQuery(IDbContextFactory<DynCmsDbContext> factory, ICultureContext cultureContext) : IPublishedContentQuery
{
    /// <summary>What one query call works with: the context, the languages and the language being served.</summary>
    private sealed record Scope(DynCmsDbContext Db, List<Language> Languages, Language Culture, bool Preview);

    public async Task<PublishedContent?> GetByRouteAsync(string path, bool preview = false, string? culture = null, CancellationToken ct = default)
    {
        var segments = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await using var db = await factory.CreateDbContextAsync(ct);
        var languages = await LanguageService.LoadAsync(db, ct);

        // A language prefix in the path wins over everything else: /de/ueber-uns is German.
        Language language;
        if (segments.Length > 0 && languages.Match(segments[0]) is { } fromPath)
        {
            language = fromPath;
            segments = segments[1..];
        }
        else
        {
            language = Resolve(languages, culture);
        }
        var scope = new Scope(db, languages, language, preview);

        var roots = await Candidates(db, preview).Where(n => n.ParentId == null)
            .OrderBy(n => n.SortOrder).ThenBy(n => n.Name).ToListAsync(ct);
        if (roots.Count == 0) return null;

        var primary = roots[0];
        if (segments.Length == 0) return Visible(primary, scope) ? Map(primary, WithPrefix(scope, "/"), scope) : null;

        var node = Visible(primary, scope) ? await DescendAsync(scope, primary, segments, 0, ct) : null;
        if (node is not null) return Map(node, WithPrefix(scope, "/" + string.Join('/', segments)), scope);

        foreach (var root in roots.Skip(1))
        {
            if (!Visible(root, scope)) continue;
            if (!string.Equals(Segment(root, scope), segments[0], StringComparison.OrdinalIgnoreCase)) continue;
            node = segments.Length == 1 ? root : await DescendAsync(scope, root, segments, 1, ct);
            if (node is not null) return Map(node, WithPrefix(scope, "/" + string.Join('/', segments)), scope);
        }

        return null;
    }

    public async Task<PublishedContent?> GetByIdAsync(Guid id, bool preview = false, string? culture = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var scope = await ScopeAsync(db, preview, culture, ct);
        var node = await Candidates(db, preview).FirstOrDefaultAsync(n => n.Id == id, ct);
        if (node is null || !Visible(node, scope)) return null;
        return Map(node, await BuildUrlAsync(scope, node, ct), scope);
    }

    public async Task<PublishedContent?> GetRootAsync(bool preview = false, string? culture = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var scope = await ScopeAsync(db, preview, culture, ct);
        var root = await Candidates(db, preview).Where(n => n.ParentId == null)
            .OrderBy(n => n.SortOrder).ThenBy(n => n.Name).FirstOrDefaultAsync(ct);
        return root is null || !Visible(root, scope) ? null : Map(root, WithPrefix(scope, "/"), scope);
    }

    public async Task<IReadOnlyList<PublishedContent>> GetChildrenAsync(Guid parentId, bool preview = false, string? culture = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var scope = await ScopeAsync(db, preview, culture, ct);
        var parent = await Candidates(db, preview).FirstOrDefaultAsync(n => n.Id == parentId, ct);
        if (parent is null || !Visible(parent, scope)) return [];
        var parentUrl = await BuildUrlAsync(scope, parent, ct);
        var children = await Candidates(db, preview).Where(n => n.ParentId == parentId)
            .OrderBy(n => n.SortOrder).ThenBy(n => n.Name).ToListAsync(ct);
        return children.Where(c => Visible(c, scope)).Select(c => Map(c, Join(parentUrl, Segment(c, scope)), scope)).ToList();
    }

    public async Task<IReadOnlyList<PublishedContent>> GetAncestorsAsync(Guid id, bool preview = false, string? culture = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var scope = await ScopeAsync(db, preview, culture, ct);
        var node = await Candidates(db, preview).FirstOrDefaultAsync(n => n.Id == id, ct);
        if (node is null || !Visible(node, scope)) return [];
        var chain = await AncestorChainAsync(scope, node, ct);
        var result = new List<PublishedContent>();
        foreach (var a in chain) result.Add(Map(a, await BuildUrlAsync(scope, a, ct), scope));
        return result;
    }

    public async Task<IReadOnlyList<PublishedContent>> GetByContentTypeAsync(string contentTypeAlias, bool preview = false, string? culture = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var scope = await ScopeAsync(db, preview, culture, ct);
        var nodes = await Candidates(db, preview).Where(n => n.ContentType.Alias == contentTypeAlias)
            .OrderByDescending(n => n.PublishedAt ?? n.UpdatedAt).ToListAsync(ct);
        var result = new List<PublishedContent>();
        foreach (var n in nodes.Where(n => Visible(n, scope))) result.Add(Map(n, await BuildUrlAsync(scope, n, ct), scope));
        return result;
    }

    public async Task<string?> GetUrlAsync(Guid id, string? culture = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var languages = await LanguageService.LoadAsync(db, ct);
        var language = Resolve(languages, culture);
        var node = await db.ContentNodes.AsNoTracking().Include(n => n.ContentType).FirstOrDefaultAsync(n => n.Id == id, ct);
        if (node is null) return null;
        var scope = new Scope(db, languages, language, Preview: !node.IsPublishedIn(language.IsoCode));
        return await BuildUrlAsync(scope, node, ct);
    }

    public async Task<IReadOnlyList<CultureLink>> GetCultureLinksAsync(Guid id, bool preview = false, string? culture = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var languages = await LanguageService.LoadAsync(db, ct);
        var current = Resolve(languages, culture);
        var node = await Candidates(db, preview).FirstOrDefaultAsync(n => n.Id == id, ct);
        if (node is null) return [];

        var links = new List<CultureLink>();
        foreach (var language in languages)
        {
            var scope = new Scope(db, languages, language, preview);
            if (!Visible(node, scope)) continue;
            links.Add(new CultureLink(language.IsoCode, language.Name, await BuildUrlAsync(scope, node, ct), language.Is(current.IsoCode), language.IsDefault));
        }
        return links;
    }

    public async Task<string> ResolveCultureAsync(string? culture = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return Resolve(await LanguageService.LoadAsync(db, ct), culture).IsoCode;
    }

    // ---- language resolution ----

    private async Task<Scope> ScopeAsync(DynCmsDbContext db, bool preview, string? culture, CancellationToken ct)
    {
        var languages = await LanguageService.LoadAsync(db, ct);
        return new Scope(db, languages, Resolve(languages, culture), preview);
    }

    /// <summary>Explicit culture, else the one set on the culture context, else the language prefix of the current path, else the default.</summary>
    private Language Resolve(List<Language> languages, string? culture) => cultureContext.ResolveLanguage(languages, culture);

    // ---- visibility and per-language projections ----

    /// <summary>Nodes that might be visible: everything in preview, otherwise those published in at least one language.</summary>
    private static IQueryable<ContentNode> Candidates(DynCmsDbContext db, bool preview)
    {
        var q = db.ContentNodes.AsNoTracking().Include(n => n.ContentType).ThenInclude(t => t.Properties);
        return preview ? q : q.Where(n => n.IsPublished);
    }

    /// <summary>Whether the node exists in the scope's language: invariant content always does (when published); variants must be published (or, in preview, created) in it.</summary>
    private static bool Visible(ContentNode n, Scope scope)
    {
        if (!n.VariesByCulture) return scope.Preview || n.IsPublished;
        var state = n.GetCulture(scope.Culture.IsoCode);
        return scope.Preview ? state is { Exists: true } : state is { IsPublished: true };
    }

    private static string Segment(ContentNode n, Scope scope)
    {
        if (n.VariesByCulture && n.GetCulture(scope.Culture.IsoCode) is { } c)
        {
            var segment = scope.Preview ? c.UrlSegment : (c.PublishedUrlSegment ?? c.UrlSegment);
            if (!string.IsNullOrEmpty(segment)) return segment;
        }
        return scope.Preview ? n.UrlSegment : (n.PublishedUrlSegment ?? n.UrlSegment);
    }

    private static string Name(ContentNode n, Scope scope)
    {
        if (n.VariesByCulture && n.GetCulture(scope.Culture.IsoCode) is { } c)
        {
            var name = scope.Preview ? c.Name : (c.PublishedName ?? c.Name);
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return scope.Preview ? n.Name : (n.PublishedName ?? n.Name);
    }

    /// <summary>The shared values plus, for content that varies, the language's values with the fallback chain applied to empty ones.</summary>
    private static Dictionary<string, string?> Values(ContentNode n, Scope scope)
    {
        var values = new Dictionary<string, string?>(scope.Preview ? n.DraftValues : n.PublishedValues, StringComparer.OrdinalIgnoreCase);
        if (!n.VariesByCulture) return values;

        var chain = scope.Languages.FallbackChain(scope.Culture.IsoCode);
        foreach (var property in n.ContentType.Properties.Where(p => p.VariesByCulture))
        {
            string? value = null;
            foreach (var iso in chain)
            {
                var state = n.GetCulture(iso);
                if (state is null || (!scope.Preview && !state.IsPublished)) continue;
                var candidate = (scope.Preview ? state.DraftValues : state.PublishedValues).GetValueOrDefault(property.Alias);
                if (!string.IsNullOrWhiteSpace(candidate)) { value = candidate; break; }
            }
            values[property.Alias] = value;
        }
        return values;
    }

    private static string Prefix(Scope scope) => scope.Culture.IsDefault ? string.Empty : "/" + scope.Culture.UrlPrefix;

    private static string WithPrefix(Scope scope, string url)
    {
        var prefix = Prefix(scope);
        if (prefix.Length == 0) return url;
        return url == "/" ? prefix : prefix + url;
    }

    private static string Join(string parentUrl, string segment) =>
        parentUrl.EndsWith('/') ? parentUrl + segment : parentUrl + "/" + segment;

    private static async Task<ContentNode?> DescendAsync(Scope scope, ContentNode start, string[] segments, int index, CancellationToken ct)
    {
        var current = start;
        for (var i = index; i < segments.Length; i++)
        {
            var seg = segments[i];
            var children = await Candidates(scope.Db, scope.Preview).Where(n => n.ParentId == current.Id).ToListAsync(ct);
            var next = children.FirstOrDefault(c => Visible(c, scope) && string.Equals(Segment(c, scope), seg, StringComparison.OrdinalIgnoreCase));
            if (next is null) return null;
            current = next;
        }
        return current;
    }

    private static async Task<List<ContentNode>> AncestorChainAsync(Scope scope, ContentNode node, CancellationToken ct)
    {
        var ids = node.AncestorIds.ToList();
        if (ids.Count == 0) return [];
        var nodes = await Candidates(scope.Db, scope.Preview).Where(n => ids.Contains(n.Id)).ToListAsync(ct);
        return ids.Select(i => nodes.FirstOrDefault(n => n.Id == i)).Where(n => n is not null).Cast<ContentNode>().ToList();
    }

    private static async Task<string> BuildUrlAsync(Scope scope, ContentNode node, CancellationToken ct)
    {
        var chain = await AncestorChainAsync(scope, node, ct);
        chain.Add(node);

        var primaryRootId = await Candidates(scope.Db, scope.Preview).Where(n => n.ParentId == null)
            .OrderBy(n => n.SortOrder).ThenBy(n => n.Name).Select(n => (Guid?)n.Id).FirstOrDefaultAsync(ct);

        var parts = chain[0].Id == primaryRootId ? chain.Skip(1) : chain;
        var url = "/" + string.Join('/', parts.Select(n => Segment(n, scope)));
        return WithPrefix(scope, url == "/" ? url : url.TrimEnd('/'));
    }

    private static PublishedContent Map(ContentNode n, string url, Scope scope)
    {
        var state = n.VariesByCulture ? n.GetCulture(scope.Culture.IsoCode) : null;
        var available = n.VariesByCulture
            ? scope.Languages.Where(l => scope.Preview ? n.GetCulture(l.IsoCode) is { Exists: true } : n.IsPublishedIn(l.IsoCode)).Select(l => l.IsoCode).ToList()
            : scope.Languages.Select(l => l.IsoCode).ToList();

        return new PublishedContent
        {
            Id = n.Id,
            ParentId = n.ParentId,
            Name = Name(n, scope),
            UrlSegment = Segment(n, scope),
            Url = url,
            ContentTypeAlias = n.ContentType.Alias,
            ContentTypeName = n.ContentType.Name,
            TemplateAlias = scope.Preview ? n.TemplateAlias : (n.PublishedTemplateAlias ?? n.TemplateAlias),
            Level = n.Level,
            SortOrder = n.SortOrder,
            CreatedAt = n.CreatedAt,
            UpdatedAt = state?.UpdatedAt is { } u && u > n.UpdatedAt ? u : n.UpdatedAt,
            PublishedAt = state?.PublishedAt ?? n.PublishedAt,
            IsPreview = scope.Preview,
            Culture = scope.Culture.IsoCode,
            VariesByCulture = n.VariesByCulture,
            Cultures = available,
            Values = Values(n, scope)
        };
    }
}
