using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DynCMS.Plugins.Models;

namespace DynCMS.Core.Api;

// The wire model of the management API and the MCP tools. Everything is plain records with camelCase JSON so an
// agent can read the OpenAPI document (or the MCP tool schemas) and know exactly what to send.

#region System

public sealed record ApiInfoDto(
    string Name,
    string Version,
    bool Ready,
    ApiEndpointsDto Endpoints,
    CallerDto? Caller,
    ApiCountsDto? Counts,
    IReadOnlyList<string> Hints);

public sealed record ApiEndpointsDto(string Api, string OpenApi, string Mcp);

public sealed record CallerDto(
    string UserId,
    string UserName,
    IReadOnlyList<string> Roles,
    string AuthMethod,
    IReadOnlyList<string> Scopes,
    Guid? ApiKeyId,
    IReadOnlyList<string> Permissions);

public sealed record ApiCountsDto(int Content, int PublishedContent, int Media, int DocumentTypes, int Templates, int Languages, int DictionaryItems);

public sealed record DatabaseInfoDto(
    bool Ready,
    string Provider,
    string Description,
    string? SqliteFile,
    long? SqliteFileSize,
    string? Server,
    string? Database,
    IReadOnlyList<TableInfoDto> Tables);

public sealed record SystemVersionDto(
    [property: Description("Version of the running DynCMS application layer.")] string ApplicationVersion,
    [property: Description("Database schema version this build expects.")] int ExpectedSchemaVersion,
    [property: Description("Database schema version recorded in the database (null when none is recorded yet).")] int? DatabaseSchemaVersion,
    [property: Description("DynCMS version that last started against the database.")] string? DatabaseApplicationVersion,
    [property: Description("current, upgraded or newerThanApplication (update DynCMS).")] string DatabaseState,
    string Runtime,
    string OperatingSystem);

public sealed record TableInfoDto(string Name, long? Rows);

public sealed record BackupDto(string FileName, long? Size, DateTime CreatedUtc, string Provider, string? Database, string? Note, bool IsLocalFile);

public sealed record CreateBackupRequest([property: Description("Optional note stored with the backup.")] string? Note);

#endregion

#region Plugins

public sealed record PluginDto(
    [property: Description("Folder name under App_Data/plugins; used in every plugin call.")] string Id,
    string Name,
    string? Description,
    string? Version,
    string? Author,
    [property: Description("running, stopped or failed.")] string Status,
    [property: Description("Whether the plugin starts with the application.")] bool Enabled,
    [property: Description("Why loading or starting failed, when status is failed.")] string? Error,
    string? AssemblyName,
    DateTime? InstalledAt,
    DateTime? StartedAt,
    [property: Description("Route templates of the plugin's back-office pages (/admin/…).")] IReadOnlyList<string> AdminPages,
    [property: Description("Route templates of the plugin's site pages.")] IReadOnlyList<string> SitePages,
    IReadOnlyList<PluginMenuItemDto> MenuItems,
    int EndpointCount,
    int ServiceCount,
    bool HasControllers,
    bool HasStaticAssets,
    [property: Description("Size of the plugin's private data folder in bytes.")] long DataSizeBytes,
    [property: Description("Lowest DynCMS version the plugin supports; the plugin is not attached to an older DynCMS.")] string? MinimumCmsVersion,
    [property: Description("compatible, needsNewerCms (update DynCMS), undeclared or invalid.")] string Compatibility);

public sealed record PluginMenuItemDto(string Title, string Url, string Icon, string Placement, string? Roles);

#endregion

#region Users and API keys

public sealed record UserDto(string Id, string UserName, string? FullName, string? Email, bool IsActive, bool IsLocked, IReadOnlyList<string> Roles);

public sealed record CreateUserRequest(
    [property: Description("Sign-in name; must be unique.")] string UserName,
    string Password,
    string? FullName,
    string? Email,
    [property: Description("Role ids, e.g. [\"Editors\"] or [\"Administrators\"].")] IReadOnlyList<string>? Roles);

public sealed record ApiKeyDto(
    Guid Id,
    string Name,
    string Prefix,
    string UserId,
    string UserName,
    IReadOnlyList<string> Scopes,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    DateTime? LastUsedAt,
    DateTime? RevokedAt,
    bool IsActive);

public sealed record ApiKeyCreatedDto(ApiKeyDto Key, [property: Description("The secret. It is shown once and cannot be retrieved again.")] string Secret);

