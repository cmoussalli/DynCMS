using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using DynCMS.Core.Helpers;
using DynCMS.Core.Models;
using DynCMS.Core.Services;
using Fluid;
using Fluid.Values;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace DynCMS.Core.Templates;

public sealed record TemplateValidationResult(bool IsValid, string? Error)
{
    public static readonly TemplateValidationResult Valid = new(true, null);
}

/// <summary>
/// Services a stored template may reach while rendering (content queries, media URLs and the dictionary), and the
/// request being answered (<see cref="Request"/>; null when there is none, as in the editor's preview).
/// </summary>
public sealed record LiquidRenderScope(IPublishedContentQuery Query, IMediaService Media, IDictionaryService Dictionary, bool Preview, LiquidRequest? Request = null);

/// <summary>
/// The request a stored template is rendered for: the path and the query string, which the template reads as
/// <c>request.path</c>, <c>request.url</c>, <c>request.query.page</c> and <c>request.query_string</c>. The
/// <c>paginate</c> filter reads <c>?page=</c> from it and builds the links to the other pages on it.
/// </summary>
public sealed record LiquidRequest(string Path, IReadOnlyDictionary<string, string> Query)
{
    /// <summary>The request for <paramref name="uri"/>: its path and query string.</summary>
    public static LiquidRequest FromUri(Uri uri) => new(
        uri.AbsolutePath,
        new Dictionary<string, string>(
            Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.ToString())),
            StringComparer.OrdinalIgnoreCase));

    /// <summary>A request for <paramref name="path"/> with no query string, used where there is no real request.</summary>
    public static LiquidRequest ForPath(string path) => new(path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The path with the query string, e.g. <c>/blog?page=2</c>.</summary>
    public string Url => Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(Path, Query.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)));

    /// <summary>The page number requested with <c>?page=</c>, null when there is none.</summary>
    public int? Page => Query.TryGetValue(Paging.PageParameter, out var v) ? Paging.ParsePage(v) : null;

    /// <summary>The URL of page <paramref name="page"/> of a list on this request's path, keeping the other query parameters.</summary>
    public string PageUrl(int page) => Paging.PageUrl(Path, Query.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)), page);
}

/// <summary>Thrown when a stored template fails to parse or render.</summary>
public sealed class TemplateRenderException(string alias, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Alias { get; } = alias;
}

/// <summary>
/// Parses and renders the Liquid templates stored in the database. Singleton: holds the parser, the shared
/// <see cref="TemplateOptions"/> (member access, filters, partials) and a cache of parsed templates.
/// </summary>
public interface ILiquidTemplateEngine
{
    /// <summary>Checks the Liquid syntax without rendering.</summary>
    TemplateValidationResult Validate(string source);
    /// <summary>Renders <paramref name="template"/> for <paramref name="content"/> to HTML.</summary>
    Task<string> RenderAsync(Template template, PublishedContent content, LiquidRenderScope scope, CancellationToken ct = default);
    /// <summary>
    /// Renders Liquid that is not (or not yet) stored, used by the back office to preview unsaved edits.
    /// <paramref name="alias"/> only names the source in error messages.
    /// </summary>
    Task<string> RenderSourceAsync(string alias, string source, PublishedContent content, LiquidRenderScope scope, CancellationToken ct = default);
}

/// <summary>Renders the stored template assigned to a content item. Scoped: carries the scoped content and media services.</summary>
public interface IStoredTemplateRenderer
{
    /// <summary>
    /// Renders the stored template with <paramref name="alias"/>. <paramref name="request"/> is the request being
    /// answered (the template's <c>request</c> variable; the page's own URL with no query string when null).
    /// Throws <see cref="TemplateRenderException"/> when the template is missing or fails.
    /// </summary>
    Task<string> RenderAsync(string alias, PublishedContent content, LiquidRequest? request = null, CancellationToken ct = default);
}

