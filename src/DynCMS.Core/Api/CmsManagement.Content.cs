using System.Globalization;
using System.Text.Json;
using DynCMS.Plugins.Models;
using DynCMS.Core.Security;
using DynCMS.Core.Services;

namespace DynCMS.Core.Api;

public sealed partial class CmsManagement
{
    #region Read

    public async Task<IReadOnlyList<ContentTreeNodeDto>> GetContentTreeAsync(Guid? rootId, int? maxDepth, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Read);
        var all = await content.GetTreeAsync(ct);
        var byParent = all.GroupBy(n => n.ParentId).ToDictionary(g => g.Key ?? Guid.Empty, g => g.OrderBy(n => n.SortOrder).ToList());
        var depth = maxDepth is > 0 ? maxDepth.Value : int.MaxValue;

        if (rootId is Guid root)
        {
            var node = all.FirstOrDefault(n => n.Id == root) ?? throw CmsApiException.NotFound($"Content '{root}'");
            return [Build(node, 1)];
        }
        return byParent.TryGetValue(Guid.Empty, out var roots) ? roots.Select(n => Build(n, 1)).ToList() : [];

        ContentTreeNodeDto Build(ContentNode n, int level)
        {
            var children = level < depth && byParent.TryGetValue(n.Id, out var kids) ? kids.Select(k => Build(k, level + 1)).ToList() : [];
            return new ContentTreeNodeDto(n.Id, n.ParentId, n.ContentType.Alias, n.Name, n.UrlSegment, n.Level, n.SortOrder, n.IsPublished, n.HasPendingChanges,
                n.VariesByCulture ? n.PublishedCultures.ToList() : null, children);
        }
    }

    public async Task<IReadOnlyList<ContentDto>> GetChildrenAsync(Guid? parentId, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Read);
        var nodes = parentId is Guid p ? await content.GetChildrenAsync(p, ct) : await content.GetRootsAsync(ct);
        return nodes.Select(n => MapContent(n, null, includePublished: false)).ToList();
    }

    public async Task<ContentDto> GetContentAsync(Guid id, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Read);
        var node = await content.GetAsync(id, ct) ?? throw CmsApiException.NotFound($"Content '{id}'");
        return await MapContentWithUrlsAsync(node, ct);
    }

    public async Task<IReadOnlyList<ContentDto>> SearchContentAsync(string term, int take, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Read);
        if (string.IsNullOrWhiteSpace(term)) throw CmsApiException.BadRequest("A search term is required.");
        var nodes = await content.SearchAsync(term.Trim(), Math.Clamp(take, 1, 200), ct);
        return nodes.Select(n => MapContent(n, null, includePublished: false)).ToList();
    }

    public async Task<PublishedContentDto?> GetPublishedByRouteAsync(string path, bool preview, string? culture, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Read);
        var item = await published.GetByRouteAsync(string.IsNullOrWhiteSpace(path) ? "/" : path, preview, await CultureAsync(culture, ct), ct);
        return item is null ? null : MapPublished(item);
    }

    public async Task<PublishedContentDto> GetPublishedAsync(Guid id, bool preview, string? culture, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Read);
        var item = await published.GetByIdAsync(id, preview, await CultureAsync(culture, ct), ct)
            ?? throw CmsApiException.NotFound(preview ? $"Content '{id}'" : $"Published content '{id}'" + (culture is null ? string.Empty : $" in '{culture}'"));
        return MapPublished(item);
    }

    public async Task<IReadOnlyList<PublishedContentDto>> GetPublishedChildrenAsync(Guid id, bool preview, string? culture, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Read);
        return (await published.GetChildrenAsync(id, preview, await CultureAsync(culture, ct), ct)).Select(MapPublished).ToList();
    }

    public async Task<IReadOnlyList<PublishedContentDto>> GetPublishedByTypeAsync(string alias, bool preview, string? culture, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Read);
        return (await published.GetByContentTypeAsync(alias, preview, await CultureAsync(culture, ct), ct)).Select(MapPublished).ToList();
    }

    #endregion

    #region Write

    public async Task<PublishResultDto> CreateContentAsync(CreateContentRequest request, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Create);
        if (string.IsNullOrWhiteSpace(request.Name)) throw CmsApiException.BadRequest("name is required.");
        if (request.Publish == true) access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Publish);

        var type = await FindTypeAsync(request.ContentType, ct);
        if (request.Template is not null) EnsureTemplateAllowed(type, request.Template);
        var culture = type.VariesByCulture ? await CultureAsync(request.Culture, ct) ?? (await languages.GetDefaultAsync(ct)).IsoCode : null;
        var values = request.Values is null ? null : ConvertValues(type, request.Values);

        var node = await Guard(() => content.CreateAsync(type.Id, request.ParentId, request.Name, culture, ct));

        var changed = false;
        if (!string.IsNullOrWhiteSpace(request.UrlSegment)) { node.SetUrlSegment(culture, request.UrlSegment); changed = true; }
        if (request.Template is not null) { node.TemplateAlias = NullIfEmpty(request.Template); changed = true; }
        if (values is not null)
        {
            foreach (var (k, v) in values) node.SetValue(k, v, culture);
            changed = true;
        }
        if (changed) node = await Guard(() => content.SaveAsync(node, ct));

        return await FinishAsync(node, request.Publish == true, culture, ct);
    }

    public async Task<PublishResultDto> UpdateContentAsync(Guid id, UpdateContentRequest request, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Update);
        if (request.Publish == true) access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Publish);

        var node = await content.GetAsync(id, ct) ?? throw CmsApiException.NotFound($"Content '{id}'");
        var culture = node.VariesByCulture ? await CultureAsync(request.Culture, ct) ?? (await languages.GetDefaultAsync(ct)).IsoCode : null;

        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name)) throw CmsApiException.BadRequest("name cannot be empty.");
            node.SetName(culture, request.Name);
        }
        if (request.UrlSegment is not null) node.SetUrlSegment(culture, request.UrlSegment);
        if (request.Template is not null)
        {
            EnsureTemplateAllowed(node.ContentType, request.Template);
            node.TemplateAlias = NullIfEmpty(request.Template);
        }
        if (request.Values is not null)
        {
            var values = ConvertValues(node.ContentType, request.Values);
            if (request.ReplaceValues == true)
            {
                foreach (var key in node.DraftValues.Keys.ToList()) node.DraftValues[key] = null;
                if (culture is not null && node.GetCulture(culture) is { } state)
                    foreach (var key in state.DraftValues.Keys.ToList()) state.DraftValues[key] = null;
            }
            foreach (var (k, v) in values) node.SetValue(k, v, culture);
        }

        node = await Guard(() => content.SaveAsync(node, ct));
        return await FinishAsync(node, request.Publish == true, culture, ct);
    }

    public IReadOnlyList<ContentValidationErrorDto> ValidateContent(ContentNode node) =>
        content.Validate(node).Select(MapError).ToList();

    public async Task<PublishResultDto> PublishContentAsync(Guid id, PublishContentRequest? request, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Publish);
        var cultures = await CulturesAsync(request?.Cultures, ct);
        var result = await Guard(() => content.PublishAsync(id, cultures, ct));
        return await MapPublishResultAsync(result, ct);
    }

    public async Task<ContentDto> UnpublishContentAsync(Guid id, UnpublishContentRequest? request, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Publish);
        var culture = await CultureAsync(request?.Culture, ct);
        var node = await Guard(() => content.UnpublishAsync(id, culture, ct)) ?? throw CmsApiException.NotFound($"Content '{id}'");
        return await MapContentWithUrlsAsync(node, ct);
    }

    public async Task DeleteContentAsync(Guid id, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Delete);
        _ = await content.GetAsync(id, ct) ?? throw CmsApiException.NotFound($"Content '{id}'");
        await Guard(() => content.DeleteAsync(id, ct));
    }

    public async Task<ContentDto> MoveContentAsync(Guid id, MoveContentRequest request, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Update);
        if (request.Direction is not (-1 or 1)) throw CmsApiException.BadRequest("direction must be -1 (up) or 1 (down).");
        _ = await content.GetAsync(id, ct) ?? throw CmsApiException.NotFound($"Content '{id}'");
        await Guard(() => content.MoveAsync(id, request.Direction, ct));
        return MapContent((await content.GetAsync(id, ct))!, null, includePublished: false);
    }

    private async Task<PublishResultDto> FinishAsync(ContentNode node, bool publish, string? culture, CancellationToken ct)
    {
        if (!publish) return new PublishResultDto(true, [], await MapContentWithUrlsAsync(node, ct));
        var result = await Guard(() => content.PublishAsync(node.Id, culture is null ? null : [culture], ct));
        if (!result.Success)
        {
            // The draft was saved; report the validation errors alongside it.
            var errors = result.Errors.Select(MapError).ToList();
            return new PublishResultDto(false, errors, await MapContentWithUrlsAsync((await content.GetAsync(node.Id, ct))!, ct));
        }
        return await MapPublishResultAsync(result, ct);
    }

    private async Task<PublishResultDto> MapPublishResultAsync(PublishResult result, CancellationToken ct)
    {
        if (!result.Success || result.Node is null)
            return new PublishResultDto(false, result.Errors.Select(MapError).ToList(), null);
        return new PublishResultDto(true, [], await MapContentWithUrlsAsync(result.Node, ct));
    }

    private void EnsureTemplateAllowed(ContentType type, string template)
    {
        var alias = template.Trim();
        if (alias.Length == 0) return;
        if (templateRegistry.Get(alias) is null) throw CmsApiException.BadRequest($"Unknown template alias '{alias}'.", new { templates = templateRegistry.Pages.Select(t => t.Alias) });
        if (type.AllowedTemplateAliases.Count > 0 && !type.AllowedTemplateAliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
            throw CmsApiException.BadRequest($"Template '{alias}' is not allowed on document type '{type.Alias}'.", new { allowed = type.AllowedTemplateAliases });
    }

    /// <summary>The ISO code of a configured language, or null when none was given. Unknown codes are a bad request.</summary>
    private async Task<string?> CultureAsync(string? culture, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(culture)) return null;
        var all = await languages.GetAllAsync(ct);
        var language = all.Match(culture)
            ?? throw CmsApiException.BadRequest($"'{culture}' is not a configured language.", new { languages = all.Select(l => l.IsoCode) });
        return language.IsoCode;
    }

    private async Task<IReadOnlyList<string>?> CulturesAsync(IReadOnlyList<string>? cultures, CancellationToken ct)
    {
        if (cultures is null || cultures.Count == 0) return null;
        var result = new List<string>();
        foreach (var c in cultures)
        {
            if (await CultureAsync(c, ct) is { } iso) result.Add(iso);
        }
        return result;
    }

    /// <summary>Checks aliases against the document type and converts JSON values to the string form the editors store.</summary>
    private static Dictionary<string, string?> ConvertValues(ContentType type, IReadOnlyDictionary<string, JsonElement> values)
    {
        var known = type.Properties.ToDictionary(p => p.Alias, StringComparer.OrdinalIgnoreCase);
        var unknown = values.Keys.Where(k => !known.ContainsKey(k)).ToList();
        if (unknown.Count > 0)
            throw CmsApiException.BadRequest($"Document type '{type.Alias}' has no property '{string.Join("', '", unknown)}'.", new { properties = known.Keys });

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, element) in values) result[known[alias].Alias] = ToStoredValue(element);
        return result;
    }

    internal static string? ToStoredValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture) : element.GetDouble().ToString(CultureInfo.InvariantCulture),
        _ => element.GetRawText()
    };

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    #endregion

    #region Mapping

    /// <summary>The full item with its public URL(s): one per published language for content that varies.</summary>
    private async Task<ContentDto> MapContentWithUrlsAsync(ContentNode node, CancellationToken ct)
    {
        var url = node.IsPublished ? await published.GetUrlAsync(node.Id, null, ct) : null;
        Dictionary<string, string>? urls = null;
        if (node.VariesByCulture)
        {
            urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in await published.GetCultureLinksAsync(node.Id, preview: false, null, ct)) urls[link.IsoCode] = link.Url;
        }
        return MapContent(node, url, includePublished: true, urls);
    }

    private static ContentDto MapContent(ContentNode n, string? url, bool includePublished, IReadOnlyDictionary<string, string>? urls = null) => new(
        n.Id, n.ParentId, n.ContentType?.Alias ?? string.Empty, n.ContentType?.Name ?? string.Empty,
        n.Name, n.UrlSegment, n.TemplateAlias, n.Level, n.SortOrder, n.IsPublished, n.HasPendingChanges, url,
        n.DraftValues,
        includePublished && n.IsPublished ? new PublishedSnapshotDto(n.PublishedName, n.PublishedUrlSegment, n.PublishedTemplateAlias, n.PublishedValues) : null,
        n.VariesByCulture,
        n.VariesByCulture
            ? n.Cultures.Where(kv => kv.Value.Exists).ToDictionary(kv => kv.Key, kv => new ContentCultureDto(
                kv.Value.Name, kv.Value.UrlSegment, kv.Value.IsPublished, kv.Value.HasPendingChanges,
                urls is not null && urls.TryGetValue(kv.Key, out var u) ? u : null,
                kv.Value.DraftValues,
                includePublished && kv.Value.IsPublished ? new PublishedSnapshotDto(kv.Value.PublishedName, kv.Value.PublishedUrlSegment, null, kv.Value.PublishedValues) : null,
                kv.Value.UpdatedAt, kv.Value.PublishedAt), StringComparer.OrdinalIgnoreCase)
            : null,
        n.CreatedAt, n.UpdatedAt, n.PublishedAt);

    private static PublishedContentDto MapPublished(PublishedContent c) => new(
        c.Id, c.ParentId, c.Name, c.UrlSegment, c.Url, c.ContentTypeAlias, c.ContentTypeName, c.TemplateAlias,
        c.Level, c.SortOrder, c.Culture, c.VariesByCulture ? c.Cultures : null, c.Values, c.PublishedAt, c.UpdatedAt);

    private static ContentValidationErrorDto MapError(ContentValidationError e) => new(e.PropertyAlias, e.Message, e.Culture);

    #endregion
}