public sealed record CreateApiKeyRequest(
    [property: Description("What the key is for, e.g. \"Claude Code\".")] string Name,
    [property: Description("Scopes to grant. Use [\"*\"] for everything the owner may do. See GET /api-keys/scopes.")] IReadOnlyList<string> Scopes,
    [property: Description("Optional UTC expiry.")] DateTime? ExpiresAt,
    [property: Description("Administrators only: create the key for another user (user id or user name). Defaults to the caller.")] string? ForUser);

#endregion

#region Languages

public sealed record LanguageDto(
    Guid Id,
    [property: Description("Culture name, e.g. en, en-US or de-DE.")] string IsoCode,
    string Name,
    [property: Description("The default language is served at the root URL without a prefix; there is exactly one.")] bool IsDefault,
    [property: Description("A mandatory language must be published before content can be published in any other language.")] bool IsMandatory,
    [property: Description("Language whose values show when a varying property is empty in this one.")] string? FallbackIsoCode,
    [property: Description("URL prefix pages of this language are served under, e.g. /de-de (\"/\" for the default language).")] string UrlPrefix,
    int SortOrder,
    DateTime CreatedAt);

public sealed record SaveLanguageRequest(
    [property: Description("Culture name, e.g. en-US or de-DE. Required on create; on update it renames the language.")] string? IsoCode,
    [property: Description("Display name. Defaults to the culture's English name.")] string? Name,
    [property: Description("Make this the default language (the flag moves off the previous default).")] bool? IsDefault,
    bool? IsMandatory,
    [property: Description("ISO code of an existing language to fall back to, or \"\" to clear.")] string? FallbackIsoCode,
    int? SortOrder);

#endregion

#region Dictionary

