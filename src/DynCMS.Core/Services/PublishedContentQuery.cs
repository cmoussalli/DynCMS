using DynCMS.Plugins.Models;

namespace DynCMS.Core.Services;

/// <summary>
/// The read side of the public site. Every call is answered from the <see cref="ContentCache"/> snapshot, so
/// resolving a route and everything a template asks for while rendering (children, ancestors, URLs…) costs no
/// database round trip. Nodes of the snapshot never leave this class: results are mapped to <see cref="PublishedContent"/>.
/// </summary>
public sealed class PublishedContentQuery(ContentCache cache, ICultureContext cultureContext) : IPublishedContentQuery
{
    /// <summary>What one query call works with: the snapshot, the language being served and whether drafts are shown.</summary>
    private sealed record Scope(ContentSnapshot Content, Language Culture, bool Preview)
    {
        public IReadOnlyList<Language> Languages => Content.Languages;
    }

    public async Task<PublishedContent?> GetByRouteAsync(string path, bool preview = false, string? culture = null, CancellationToken ct = default)
    {
        var segments = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var content = await cache.GetAsync(ct);
        var languages = content.Languages;

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
        var scope = new Scope(content, language, preview);

        var roots = Children(scope, null).ToList();
        if (roots.Count == 0) return null;

        var primary = roots[0];
        if (segments.Length == 0) return Visible(primary, scope) ? Map(primary, WithPrefix(scope, "/"), scope) : null;

        var node = Visible(primary, scope) ? Descend(scope, primary, segments, 0) : null;
        if (node is not null) return Map(node, WithPrefix(scope, "/" + string.Join('/', segments)), scope);

        foreach (var root in roots.Skip(1))
        {
            if (!Visible(root, scope)) continue;
            if (!string.Equals(Segment(root, scope), segments[0], StringComparison.OrdinalIgnoreCase)) continue;
            node = segments.Length == 1 ? root : Descend(scope, root, segments, 1);
            if (node is not null) return Map(node, WithPrefix(scope, "/" + string.Join('/', segments)), scope);
        }

        return null;
    }

    public async Task<PublishedContent?> GetByIdAsync(Guid id, bool preview = false, string? culture = null, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(preview, culture, ct);
        var node = Find(scope, id);
        if (node is null || !Visible(node, scope)) return null;
        return Map(node, BuildUrl(scope, node), scope);
    }

    public async Task<PublishedContent?> GetRootAsync(bool preview = false, string? culture = null, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(preview, culture, ct);
        var root = Children(scope, null).FirstOrDefault();
        return root is null || !Visible(root, scope) ? null : Map(root, WithPrefix(scope, "/"), scope);
    }

    public async Task<IReadOnlyList<PublishedContent>> GetChildrenAsync(Guid parentId, bool preview = false, string? culture = null, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(preview, culture, ct);
        var parent = Find(scope, parentId);
        if (parent is null || !Visible(parent, scope)) return [];
        var parentUrl = BuildUrl(scope, parent);
        return Children(scope, parentId).Where(c => Visible(c, scope)).Select(c => Map(c, Join(parentUrl, Segment(c, scope)), scope)).ToList();
    }

    public async Task<IReadOnlyList<PublishedContent>> GetAncestorsAsync(Guid id, bool preview = false, string? culture = null, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(preview, culture, ct);
        var node = Find(scope, id);
        if (node is null || !Visible(node, scope)) return [];
        return AncestorChain(scope, node).Select(a => Map(a, BuildUrl(scope, a), scope)).ToList();
    }

    public async Task<IReadOnlyList<PublishedContent>> GetByContentTypeAsync(string contentTypeAlias, bool preview = false, string? culture = null, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(preview, culture, ct);
        return scope.Content.OfType(contentTypeAlias)
            .Where(n => Candidate(n, scope) && Visible(n, scope))
            .OrderByDescending(n => n.PublishedAt ?? n.UpdatedAt)
            .Select(n => Map(n, BuildUrl(scope, n), scope))
            .ToList();
    }

    public async Task<string?> GetUrlAsync(Guid id, string? culture = null, CancellationToken ct = default)
    {
        var content = await cache.GetAsync(ct);
        var language = Resolve(content.Languages, culture);
        var node = content.Node(id);
        if (node is null) return null;
        var scope = new Scope(content, language, Preview: !node.IsPublishedIn(language.IsoCode));
        return BuildUrl(scope, node);
    }

    public async Task<IReadOnlyList<CultureLink>> GetCultureLinksAsync(Guid id, bool preview = false, string? culture = null, CancellationToken ct = default)
    {
        var content = await cache.GetAsync(ct);
        var languages = content.Languages;
        var current = Resolve(languages, culture);
        var node = content.Node(id);
        if (node is null || !(preview || node.IsPublished)) return [];

        var links = new List<CultureLink>();
        foreach (var language in languages)
        {
            var scope = new Scope(content, language, preview);
            if (!Visible(node, scope)) continue;
            links.Add(new CultureLink(language.IsoCode, language.Name, BuildUrl(scope, node), language.Is(current.IsoCode), language.IsDefault));
        }
        return links;
    }

    public async Task<string> ResolveCultureAsync(string? culture = null, CancellationToken ct = default) =>
        Resolve((await cache.GetAsync(ct)).Languages, culture).IsoCode;

    // ---- language resolution ----

    private async ValueTask<Scope> ScopeAsync(bool preview, string? culture, CancellationToken ct)
    {
        var content = await cache.GetAsync(ct);
        return new Scope(content, Resolve(content.Languages, culture), preview);
    }

    /// <summary>Explicit culture, else the one set on the culture context, else the language prefix of the current path, else the default.</summary>
    private Language Resolve(IReadOnlyList<Language> languages, string? culture) => cultureContext.ResolveLanguage(languages, culture);

    // ---- visibility and per-language projections ----

    /// <summary>Nodes that might be visible: everything in preview, otherwise those published in at least one language.</summary>
    private static bool Candidate(ContentNode n, Scope scope) => scope.Preview || n.IsPublished;

    /// <summary>The candidates under <paramref name="parentId"/> (the roots for null) in sibling order.</summary>
    private static IEnumerable<ContentNode> Children(Scope scope, Guid? parentId) =>
        scope.Content.Children(parentId).Where(n => Candidate(n, scope));

    private static ContentNode? Find(Scope scope, Guid id) =>
        scope.Content.Node(id) is { } n && Candidate(n, scope) ? n : null;

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

    private static ContentNode? Descend(Scope scope, ContentNode start, string[] segments, int index)
    {
        var current = start;
        for (var i = index; i < segments.Length; i++)
        {
            var seg = segments[i];
            var next = Children(scope, current.Id).FirstOrDefault(c => Visible(c, scope) && string.Equals(Segment(c, scope), seg, StringComparison.OrdinalIgnoreCase));
            if (next is null) return null;
            current = next;
        }
        return current;
    }

    private static List<ContentNode> AncestorChain(Scope scope, ContentNode node)
    {
        var chain = new List<ContentNode>();
        foreach (var id in node.AncestorIds)
        {
            if (Find(scope, id) is { } ancestor) chain.Add(ancestor);
        }
        return chain;
    }

    private static string BuildUrl(Scope scope, ContentNode node)
    {
        var chain = AncestorChain(scope, node);
        chain.Add(node);

        var primaryRootId = Children(scope, null).FirstOrDefault()?.Id;

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
