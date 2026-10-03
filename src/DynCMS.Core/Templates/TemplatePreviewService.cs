using DynCMS.Core.Models;
using DynCMS.Core.PropertyEditors;
using DynCMS.Core.Services;

namespace DynCMS.Core.Templates;

/// <summary>Something a template can be previewed against: a real content item, or a made-up sample of a document type.</summary>
public sealed record TemplatePreviewTarget(Guid Id, string Name, string Description, bool IsSample);

/// <summary>The outcome of a preview render.</summary>
public sealed record TemplatePreviewResult(bool Success, string Html, string? Error, TemplatePreviewTarget? Target)
{
    public static TemplatePreviewResult Failed(string error, TemplatePreviewTarget? target = null) => new(false, string.Empty, error, target);
}

/// <summary>
/// Renders the Liquid currently in the back office editor — saved or not — so an editor can see the result
/// while writing. Real content is used when there is any; otherwise a sample item is made up from a document
/// type so a brand new template still shows something.
/// </summary>
public interface ITemplatePreviewService
{
    /// <summary>Content items and sample document types a template with <paramref name="alias"/> can be previewed against.</summary>
    Task<IReadOnlyList<TemplatePreviewTarget>> GetTargetsAsync(string? alias, CancellationToken ct = default);

    /// <summary>Renders <paramref name="source"/> against <paramref name="targetId"/> (the best target when null).</summary>
    Task<TemplatePreviewResult> RenderAsync(string? alias, string source, Guid? targetId, CancellationToken ct = default);
}

public sealed class TemplatePreviewService(
    ILiquidTemplateEngine engine,
    IPublishedContentQuery query,
    IContentService content,
    IContentTypeService contentTypes,
    IMediaService media,
    IDictionaryService dictionary) : ITemplatePreviewService
{
    public async Task<IReadOnlyList<TemplatePreviewTarget>> GetTargetsAsync(string? alias, CancellationToken ct = default)
    {
        var targets = new List<TemplatePreviewTarget>();
        var nodes = await content.GetTreeAsync(ct);

        // Content that already uses this template first, then the rest of the tree.
        foreach (var node in nodes.OrderByDescending(n => Uses(n, alias)).ThenBy(n => n.Path))
        {
            var label = new string('·', Math.Max(0, node.Level - 1)) + (node.Level > 1 ? " " : string.Empty) + node.Name;
            var note = Uses(node, alias) ? $"{node.ContentType.Name} · uses this template" : node.ContentType.Name;
            targets.Add(new TemplatePreviewTarget(node.Id, label, note, false));
        }

        foreach (var type in await contentTypes.GetAllAsync(ct))
            targets.Add(new TemplatePreviewTarget(type.Id, $"Sample {type.Name}", "Made-up content, no publishing needed", true));

        return targets;
    }

    public async Task<TemplatePreviewResult> RenderAsync(string? alias, string source, Guid? targetId, CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(alias) ? "template" : alias;

        var (item, target) = await ResolveAsync(alias, targetId, ct);
        if (item is null)
            return TemplatePreviewResult.Failed("There is no content and no document type to preview this template against. Create a document type first.");

        try
        {
            var html = await engine.RenderSourceAsync(name, source ?? string.Empty, item, new LiquidRenderScope(query, media, dictionary, true), ct);
            return new TemplatePreviewResult(true, html, null, target);
        }
        catch (TemplateRenderException ex)
        {
            return TemplatePreviewResult.Failed(ex.Message, target);
        }
        catch (Exception ex)
        {
            return TemplatePreviewResult.Failed(ex.Message, target);
        }
    }

    private async Task<(PublishedContent? Item, TemplatePreviewTarget? Target)> ResolveAsync(string? alias, Guid? targetId, CancellationToken ct)
    {
        if (targetId is Guid id)
        {
            // Draft values, so unpublished content previews too.
            if (await query.GetByIdAsync(id, true, null, ct) is { } chosen)
                return (chosen, new TemplatePreviewTarget(chosen.Id, chosen.Name, chosen.ContentTypeName, false));

            if (await contentTypes.GetAsync(id, ct) is { } type)
                return (await SampleAsync(type, ct), new TemplatePreviewTarget(type.Id, $"Sample {type.Name}", "Made-up content", true));
        }

        // Nothing chosen: prefer content that already uses this template, then the site root, then a sample.
        var nodes = await content.GetTreeAsync(ct);
        var match = nodes.FirstOrDefault(n => Uses(n, alias));
        if (match is not null && await query.GetByIdAsync(match.Id, true, null, ct) is { } used)
            return (used, new TemplatePreviewTarget(used.Id, used.Name, used.ContentTypeName, false));

        if (await query.GetRootAsync(true, null, ct) is { } root)
            return (root, new TemplatePreviewTarget(root.Id, root.Name, root.ContentTypeName, false));

        var first = (await contentTypes.GetAllAsync(ct)).FirstOrDefault();
        return first is null
            ? (null, null)
            : (await SampleAsync(first, ct), new TemplatePreviewTarget(first.Id, $"Sample {first.Name}", "Made-up content", true));
    }

    private static bool Uses(ContentNode node, string? alias) =>
        !string.IsNullOrWhiteSpace(alias) &&
        (string.Equals(node.TemplateAlias, alias, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(node.PublishedTemplateAlias, alias, StringComparison.OrdinalIgnoreCase));

    // ---- sample content -----------------------------------------------------------------------------

    /// <summary>A content item that does not exist, filled with plausible values for every property of a document type.</summary>
    private async Task<PublishedContent> SampleAsync(ContentType type, CancellationToken ct)
    {
        var image = (await media.GetRecentAsync(24, ct)).FirstOrDefault(m => m.IsImage)?.Id;
        var link = (await content.GetRootsAsync(ct)).FirstOrDefault()?.Id;

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in type.Properties.OrderBy(p => p.SortOrder))
            values[property.Alias] = SampleValue(property, image, link);

        return new PublishedContent
        {
            Id = type.Id,
            ParentId = null,
            Name = $"Sample {type.Name}",
            UrlSegment = "sample",
            Url = "/sample",
            ContentTypeAlias = type.Alias,
            ContentTypeName = type.Name,
            TemplateAlias = type.DefaultTemplateAlias,
            Level = 1,
            SortOrder = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            PublishedAt = DateTime.UtcNow,
            IsPreview = true,
            Culture = await query.ResolveCultureAsync(null, ct),
            Values = values
        };
    }

    private static string? SampleValue(PropertyType property, Guid? image, Guid? link) => property.EditorAlias switch
    {
        PropertyEditorAliases.RichText =>
            "<p>This is sample body text so you can see the template working. It is not stored anywhere.</p>" +
            "<ul><li>First point</li><li>Second point</li></ul>",
        PropertyEditorAliases.TextArea => $"Sample {property.Name.ToLowerInvariant()} — a couple of lines of plain text to fill the space.",
        PropertyEditorAliases.Numeric => "3",
        PropertyEditorAliases.Toggle => "true",
        PropertyEditorAliases.DatePicker => DateTime.UtcNow.ToString("O"),
        PropertyEditorAliases.Dropdown => property.GetConfig("items")?.Split('\n').FirstOrDefault()?.Trim() ?? "Option",
        PropertyEditorAliases.MediaPicker => image?.ToString(),
        PropertyEditorAliases.ContentPicker => link?.ToString(),
        PropertyEditorAliases.Tags => "[\"sample\",\"preview\"]",
        PropertyEditorAliases.ColorPicker => "#4f5bd5",
        _ => $"Sample {property.Name.ToLowerInvariant()}"
    };
}