public sealed class StoredTemplateRenderer(
    ITemplateRegistry registry,
    ILiquidTemplateEngine engine,
    IPublishedContentQuery query,
    IMediaService media,
    IDictionaryService dictionary) : IStoredTemplateRenderer
{
    public Task<string> RenderAsync(string alias, PublishedContent content, LiquidRequest? request = null, CancellationToken ct = default)
    {
        var template = registry.GetStored(alias)
            ?? throw new TemplateRenderException(alias, $"There is no stored template with the alias '{alias}'.");
        return engine.RenderAsync(template, content, new LiquidRenderScope(query, media, dictionary, content.IsPreview, request), ct);
    }
}

public sealed class FluidTemplateEngine : ILiquidTemplateEngine
{
    private const string ScopeKey = "dyncms.scope";

    private readonly FluidParser _parser = new(new FluidParserOptions { AllowParentheses = true });
    private readonly TemplateOptions _options;
    private readonly ConcurrentDictionary<Guid, (string Source, IFluidTemplate Template)> _cache = new();

    public FluidTemplateEngine(ITemplateRegistry registry)
    {
        _options = new TemplateOptions
        {
            MaxSteps = 250_000,
            MaxRecursion = 64,
            CultureInfo = CultureInfo.InvariantCulture,
            FileProvider = new StoredTemplateFileProvider(registry)
        };

        _options.MemberAccessStrategy.Register<PublishedContent, object?>(ContentMemberAsync);
        _options.MemberAccessStrategy.Register<SiteModel, object?>(SiteMemberAsync);

        _options.Filters.AddFilter("media_url", MediaUrlAsync);
        _options.Filters.AddFilter("media", MediaAsync);
        _options.Filters.AddFilter("content_by_id", ContentByIdAsync);
        _options.Filters.AddFilter("content_url", ContentUrlAsync);
        _options.Filters.AddFilter("content_of_type", ContentOfTypeAsync);
        _options.Filters.AddFilter("children_of", ChildrenOfAsync);
        _options.Filters.AddFilter("json", ParseJson);
        _options.Filters.AddFilter("tags", ParseJson);
        _options.Filters.AddFilter("dictionary", DictionaryAsync);
        _options.Filters.AddFilter("translate", DictionaryAsync);
        _options.Filters.AddFilter("paginate", Paginate);
        _options.Filters.AddFilter("page_url", PageUrl);
    }

    public TemplateValidationResult Validate(string source) =>
        _parser.TryParse(source ?? string.Empty, out _, out var error)
            ? TemplateValidationResult.Valid
            : new TemplateValidationResult(false, error);

    public Task<string> RenderAsync(Template template, PublishedContent content, LiquidRenderScope scope, CancellationToken ct = default) =>
        RenderCompiledAsync(template.Alias, GetCompiled(template), content, scope);

    public Task<string> RenderSourceAsync(string alias, string source, PublishedContent content, LiquidRenderScope scope, CancellationToken ct = default)
    {
        if (!_parser.TryParse(source ?? string.Empty, out var parsed, out var error))
            throw new TemplateRenderException(alias, $"Syntax error: {error}");
        return RenderCompiledAsync(alias, parsed, content, scope);
    }

    private async Task<string> RenderCompiledAsync(string alias, IFluidTemplate compiled, PublishedContent content, LiquidRenderScope scope)
    {
        var context = new TemplateContext(_options, StringComparer.OrdinalIgnoreCase);
        context.AmbientValues[ScopeKey] = scope;
        // Dates and numbers format in the language of the page: {{ content.publishDate | date: "%d %B %Y" }} reads "3. März 2026" on /de/.
        context.CultureInfo = CultureOf(content.Culture);
        context.SetValue("content", FluidValue.Create(content, _options));
        context.SetValue("site", FluidValue.Create(new SiteModel(), _options));
        context.SetValue("preview", BooleanValue.Create(content.IsPreview));
        context.SetValue("request", FluidValue.Create(RequestModel(scope.Request ?? LiquidRequest.ForPath(content.Url)), _options));

        try
        {
            return await compiled.RenderAsync(context, HtmlEncoder.Default, isolateContext: false);
        }
        catch (Exception ex) when (ex is not TemplateRenderException)
        {
            throw new TemplateRenderException(alias, $"Template '{alias}' failed to render: {ex.Message}", ex);
        }
    }

