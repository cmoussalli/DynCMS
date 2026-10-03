using DynCMS.Core.Helpers;
using DynCMS.Core.Models;
using DynCMS.Core.PropertyEditors;
using DynCMS.Core.Security;

namespace DynCMS.Core.Api;

public sealed partial class CmsManagement
{
    #region Property editors

    public IReadOnlyList<PropertyEditorDto> GetPropertyEditors()
    {
        access.Require(CmsPermissions.Entities.DocumentTypes, CmsPermissions.Actions.Read);
        return editors.All.Select(e => new PropertyEditorDto(
            e.Alias, e.Name, e.Description, e.Icon,
            e.ConfigFields.Select(f => new PropertyEditorConfigFieldDto(f.Key, f.Label, f.Description, f.Type.ToString())).ToList(),
            StoredFormat(e.Alias))).ToList();
    }

    /// <summary>How a value of this editor is stored, so an agent knows what to write.</summary>
    private static string StoredFormat(string alias) => alias switch
    {
        PropertyEditorAliases.TextBox or PropertyEditorAliases.TextArea => "Plain text.",
        PropertyEditorAliases.RichText => "HTML.",
        PropertyEditorAliases.Numeric => "Invariant-culture number, e.g. \"3\" or \"3.5\".",
        PropertyEditorAliases.Toggle => "\"true\" or \"false\".",
        PropertyEditorAliases.DatePicker => "\"yyyy-MM-dd\", or \"yyyy-MM-ddTHH:mm\" when includeTime is set.",
        PropertyEditorAliases.Dropdown => "The selected option's text (one of the configured items).",
        PropertyEditorAliases.MediaPicker => "The media item's GUID.",
        PropertyEditorAliases.ContentPicker => "The content item's GUID.",
        PropertyEditorAliases.Tags => "JSON array of strings, e.g. [\"news\",\"release\"].",
        PropertyEditorAliases.ColorPicker => "Hex colour, e.g. \"#ff6600\".",
        _ => "String; format defined by the editor."
    };

    #endregion

    #region Document types