public sealed record DictionaryItemDto(
    Guid Id,
    [property: Description("The key templates look up, unique across the dictionary, e.g. blog.readMore.")] string Key,
    [property: Description("The item this one is filed under (organisation only; keys are global).")] Guid? ParentId,
    string? ParentKey,
    [property: Description("Depth in the tree, 0 at the root.")] int Level,
    [property: Description("The text per language, keyed by ISO code. Languages without a text are absent; templates fall back along the language's fallback chain and then to the default language.")] IReadOnlyDictionary<string, string> Translations,
    int SortOrder,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record SaveDictionaryItemRequest(
    [property: Description("The key, e.g. blog.readMore. Required on create; on update it renames the item (templates using the old key stop finding it).")] string? Key,
    [property: Description("Id or key of the item to file this one under, or \"\" for the root. Omit to leave unchanged.")] string? Parent,
    [property: Description("Texts keyed by language ISO code (see GET /languages). Merged into the existing texts unless replaceTranslations is true; an empty string removes that language's text.")] IReadOnlyDictionary<string, string?>? Translations,
    [property: Description("True to drop every text not in translations.")] bool? ReplaceTranslations,
    int? SortOrder);

#endregion

#region Document types

public sealed record PropertyEditorDto(string Alias, string Name, string? Description, string Icon, IReadOnlyList<PropertyEditorConfigFieldDto> ConfigFields, string StoredFormat);

public sealed record PropertyEditorConfigFieldDto(string Key, string Label, string? Description, string Type);

public sealed record DocumentTypeDto(
    Guid Id,
    string Alias,
    string Name,
    string? Description,
    string Icon,
    bool AllowedAsRoot,
    IReadOnlyList<string> AllowedChildTypes,
    IReadOnlyList<string> AllowedTemplates,
    string? DefaultTemplate,
    [property: Description("Content of this type has a name, URL and publish state per language; properties flagged variesByCulture hold a value per language.")] bool VariesByCulture,
    int SortOrder,
    IReadOnlyList<PropertyTypeDto> Properties,
    int? ContentCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record PropertyTypeDto(
    Guid Id,
    string Alias,
    string Name,
    string? Description,
    string Editor,
    string Group,
    bool Mandatory,
    [property: Description("The value is stored per language (only effective when the document type varies by culture).")] bool VariesByCulture,
    int SortOrder,
    IReadOnlyDictionary<string, string> Config);

public sealed record SaveDocumentTypeRequest(
    [property: Description("Unique alias (camelCase). Derived from the name when omitted on create; when updating, omitting it keeps the current alias.")] string? Alias,
    [property: Description("Display name.")] string? Name,
    string? Description,
    [property: Description("Icon name, e.g. document, newspaper, home, folder. Defaults to document.")] string? Icon,
    [property: Description("May this type be created at the top of the tree?")] bool? AllowedAsRoot,
    [property: Description("Aliases of document types that may be created under this one.")] IReadOnlyList<string>? AllowedChildTypes,
    [property: Description("Template aliases this type may render with (stored Liquid templates or component templates).")] IReadOnlyList<string>? AllowedTemplates,
    [property: Description("Template used for new content of this type. Must be in allowedTemplates.")] string? DefaultTemplate,
    [property: Description("Let content of this type vary by language: name, URL segment, publish state and the varying properties are then per language.")] bool? VariesByCulture,
    int? SortOrder,
    [property: Description("The full property list. On update, properties not in the list are removed; properties are matched by id, then by alias.")] IReadOnlyList<SavePropertyTypeRequest>? Properties);

public sealed record SavePropertyTypeRequest(
    [property: Description("Existing property id (optional). Properties without an id are matched to existing ones by alias.")] Guid? Id,
    [property: Description("Unique alias within the type (camelCase). Derived from the name when omitted.")] string? Alias,
    string Name,
    string? Description,
    [property: Description("Property editor alias, e.g. DynCms.TextBox, DynCms.RichText, DynCms.MediaPicker. See the property editors list.")] string Editor,
    [property: Description("Tab the property appears on. Defaults to Content.")] string? Group,
    bool? Mandatory,
    [property: Description("Store the value per language. Needs variesByCulture on the document type.")] bool? VariesByCulture,
    [property: Description("Editor settings keyed by the editor's config field keys (e.g. {\"maxLength\": \"120\"}).")] IReadOnlyDictionary<string, string>? Config);

#endregion

#region Templates

public sealed record TemplateDto(
    Guid? Id,
    string Alias,
    string Name,
    string? Description,
    string Role,
    string Kind,
    string? ComponentType,
    [property: Description("Liquid source. Only stored templates have source; component templates are compiled Blazor components.")] string? Content,
    DateTime? CreatedAt,
    DateTime? UpdatedAt,
    TemplateUsageDto? Usage);

public sealed record TemplateUsageDto(int ContentCount, IReadOnlyList<string> DocumentTypes, IReadOnlyList<string> UsedBy, IReadOnlyList<string> Uses);

public sealed record SaveTemplateRequest(
    [property: Description("Unique alias. Derived from the name when omitted on create; when updating, omitting it keeps the current alias.")] string? Alias,
    string? Name,
    string? Description,
    [property: Description("Page (renders a whole page, assignable to content) or Partial (rendered from another template with {% render 'alias' %}).")] TemplateRole? Role,
    [property: Description("Liquid source. Rich text needs | raw, e.g. {{ content.bodyText | raw }}.")] string? Content);

public sealed record ValidateTemplateRequest(string Content);

public sealed record TemplateValidationDto(bool IsValid, string? Error);

public sealed record PreviewTemplateRequest(
    [property: Description("Liquid source to render.")] string Content,
    [property: Description("Alias of the template the source belongs to (used to pick a sensible preview target and for error messages).")] string? Alias,
    [property: Description("Content id to render against. When omitted, the best target (content already using the alias, else a sample) is used.")] Guid? ContentId);

public sealed record TemplatePreviewDto(bool Success, string Html, string? Error, string? Target);

public sealed record LiquidReferenceDto(
    IReadOnlyList<string> ContentMembers,
    IReadOnlyList<string> Filters,
    IReadOnlyList<string> Notes);

#endregion

#region Content

public sealed record ContentDto(
    Guid Id,
    Guid? ParentId,
    string ContentType,
    string ContentTypeName,
    string Name,
    string UrlSegment,
    string? Template,
    int Level,
    int SortOrder,
    bool IsPublished,
    bool HasPendingChanges,
    [property: Description("Public URL of the published page, when published and requested as a single item.")] string? Url,
    [property: Description("Draft property values keyed by property alias (the values shared by every language when the type varies by culture). Values are stored as strings (see property editor stored formats).")] IReadOnlyDictionary<string, string?> Values,
    PublishedSnapshotDto? Published,
    [property: Description("True when the document type varies by culture: see cultures for the per-language state.")] bool VariesByCulture,
    [property: Description("Per-language state keyed by ISO code (only for content that varies by culture): name, URL segment, publish state and the values of the varying properties.")] IReadOnlyDictionary<string, ContentCultureDto>? Cultures,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? PublishedAt);

public sealed record PublishedSnapshotDto(string? Name, string? UrlSegment, string? Template, IReadOnlyDictionary<string, string?> Values);

public sealed record ContentCultureDto(
    string Name,
    string UrlSegment,
    bool IsPublished,
    bool HasPendingChanges,
    [property: Description("Public URL in this language, when published.")] string? Url,
    [property: Description("Draft values of the properties that vary by culture.")] IReadOnlyDictionary<string, string?> Values,
    PublishedSnapshotDto? Published,
    DateTime UpdatedAt,
    DateTime? PublishedAt);

public sealed record ContentTreeNodeDto(
    Guid Id,
    Guid? ParentId,
    string ContentType,
    string Name,
    string UrlSegment,
    int Level,
    int SortOrder,
    bool IsPublished,
    bool HasPendingChanges,
    [property: Description("Languages the item is published in; null for content that does not vary by culture.")] IReadOnlyList<string>? PublishedCultures,
    IReadOnlyList<ContentTreeNodeDto> Children);

public sealed record CreateContentRequest(
    [property: Description("Document type alias or id.")] string ContentType,
    [property: Description("Parent content id; omit for a root item.")] Guid? ParentId,
    string Name,
    [property: Description("URL segment; slugged from the name when omitted and made unique among siblings.")] string? UrlSegment,
    [property: Description("Template alias; defaults to the document type's default template.")] string? Template,
    [property: Description("Property values keyed by alias. Strings are stored as-is; booleans, numbers, arrays and objects are converted to the editor's stored format.")] IReadOnlyDictionary<string, JsonElement>? Values,
    [property: Description("For document types that vary by culture: the language (ISO code) the name, urlSegment and varying values are for. Defaults to the default language. Ignored otherwise.")] string? Culture,
    [property: Description("Publish immediately after creating (fails with the validation errors when mandatory properties are empty). For content that varies by culture, publishes the culture given.")] bool? Publish);

public sealed record UpdateContentRequest(
    string? Name,
    string? UrlSegment,
    string? Template,
    [property: Description("Property values keyed by alias. Merged into the draft unless replaceValues is true. Use null to clear a value.")] IReadOnlyDictionary<string, JsonElement>? Values,
    [property: Description("When true, values not in the request are cleared.")] bool? ReplaceValues,
    [property: Description("For document types that vary by culture: the language (ISO code) the name, urlSegment and varying values are for; a language that does not exist on the item yet is created by giving it a name. Defaults to the default language.")] string? Culture,
    [property: Description("Publish after saving (the culture given, for content that varies by culture).")] bool? Publish);

public sealed record PublishContentRequest(
    [property: Description("For content that varies by culture: the languages to publish. Omit to publish every language that has a name. Mandatory languages must be published too.")] IReadOnlyList<string>? Cultures);

public sealed record UnpublishContentRequest(
    [property: Description("For content that varies by culture: the one language to take offline. Omit to unpublish every language.")] string? Culture);

public sealed record MoveContentRequest([property: Description("-1 moves up one place among its siblings, 1 moves down.")] int Direction);

public sealed record ContentValidationErrorDto(string Property, string Message, [property: Description("The language the error belongs to; null for shared fields.")] string? Culture);

public sealed record PublishResultDto(bool Success, IReadOnlyList<ContentValidationErrorDto> Errors, ContentDto? Content);

public sealed record PublishedContentDto(
    Guid Id,
    Guid? ParentId,
    string Name,
    string UrlSegment,
    string Url,
    string ContentType,
    string ContentTypeName,
    string? Template,
    int Level,
    int SortOrder,
    [property: Description("The language this view is in.")] string Culture,
    [property: Description("Languages the item is available in; null for content that does not vary by culture.")] IReadOnlyList<string>? Cultures,
    IReadOnlyDictionary<string, string?> Values,
    DateTime? PublishedAt,
    DateTime UpdatedAt);

#endregion

#region Media

public sealed record MediaDto(
    Guid Id,
    Guid? ParentId,
    string Name,
    bool IsFolder,
    string? FileName,
    string? Extension,
    string? MimeType,
    long SizeBytes,
    bool IsImage,
    [property: Description("Public URL of the file; null for folders. Store the id (not the URL) in media picker properties.")] string? Url,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CreateMediaFolderRequest(string Name, Guid? ParentId);

public sealed record RenameMediaRequest(string Name);

public sealed record UploadMediaRequest(
    [property: Description("File name with extension; the extension decides whether the upload is allowed.")] string FileName,
    [property: Description("File content, base64 encoded.")] string ContentBase64,
    [property: Description("Destination folder id; omit for the library root.")] Guid? FolderId,
    string? MimeType);

#endregion

public sealed record ProblemDto(int Status, string Title, string? Detail, object? Errors);

/// <summary>JSON settings shared by the REST API and the MCP tools.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }
}