    private static CultureInfo CultureOf(string? isoCode)
    {
        if (string.IsNullOrWhiteSpace(isoCode)) return CultureInfo.InvariantCulture;
        try { return CultureInfo.GetCultureInfo(isoCode); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    private IFluidTemplate GetCompiled(Template template)
    {
        if (_cache.TryGetValue(template.Id, out var hit) && hit.Source == template.Content) return hit.Template;

        if (!_parser.TryParse(template.Content ?? string.Empty, out var parsed, out var error))
            throw new TemplateRenderException(template.Alias, $"Template '{template.Alias}' has a syntax error: {error}");

        _cache[template.Id] = (template.Content ?? string.Empty, parsed);
        return parsed;
    }

    // ---- content members --------------------------------------------------------------------------

    /// <summary>Marker for the <c>site</c> variable.</summary>
    private sealed class SiteModel;

    private static LiquidRenderScope Scope(TemplateContext ctx) =>
        ctx.AmbientValues.TryGetValue(ScopeKey, out var s) && s is LiquidRenderScope scope
            ? scope
            : throw new InvalidOperationException("The template is rendered outside a DynCMS render scope.");

    private static string Normalize(string name) => name.Replace("_", string.Empty).ToLowerInvariant();

    private static async Task<object?> ContentMemberAsync(PublishedContent c, string name, TemplateContext ctx)
    {
        switch (Normalize(name))
        {
            case "id": return c.Id;
            case "name": return c.Name;
            case "url": return c.Url;
            case "urlsegment": return c.UrlSegment;
            case "contenttype": case "contenttypealias": return c.ContentTypeAlias;
            case "contenttypename": return c.ContentTypeName;
            case "template": case "templatealias": return c.TemplateAlias;
            case "level": return c.Level;
            case "sortorder": return c.SortOrder;
            case "createdat": return c.CreatedAt;
            case "updatedat": return c.UpdatedAt;
            case "publishedat": return c.PublishedAt;
            case "ispreview": return c.IsPreview;
            case "parentid": return c.ParentId;
            case "values": return c.Values;
            case "culture": return c.Culture;
            case "variesbyculture": return c.VariesByCulture;
            case "availablecultures": return c.Cultures;
            case "cultures":
            {
                // The same page in every language it exists in: iso_code, name, url, is_current, is_default.
                var scope = Scope(ctx);
                return (await scope.Query.GetCultureLinksAsync(c.Id, scope.Preview, c.Culture)).Select(CultureLinkModel).ToList();
            }
            case "children":
            {
                var scope = Scope(ctx);
                return await scope.Query.GetChildrenAsync(c.Id, scope.Preview, c.Culture);
            }
            case "ancestors":
            {
                var scope = Scope(ctx);
                return await scope.Query.GetAncestorsAsync(c.Id, scope.Preview, c.Culture);
            }
            case "parent":
            {
                if (c.ParentId is not Guid parentId) return null;
                var scope = Scope(ctx);
                return await scope.Query.GetByIdAsync(parentId, scope.Preview, c.Culture);
            }
            case "siblings":
            {
                if (c.ParentId is not Guid parentId) return null;
                var scope = Scope(ctx);
                return (await scope.Query.GetChildrenAsync(parentId, scope.Preview, c.Culture)).Where(s => s.Id != c.Id).ToList();
            }
        }

        // Anything else is a property value: {{ content.bodyText }}, {{ content.publish_date }} …
        var raw = c[name] ?? c.Values.FirstOrDefault(kv => Normalize(kv.Key) == Normalize(name)).Value;
        return ConvertValue(raw);
    }

    private static async Task<object?> SiteMemberAsync(SiteModel _, string name, TemplateContext ctx)
    {
        var scope = Scope(ctx);
        var culture = CurrentCulture(ctx);
        switch (Normalize(name))
        {
            case "root": case "home": return await scope.Query.GetRootAsync(scope.Preview, culture);
            case "preview": return scope.Preview;
            case "culture": return await scope.Query.ResolveCultureAsync(culture);
            case "languages":
            {
                // The home page in every language, for a site-wide language switcher.
                var root = await scope.Query.GetRootAsync(scope.Preview, culture);
                if (root is null) return new List<object>();
                return (await scope.Query.GetCultureLinksAsync(root.Id, scope.Preview, culture)).Select(CultureLinkModel).ToList();
            }
            case "dictionary":
            {
                // Every dictionary key with its text in the page's language: {{ site.dictionary['blog.readMore'] }}.
                var values = await scope.Dictionary.GetValuesAsync(culture);
                return new Dictionary<string, object?>(values.ToDictionary(kv => kv.Key, kv => (object?)kv.Value), StringComparer.OrdinalIgnoreCase);
            }
            default: return null;
        }
    }

    private static Dictionary<string, object?> CultureLinkModel(CultureLink link) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["iso_code"] = link.IsoCode,
        ["name"] = link.Name,
        ["url"] = link.Url,
        ["is_current"] = link.IsCurrent,
        ["is_default"] = link.IsDefault
    };

    /// <summary>The <c>request</c> variable: <c>path</c>, <c>url</c>, <c>query</c> (a hash of the query string) and <c>query_string</c>.</summary>
    private static Dictionary<string, object?> RequestModel(LiquidRequest request) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["path"] = request.Path,
        ["url"] = request.Url,
        ["query"] = new Dictionary<string, object?>(request.Query.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)), StringComparer.OrdinalIgnoreCase),
        ["query_string"] = request.Url.Length > request.Path.Length ? request.Url[request.Path.Length..] : string.Empty
    };

    /// <summary>The request the template is rendered for; the page's own URL when the scope carries none.</summary>
    private static LiquidRequest Request(TemplateContext ctx) =>
        Scope(ctx).Request ?? LiquidRequest.ForPath(ctx.GetValue("content").ToObjectValue() is PublishedContent current ? current.Url : "/");

    /// <summary>The language of the page being rendered, so queries made from members and filters stay in it.</summary>
    private static string? CurrentCulture(TemplateContext ctx) =>
        ctx.GetValue("content").ToObjectValue() is PublishedContent current ? current.Culture : null;

    /// <summary>Turns a stored property value into something Liquid can work with: empty → nil, JSON → arrays/objects, "true"/"false" → booleans.</summary>
    private static object? ConvertValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();

        if (trimmed is "true" or "false") return trimmed == "true";

        if ((trimmed.StartsWith('[') && trimmed.EndsWith(']')) || (trimmed.StartsWith('{') && trimmed.EndsWith('}')))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return FromJson(doc.RootElement);
            }
            catch (JsonException)
            {
                // Not JSON after all: fall through and treat it as text.
            }
        }

        return raw;
    }

    private static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.OrdinalIgnoreCase),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    // ---- filters ------------------------------------------------------------------------------------

    private static Guid? AsGuid(FluidValue value)
    {
        var obj = value.ToObjectValue();
        return obj switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var g) => g,
            _ => Guid.TryParse(value.ToStringValue(), out var g) ? g : null
        };
    }

    private static async ValueTask<FluidValue> MediaUrlAsync(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        if (AsGuid(input) is not Guid id) return NilValue.Instance;
        var scope = Scope(ctx);
        var url = scope.Media.GetUrl(await scope.Media.GetAsync(id));
        return url is null ? NilValue.Instance : new StringValue(url);
    }

    private static async ValueTask<FluidValue> MediaAsync(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        if (AsGuid(input) is not Guid id) return NilValue.Instance;
        var scope = Scope(ctx);
        var item = await scope.Media.GetAsync(id);
        if (item is null) return NilValue.Instance;

        var model = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = item.Id,
            ["name"] = item.Name,
            ["url"] = scope.Media.GetUrl(item),
            ["file_name"] = item.FileName,
            ["extension"] = item.Extension,
            ["mime_type"] = item.MimeType,
            ["size_bytes"] = item.SizeBytes,
            ["is_image"] = item.IsImage,
            ["is_folder"] = item.IsFolder
        };
        return FluidValue.Create(model, ctx.Options);
    }

    private static async ValueTask<FluidValue> ContentByIdAsync(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        if (AsGuid(input) is not Guid id) return NilValue.Instance;
        var scope = Scope(ctx);
        var content = await scope.Query.GetByIdAsync(id, scope.Preview, CurrentCulture(ctx));
        return content is null ? NilValue.Instance : FluidValue.Create(content, ctx.Options);
    }

    private static async ValueTask<FluidValue> ContentUrlAsync(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        if (input.ToObjectValue() is PublishedContent direct) return new StringValue(direct.Url);
        if (AsGuid(input) is not Guid id) return NilValue.Instance;
        var scope = Scope(ctx);
        var content = await scope.Query.GetByIdAsync(id, scope.Preview, CurrentCulture(ctx));
        return content is null ? NilValue.Instance : new StringValue(content.Url);
    }

    private static async ValueTask<FluidValue> ContentOfTypeAsync(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var alias = input.ToStringValue();
        if (string.IsNullOrWhiteSpace(alias)) return new ArrayValue([]);
        var scope = Scope(ctx);
        var items = await scope.Query.GetByContentTypeAsync(alias, scope.Preview, CurrentCulture(ctx));
        return FluidValue.Create(items, ctx.Options);
    }

    private static async ValueTask<FluidValue> ChildrenOfAsync(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var id = input.ToObjectValue() is PublishedContent c ? c.Id : AsGuid(input);
        if (id is null) return new ArrayValue([]);
        var scope = Scope(ctx);
        var items = await scope.Query.GetChildrenAsync(id.Value, scope.Preview, CurrentCulture(ctx));
        return FluidValue.Create(items, ctx.Options);
    }

    /// <summary>
    /// <c>{{ 'blog.readMore' | dictionary }}</c>: the dictionary text for a key in the page's language (with the
    /// language's fallback chain). <c>{{ 'blog.readMore' | dictionary: 'Read more' }}</c> prints the argument when
    /// there is no text; without one the key itself is printed so a missing translation is visible.
    /// </summary>
    private static async ValueTask<FluidValue> DictionaryAsync(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var key = input.ToStringValue();
        if (string.IsNullOrWhiteSpace(key)) return NilValue.Instance;
        var scope = Scope(ctx);
        var text = await scope.Dictionary.GetValueAsync(key, CurrentCulture(ctx));
        if (text is not null) return new StringValue(text);
        var fallback = args.Count > 0 ? args.At(0) : null;
        return fallback is not null && !fallback.IsNil() ? fallback : new StringValue(key);
    }

    /// <summary>
    /// <c>{% assign paged = items | paginate: 6 %}</c>: one page of a list, six items to a page. Which page comes from
    /// <c>?page=</c> in the request (<c>| paginate: 6, 2</c> asks for a page explicitly); out-of-range numbers are
    /// clamped. The result is a hash: <c>items</c> (the items on the page), <c>page</c>, <c>page_size</c>,
    /// <c>page_count</c>, <c>total</c>, <c>first</c> and <c>last</c> (positions of the page's first and last item in
    /// the whole list), <c>has_previous</c>, <c>has_next</c>, <c>previous_url</c>, <c>next_url</c> and <c>pages</c>
    /// (one hash per page: <c>number</c>, <c>url</c>, <c>is_current</c>). URLs keep the rest of the query string.
    /// </summary>
    private static ValueTask<FluidValue> Paginate(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var request = Request(ctx);
        var pageSize = args.Count > 0 && args.At(0).Type == FluidValues.Number ? (int)args.At(0).ToNumberValue() : 10;
        var page = args.Count > 1 && !args.At(1).IsNil() && args.At(1).Type == FluidValues.Number
            ? (int?)args.At(1).ToNumberValue()
            : request.Page;

        var all = input.IsNil() ? [] : input.Enumerate(ctx).ToList();
        var paged = Paging.Page(all, pageSize, page);

        var pages = Enumerable.Range(1, paged.PageCount)
            .Select(n => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["number"] = n,
                ["url"] = request.PageUrl(n),
                ["is_current"] = n == paged.Page
            })
            .ToList();

        var model = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["items"] = new ArrayValue(paged.Items),
            ["page"] = paged.Page,
            ["page_size"] = paged.PageSize,
            ["page_count"] = paged.PageCount,
            ["total"] = paged.TotalCount,
            ["first"] = paged.First,
            ["last"] = paged.Last,
            ["has_previous"] = paged.HasPrevious,
            ["has_next"] = paged.HasNext,
            ["previous_page"] = paged.PreviousPage,
            ["next_page"] = paged.NextPage,
            ["previous_url"] = paged.PreviousPage is int p ? request.PageUrl(p) : null,
            ["next_url"] = paged.NextPage is int nx ? request.PageUrl(nx) : null,
            ["pages"] = pages
        };
        return new ValueTask<FluidValue>(FluidValue.Create(model, ctx.Options));
    }

    /// <summary><c>{{ 3 | page_url }}</c>: the URL of page 3 of the list on the current path, keeping the rest of the query string.</summary>
    private static ValueTask<FluidValue> PageUrl(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        if (input.Type != FluidValues.Number) return new ValueTask<FluidValue>(NilValue.Instance);
        return new ValueTask<FluidValue>(new StringValue(Request(ctx).PageUrl(Math.Max(1, (int)input.ToNumberValue()))));
    }

    private static ValueTask<FluidValue> ParseJson(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        if (input.IsNil()) return new ValueTask<FluidValue>(NilValue.Instance);
        if (input.ToObjectValue() is not string s) return new ValueTask<FluidValue>(input);
        var converted = ConvertValue(s);
        return new ValueTask<FluidValue>(converted is null ? NilValue.Instance : FluidValue.Create(converted, ctx.Options));
    }

    // ---- partials -----------------------------------------------------------------------------------

    /// <summary>Exposes stored templates as files so <c>{% render 'alias' %}</c> and <c>{% include 'alias' %}</c> work between them.</summary>
    private sealed class StoredTemplateFileProvider(ITemplateRegistry registry) : IFileProvider
    {
        public IFileInfo GetFileInfo(string subpath)
        {
            var alias = subpath.Trim('/', '\\');
            if (alias.EndsWith(".liquid", StringComparison.OrdinalIgnoreCase)) alias = alias[..^".liquid".Length];
            var template = registry.GetStored(alias);
            return template is null ? new NotFoundFileInfo(subpath) : new StoredTemplateFileInfo(template);
        }

        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

        private sealed class StoredTemplateFileInfo(Template template) : IFileInfo
        {
            private readonly byte[] _bytes = System.Text.Encoding.UTF8.GetBytes(template.Content ?? string.Empty);
            public bool Exists => true;
            public long Length => _bytes.Length;
            public string? PhysicalPath => null;
            public string Name => template.Alias + ".liquid";
            public DateTimeOffset LastModified => template.UpdatedAt;
            public bool IsDirectory => false;
            public Stream CreateReadStream() => new MemoryStream(_bytes, writable: false);
        }
    }
}
