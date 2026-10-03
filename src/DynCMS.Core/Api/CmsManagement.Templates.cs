using DynCMS.Core.Helpers;
using DynCMS.Core.Models;
using DynCMS.Core.Security;
using DynCMS.Core.Services;
using DynCMS.Core.Templates;

namespace DynCMS.Core.Api;

public sealed partial class CmsManagement
{
    public IReadOnlyList<TemplateDto> GetTemplates()
    {
        access.Require(CmsPermissions.Entities.Templates, CmsPermissions.Actions.Read);
        return templateRegistry.All.Select(d => MapTemplate(d, templateRegistry.GetStored(d.Alias), includeSource: false, usage: null)).ToList();
    }

    public async Task<TemplateDto> GetTemplateAsync(string idOrAlias, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Templates, CmsPermissions.Actions.Read);
        var (definition, stored) = await FindTemplateAsync(idOrAlias, ct);
        var usage = await templates.GetUsageAsync(definition.Alias, stored?.Id, ct);
        return MapTemplate(definition, stored, includeSource: true, usage);
    }

    public async Task<TemplateDto> CreateTemplateAsync(SaveTemplateRequest request, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Templates, CmsPermissions.Actions.Create);
        if (string.IsNullOrWhiteSpace(request.Name)) throw CmsApiException.BadRequest("name is required.");
        if (request.Content is null) throw CmsApiException.BadRequest("content (Liquid source) is required; use an empty string for a blank template.");

        var template = new Template();
        Apply(template, request);
        var saved = await Guard(() => templates.SaveAsync(template, ct));
        return MapTemplate(templateRegistry.Get(saved.Alias)!, saved, includeSource: true, TemplateUsage.Empty);
    }

    public async Task<TemplateDto> UpdateTemplateAsync(string idOrAlias, SaveTemplateRequest request, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Templates, CmsPermissions.Actions.Update);
        var (definition, stored) = await FindTemplateAsync(idOrAlias, ct);
        if (stored is null)
            throw CmsApiException.Conflict($"'{definition.Alias}' is a component template compiled into the site. Create an editable version first (POST .../templates/{definition.Alias}/override).");

        Apply(stored, request);
        var saved = await Guard(() => templates.SaveAsync(stored, ct));
        return MapTemplate(templateRegistry.Get(saved.Alias)!, saved, includeSource: true, await templates.GetUsageAsync(saved.Alias, saved.Id, ct));
    }

    public async Task DeleteTemplateAsync(string idOrAlias, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Templates, CmsPermissions.Actions.Delete);
        var (definition, stored) = await FindTemplateAsync(idOrAlias, ct);
        if (stored is null) throw CmsApiException.Conflict($"'{definition.Alias}' is a component template and cannot be deleted through the API.");
        await Guard(() => templates.DeleteAsync(stored.Id, ct));
    }

    public async Task<TemplateDto> DuplicateTemplateAsync(string idOrAlias, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Templates, CmsPermissions.Actions.Create);
        var (definition, stored) = await FindTemplateAsync(idOrAlias, ct);
        if (stored is null) throw CmsApiException.Conflict($"'{definition.Alias}' is a component template; create an editable version instead.");
        var copy = await Guard(() => templates.DuplicateAsync(stored.Id, ct));
        return MapTemplate(templateRegistry.Get(copy.Alias)!, copy, includeSource: true, TemplateUsage.Empty);
    }

    /// <summary>Writes a stored Liquid template that takes over from a component template with the same alias.</summary>
    public async Task<TemplateDto> CreateOverrideAsync(string alias, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Templates, CmsPermissions.Actions.Create);
        if (templateRegistry.GetComponent(alias) is null) throw CmsApiException.NotFound($"Component template '{alias}'");
        if (templateRegistry.GetStored(alias) is not null) throw CmsApiException.Conflict($"'{alias}' already has an editable version.");
        var created = await Guard(() => templates.CreateOverrideForComponentAsync(alias, ct));
        return MapTemplate(templateRegistry.Get(created.Alias)!, created, includeSource: true, await templates.GetUsageAsync(created.Alias, created.Id, ct));
    }

    public TemplateValidationDto ValidateTemplate(ValidateTemplateRequest request)
    {
        access.Require(CmsPermissions.Entities.Templates, CmsPermissions.Actions.Read);
        var result = liquid.Validate(request.Content ?? string.Empty);
        return new TemplateValidationDto(result.IsValid, result.Error);
    }

    public async Task<TemplatePreviewDto> PreviewTemplateAsync(PreviewTemplateRequest request, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Templates, CmsPermissions.Actions.Read);
        if (request.Content is null) throw CmsApiException.BadRequest("content is required.");
        var result = await previews.RenderAsync(request.Alias, request.Content, request.ContentId, ct);
        return new TemplatePreviewDto(result.Success, result.Html, result.Error, result.Target?.Name);
    }

    /// <summary>What a template author (human or agent) can use inside Liquid.</summary>
    public LiquidReferenceDto GetLiquidReference()
    {
        access.Require(CmsPermissions.Entities.Templates, CmsPermissions.Actions.Read);
        return new LiquidReferenceDto(
            TemplateIntellisense.Variables.Select(v => $"{v.Label} — {v.Detail}").ToList(),
            TemplateIntellisense.Filters.Select(f => $"{f.Insert} — {f.Detail}").ToList(),
            [
                "Any other member of content is a property lookup by alias: content.bodyText, content.body_text and content.bodytext are the same property.",
                "Output is HTML-encoded; rich text needs | raw.",
                "Empty values are nil, \"true\"/\"false\" become booleans, JSON arrays/objects become Liquid arrays/hashes.",
                "Partials: {% render 'alias' %} (content = current page) or {% render 'alias', content: item %}.",
                "Navigation members children, parent, ancestors and siblings are live queries; site.root is the home page; preview is true while a draft is shown.",
                "Languages: content.culture is the ISO code of the page being rendered; content.cultures and site.languages list the page/home in every language as {iso_code, name, url, is_current, is_default}. Queries made from a template stay in the page's language; the date filter formats in it."
            ]);
    }

    private async Task<(TemplateDefinition Definition, Template? Stored)> FindTemplateAsync(string idOrAlias, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idOrAlias)) throw CmsApiException.BadRequest("A template id or alias is required.");
        if (Guid.TryParse(idOrAlias, out var id))
        {
            var stored = await templates.GetAsync(id, ct) ?? throw CmsApiException.NotFound($"Template '{idOrAlias}'");
            return (templateRegistry.Get(stored.Alias) ?? new TemplateDefinition(stored.Alias, stored.Name, null, stored.Id, stored.Role, stored.Description), stored);
        }
        var alias = idOrAlias.Trim();
        var definition = templateRegistry.Get(alias) ?? throw CmsApiException.NotFound($"Template '{alias}'");
        return (definition, definition.IsStored ? await templates.GetAsync(definition.StoredId!.Value, ct) : null);
    }

    private static void Apply(Template template, SaveTemplateRequest r)
    {
        if (r.Name is not null) template.Name = r.Name.Trim();
        if (r.Alias is not null) template.Alias = Slug.ToAlias(r.Alias);
        if (r.Description is not null) template.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        if (r.Role is not null) template.Role = r.Role.Value;
        if (r.Content is not null) template.Content = r.Content;
    }

    private static TemplateDto MapTemplate(TemplateDefinition d, Template? stored, bool includeSource, TemplateUsage? usage) => new(
        stored?.Id, d.Alias, d.Name, d.Description ?? stored?.Description, d.Role.ToString(), d.Kind.ToString(),
        d.ComponentType?.FullName,
        includeSource ? stored?.Content : null,
        stored?.CreatedAt, stored?.UpdatedAt,
        usage is null ? null : new TemplateUsageDto(usage.ContentCount, usage.DocumentTypes.Select(t => t.Alias).ToList(), usage.UsedBy.Select(t => t.Alias).ToList(), usage.Uses));
}