    public async Task<IReadOnlyList<DocumentTypeDto>> GetDocumentTypesAsync(CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.DocumentTypes, CmsPermissions.Actions.Read);
        var types = await contentTypes.GetAllAsync(ct);
        return types.Select(t => MapType(t, null)).ToList();
    }

    public async Task<DocumentTypeDto> GetDocumentTypeAsync(string idOrAlias, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.DocumentTypes, CmsPermissions.Actions.Read);
        var type = await FindTypeAsync(idOrAlias, ct);
        return MapType(type, await contentTypes.CountContentAsync(type.Id, ct));
    }

    public async Task<DocumentTypeDto> CreateDocumentTypeAsync(SaveDocumentTypeRequest request, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.DocumentTypes, CmsPermissions.Actions.Create);
        if (string.IsNullOrWhiteSpace(request.Name)) throw CmsApiException.BadRequest("name is required.");

        var type = new ContentType();
        Apply(type, request);
        var saved = await Guard(() => contentTypes.SaveAsync(type, ct));
        return MapType(saved, 0);
    }

    public async Task<DocumentTypeDto> UpdateDocumentTypeAsync(string idOrAlias, SaveDocumentTypeRequest request, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.DocumentTypes, CmsPermissions.Actions.Update);
        var type = await FindTypeAsync(idOrAlias, ct);
        Apply(type, request);
        var saved = await Guard(() => contentTypes.SaveAsync(type, ct));
        return MapType(saved, await contentTypes.CountContentAsync(saved.Id, ct));
    }

    public async Task DeleteDocumentTypeAsync(string idOrAlias, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.DocumentTypes, CmsPermissions.Actions.Delete);
        var type = await FindTypeAsync(idOrAlias, ct);
        await Guard(() => contentTypes.DeleteAsync(type.Id, ct));
    }

    private async Task<ContentType> FindTypeAsync(string idOrAlias, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idOrAlias)) throw CmsApiException.BadRequest("A document type id or alias is required.");
        var type = Guid.TryParse(idOrAlias, out var id)
            ? await contentTypes.GetAsync(id, ct)
            : await contentTypes.GetByAliasAsync(idOrAlias.Trim(), ct);
        return type ?? throw CmsApiException.NotFound($"Document type '{idOrAlias}'");
    }

    private void Apply(ContentType type, SaveDocumentTypeRequest r)
    {
        if (r.Name is not null) type.Name = r.Name.Trim();
        if (r.Alias is not null) type.Alias = Slug.ToAlias(r.Alias);
        if (r.Description is not null) type.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        if (r.Icon is not null) type.Icon = string.IsNullOrWhiteSpace(r.Icon) ? "document" : r.Icon.Trim();
        if (r.AllowedAsRoot is not null) type.AllowedAsRoot = r.AllowedAsRoot.Value;
        if (r.VariesByCulture is not null) type.VariesByCulture = r.VariesByCulture.Value;
        if (r.SortOrder is not null) type.SortOrder = r.SortOrder.Value;
        if (r.AllowedChildTypes is not null) type.AllowedChildTypeAliases = Clean(r.AllowedChildTypes);

        if (r.AllowedTemplates is not null)
        {
            var aliases = Clean(r.AllowedTemplates);
            var unknown = aliases.Where(a => templateRegistry.Get(a) is null).ToList();
            if (unknown.Count > 0)
                throw CmsApiException.BadRequest($"Unknown template alias(es): {string.Join(", ", unknown)}.", new { templates = templateRegistry.Pages.Select(t => t.Alias) });
            var partials = aliases.Where(a => templateRegistry.Get(a)?.IsPartial == true).ToList();
            if (partials.Count > 0)
                throw CmsApiException.BadRequest($"Partials cannot be page templates: {string.Join(", ", partials)}.");
            type.AllowedTemplateAliases = aliases;
        }

        if (r.DefaultTemplate is not null)
        {
            var alias = r.DefaultTemplate.Trim();
            if (alias.Length == 0) type.DefaultTemplateAlias = null;
            else
            {
                if (templateRegistry.Get(alias) is null) throw CmsApiException.BadRequest($"Unknown template alias '{alias}'.");
                if (!type.AllowedTemplateAliases.Contains(alias, StringComparer.OrdinalIgnoreCase)) type.AllowedTemplateAliases.Add(alias);
                type.DefaultTemplateAlias = alias;
            }
        }
        else if (type.DefaultTemplateAlias is not null && !type.AllowedTemplateAliases.Contains(type.DefaultTemplateAlias, StringComparer.OrdinalIgnoreCase))
        {
            type.DefaultTemplateAlias = type.AllowedTemplateAliases.FirstOrDefault();
        }

        if (r.Properties is not null)
        {
            var existing = type.Properties.ToList();
            var result = new List<PropertyType>();
            foreach (var p in r.Properties)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) throw CmsApiException.BadRequest("Every property needs a name.");
                if (string.IsNullOrWhiteSpace(p.Editor)) throw CmsApiException.BadRequest($"Property '{p.Name}' needs an editor alias.");
                var editor = editors.Get(p.Editor.Trim())
                    ?? throw CmsApiException.BadRequest($"Unknown property editor '{p.Editor}' on '{p.Name}'.", new { editors = editors.All.Select(e => e.Alias) });

                var alias = string.IsNullOrWhiteSpace(p.Alias) ? Slug.ToAlias(p.Name) : Slug.ToAlias(p.Alias);
                var target = (p.Id is Guid pid ? existing.FirstOrDefault(x => x.Id == pid) : null)
                             ?? existing.FirstOrDefault(x => string.Equals(x.Alias, alias, StringComparison.OrdinalIgnoreCase))
                             ?? new PropertyType { Id = p.Id ?? Guid.NewGuid(), ContentTypeId = type.Id };
                existing.Remove(target);

                target.Alias = alias;
                target.Name = p.Name.Trim();
                target.Description = string.IsNullOrWhiteSpace(p.Description) ? null : p.Description.Trim();
                target.EditorAlias = editor.Alias;
                target.GroupName = string.IsNullOrWhiteSpace(p.Group) ? "Content" : p.Group.Trim();
                target.Mandatory = p.Mandatory ?? false;
                if (p.VariesByCulture is not null) target.VariesByCulture = p.VariesByCulture.Value;
                if (p.Config is not null)
                {
                    var knownKeys = editor.ConfigFields.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var unknownKeys = p.Config.Keys.Where(k => !knownKeys.Contains(k)).ToList();
                    if (unknownKeys.Count > 0)
                        throw CmsApiException.BadRequest($"Editor '{editor.Alias}' has no setting(s) {string.Join(", ", unknownKeys)}.", new { settings = editor.ConfigFields.Select(f => f.Key) });
                    target.Config = new Dictionary<string, string>(p.Config, StringComparer.OrdinalIgnoreCase);
                }
                result.Add(target);
            }
            type.Properties = result;
        }
    }

    private static List<string> Clean(IEnumerable<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static DocumentTypeDto MapType(ContentType t, int? contentCount) => new(
        t.Id, t.Alias, t.Name, t.Description, t.Icon, t.AllowedAsRoot,
        t.AllowedChildTypeAliases, t.AllowedTemplateAliases, t.DefaultTemplateAlias, t.VariesByCulture, t.SortOrder,
        t.Properties.OrderBy(p => p.SortOrder).Select(p => new PropertyTypeDto(p.Id, p.Alias, p.Name, p.Description, p.EditorAlias, p.GroupName, p.Mandatory, p.VariesByCulture, p.SortOrder, p.Config)).ToList(),
        contentCount, t.CreatedAt, t.UpdatedAt);

    #endregion
}
