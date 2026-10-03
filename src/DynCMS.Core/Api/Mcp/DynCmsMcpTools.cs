using System.ComponentModel;
using System.Text.Json;
using DynCMS.Core.Data;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DynCMS.Core.Api.Mcp;

/// <summary>
/// The MCP tool set an AI agent uses to manage a DynCMS site. Every tool is a thin call into
/// <see cref="CmsManagement"/>, so the tools and the REST API always agree; authorisation is the caller's API key
/// (sent as <c>Authorization: Bearer</c> on the MCP requests) and the permissions of the user who owns it.
/// </summary>
[McpServerToolType]
public sealed class DynCmsMcpTools(CmsManagement cms)
{
    // ---- Orientation --------------------------------------------------------------------------------------------

    [McpServerTool(Name = "cms_overview", ReadOnly = true, Idempotent = true, Title = "Site overview")]
    [Description("Start here. Returns the DynCMS version, who you are (user, roles, scopes, effective permissions), item counts and modelling hints. Lists which other tools will work for you.")]
    public Task<ApiInfoDto> Overview(CancellationToken ct) => Run(() => cms.GetInfoAsync(ct));

    [McpServerTool(Name = "list_property_editors", ReadOnly = true, Idempotent = true, Title = "List property editors")]
    [Description("The property editors a document type property can use (alias, settings, and the string format each stores). Use the alias in the editor field of a property.")]
    public Task<IReadOnlyList<PropertyEditorDto>> ListPropertyEditors() => Run(() => Task.FromResult(cms.GetPropertyEditors()));

    // ---- Languages ----------------------------------------------------------------------------------------------

    [McpServerTool(Name = "list_languages", ReadOnly = true, Idempotent = true, Title = "List languages")]
    [Description("The languages of the site with their ISO codes, which one is the default (served at /), which are mandatory and their fallbacks. Content of document types that vary by culture exists per language; other languages are served under /{iso-code}/.")]
    public Task<IReadOnlyList<LanguageDto>> ListLanguages(CancellationToken ct) => Run(() => cms.GetLanguagesAsync(ct));

    [McpServerTool(Name = "create_language", Title = "Add language")]
    [Description("Add a language by culture name (en-US, de-DE, fr…). Optionally make it the default or mandatory, or give it a fallback language.")]
    public Task<LanguageDto> CreateLanguage(SaveLanguageRequest request, CancellationToken ct) => Run(() => cms.CreateLanguageAsync(request, ct));

    [McpServerTool(Name = "update_language", Title = "Update language")]
    [Description("Change a language: name, default flag, mandatory flag, fallback language or sort order. Only fields you send are changed.")]
    public Task<LanguageDto> UpdateLanguage([Description("ISO code, e.g. de-DE.")] string isoCode, SaveLanguageRequest request, CancellationToken ct) =>
        Run(() => cms.UpdateLanguageAsync(isoCode, request, ct));

    [McpServerTool(Name = "delete_language", Destructive = true, Title = "Delete language")]
    [Description("Remove a language and every per-language name and value stored in it. The default language cannot be removed.")]
    public Task<string> DeleteLanguage([Description("ISO code.")] string isoCode, CancellationToken ct) =>
        Run(async () => { await cms.DeleteLanguageAsync(isoCode, ct); return $"Language '{isoCode}' deleted."; });

    // ---- Dictionary ---------------------------------------------------------------------------------------------

    [McpServerTool(Name = "list_dictionary_items", ReadOnly = true, Idempotent = true, Title = "List dictionary items")]
    [Description("The dictionary: translated labels templates print by key ({{ 'blog.readMore' | dictionary }} in Liquid, T(\"blog.readMore\") in Blazor). Every item in tree order with its text per language ISO code. A language without a text falls back along its fallback chain, then to the default language.")]
    public Task<IReadOnlyList<DictionaryItemDto>> ListDictionaryItems(CancellationToken ct) => Run(() => cms.GetDictionaryItemsAsync(ct));

    [McpServerTool(Name = "get_dictionary_item", ReadOnly = true, Idempotent = true, Title = "Get dictionary item")]
    [Description("One dictionary item by key or id, with its texts per language.")]
    public Task<DictionaryItemDto> GetDictionaryItem([Description("Key (e.g. blog.readMore) or GUID.")] string idOrKey, CancellationToken ct) =>
        Run(() => cms.GetDictionaryItemAsync(idOrKey, ct));

    [McpServerTool(Name = "get_dictionary_values", ReadOnly = true, Idempotent = true, Title = "Get dictionary values")]
    [Description("Every dictionary key with its text in one language, resolved with fallback the way templates see it. Handy to check what a page in that language will print.")]
    public Task<IReadOnlyDictionary<string, string>> GetDictionaryValues([Description("ISO code of the language, e.g. de-DE. Defaults to the default language.")] string? culture, CancellationToken ct) =>
        Run(() => cms.GetDictionaryValuesAsync(culture, ct));

    [McpServerTool(Name = "create_dictionary_item", Title = "Create dictionary item")]
    [Description("Add a dictionary item: a unique key (e.g. blog.readMore), its texts keyed by language ISO code, and optionally the key or id of a parent item to file it under.")]
    public Task<DictionaryItemDto> CreateDictionaryItem(SaveDictionaryItemRequest request, CancellationToken ct) => Run(() => cms.CreateDictionaryItemAsync(request, ct));

    [McpServerTool(Name = "update_dictionary_item", Title = "Update dictionary item")]
    [Description("Change a dictionary item: rename the key, move it (parent = key/id, or \"\" for the root), or set texts per language (merged; \"\" removes a language's text; replaceTranslations=true drops the ones not sent). Only fields you send are changed.")]
    public Task<DictionaryItemDto> UpdateDictionaryItem([Description("Key or GUID.")] string idOrKey, SaveDictionaryItemRequest request, CancellationToken ct) =>
        Run(() => cms.UpdateDictionaryItemAsync(idOrKey, request, ct));

    [McpServerTool(Name = "delete_dictionary_item", Destructive = true, Title = "Delete dictionary item")]
    [Description("Delete a dictionary item and everything filed under it. Templates that use the key print the key itself afterwards.")]
    public Task<string> DeleteDictionaryItem([Description("Key or GUID.")] string idOrKey, CancellationToken ct) =>
        Run(async () => { await cms.DeleteDictionaryItemAsync(idOrKey, ct); return $"Dictionary item '{idOrKey}' deleted."; });

    // ---- Document types -----------------------------------------------------------------------------------------

    [McpServerTool(Name = "list_document_types", ReadOnly = true, Idempotent = true, Title = "List document types")]
    [Description("All document types with their properties, allowed children and templates.")]
    public Task<IReadOnlyList<DocumentTypeDto>> ListDocumentTypes(CancellationToken ct) => Run(() => cms.GetDocumentTypesAsync(ct));

    [McpServerTool(Name = "get_document_type", ReadOnly = true, Idempotent = true, Title = "Get document type")]
    [Description("One document type by alias or id, including how many content items use it.")]
    public Task<DocumentTypeDto> GetDocumentType([Description("Alias (e.g. article) or GUID.")] string idOrAlias, CancellationToken ct) =>
        Run(() => cms.GetDocumentTypeAsync(idOrAlias, ct));

    [McpServerTool(Name = "create_document_type", Title = "Create document type")]
    [Description("Create a document type. Give it a name, whether it may be a root, allowed child type aliases, allowed template aliases and its properties (name, editor alias, optional alias/group/mandatory/variesByCulture/config). Set variesByCulture=true on the type (and on the properties that should differ per language) for multilingual content. Aliases are derived from names when omitted.")]
    public Task<DocumentTypeDto> CreateDocumentType(SaveDocumentTypeRequest request, CancellationToken ct) => Run(() => cms.CreateDocumentTypeAsync(request, ct));

    [McpServerTool(Name = "update_document_type", Title = "Update document type")]
    [Description("Change a document type. Only fields you send are changed. When you send properties, send the complete list: properties are matched by id, then alias; anything missing is removed.")]
    public Task<DocumentTypeDto> UpdateDocumentType([Description("Alias or GUID.")] string idOrAlias, SaveDocumentTypeRequest request, CancellationToken ct) =>
        Run(() => cms.UpdateDocumentTypeAsync(idOrAlias, request, ct));

    [McpServerTool(Name = "delete_document_type", Destructive = true, Title = "Delete document type")]
    [Description("Delete a document type. Fails while content still uses it.")]
    public Task<string> DeleteDocumentType([Description("Alias or GUID.")] string idOrAlias, CancellationToken ct) =>
        Run(async () => { await cms.DeleteDocumentTypeAsync(idOrAlias, ct); return $"Document type '{idOrAlias}' deleted."; });

    // ---- Templates ----------------------------------------------------------------------------------------------

    [McpServerTool(Name = "list_templates", ReadOnly = true, Idempotent = true, Title = "List templates")]
    [Description("All templates: stored Liquid templates (editable) and component templates compiled into the site. Pages render content; partials are fragments rendered with {% render 'alias' %}.")]
    public Task<IReadOnlyList<TemplateDto>> ListTemplates() => Run(() => Task.FromResult(cms.GetTemplates()));

    [McpServerTool(Name = "get_template", ReadOnly = true, Idempotent = true, Title = "Get template")]
    [Description("A template by alias or id, with its Liquid source (stored templates) and where it is used.")]
    public Task<TemplateDto> GetTemplate([Description("Alias or GUID.")] string idOrAlias, CancellationToken ct) => Run(() => cms.GetTemplateAsync(idOrAlias, ct));

    [McpServerTool(Name = "liquid_reference", ReadOnly = true, Idempotent = true, Title = "Liquid reference")]
    [Description("What templates can use: content members, DynCMS filters (media_url, content_url, content_of_type, children_of, tags, raw…) and the rules (HTML encoding, partials, property lookup by alias). Read before writing a template.")]
    public Task<LiquidReferenceDto> LiquidReference() => Run(() => Task.FromResult(cms.GetLiquidReference()));

    [McpServerTool(Name = "create_template", Title = "Create template")]
    [Description("Create a stored Liquid template. role is Page (default) or Partial. The Liquid must parse. Rich text needs | raw: {{ content.bodyText | raw }}.")]
    public Task<TemplateDto> CreateTemplate(SaveTemplateRequest request, CancellationToken ct) => Run(() => cms.CreateTemplateAsync(request, ct));

    [McpServerTool(Name = "update_template", Title = "Update template")]
    [Description("Change a stored template's name, alias, description, role or Liquid source. Live immediately. Component templates must be overridden first (create_template_override).")]
    public Task<TemplateDto> UpdateTemplate([Description("Alias or GUID.")] string idOrAlias, SaveTemplateRequest request, CancellationToken ct) =>
        Run(() => cms.UpdateTemplateAsync(idOrAlias, request, ct));

    [McpServerTool(Name = "delete_template", Destructive = true, Title = "Delete template")]
    [Description("Delete a stored template. Fails while content, a document type or another template uses it.")]
    public Task<string> DeleteTemplate([Description("Alias or GUID.")] string idOrAlias, CancellationToken ct) =>
        Run(async () => { await cms.DeleteTemplateAsync(idOrAlias, ct); return $"Template '{idOrAlias}' deleted."; });

    [McpServerTool(Name = "duplicate_template", Title = "Duplicate template")]
    [Description("Copy a stored template under a new alias as a starting point.")]
    public Task<TemplateDto> DuplicateTemplate([Description("Alias or GUID.")] string idOrAlias, CancellationToken ct) => Run(() => cms.DuplicateTemplateAsync(idOrAlias, ct));

    [McpServerTool(Name = "create_template_override", Title = "Override component template")]
    [Description("Create an editable Liquid version of a component (compiled) template with the same alias. From then on the Liquid renders instead of the component; delete it to fall back.")]
    public Task<TemplateDto> CreateTemplateOverride([Description("Alias of the component template.")] string alias, CancellationToken ct) => Run(() => cms.CreateOverrideAsync(alias, ct));

    [McpServerTool(Name = "validate_template", ReadOnly = true, Idempotent = true, Title = "Validate Liquid")]
    [Description("Check Liquid syntax without saving.")]
    public Task<TemplateValidationDto> ValidateTemplate([Description("Liquid source.")] string content) =>
        Run(() => Task.FromResult(cms.ValidateTemplate(new ValidateTemplateRequest(content))));

    [McpServerTool(Name = "preview_template", ReadOnly = true, Title = "Preview Liquid")]
    [Description("Render Liquid source (saved or not) against real content, or a sample when there is none, and return the HTML or the render error.")]
    public Task<TemplatePreviewDto> PreviewTemplate(PreviewTemplateRequest request, CancellationToken ct) => Run(() => cms.PreviewTemplateAsync(request, ct));

    // ---- Content ------------------------------------------------------------------------------------------------

    [McpServerTool(Name = "get_content_tree", ReadOnly = true, Idempotent = true, Title = "Content tree")]
    [Description("The whole content tree (or the subtree under rootId), nested, with ids, document type aliases and publish state. Use depth to limit levels.")]
    public Task<IReadOnlyList<ContentTreeNodeDto>> GetContentTree(Guid? rootId, int? depth, CancellationToken ct) => Run(() => cms.GetContentTreeAsync(rootId, depth, ct));

    [McpServerTool(Name = "get_content", ReadOnly = true, Idempotent = true, Title = "Get content")]
    [Description("A content item with its draft values, published snapshot and public URL. For content that varies by culture, cultures holds the per-language names, values, publish state and URLs.")]
    public Task<ContentDto> GetContent(Guid id, CancellationToken ct) => Run(() => cms.GetContentAsync(id, ct));

    [McpServerTool(Name = "search_content", ReadOnly = true, Idempotent = true, Title = "Search content")]
    [Description("Find content by name.")]
    public Task<IReadOnlyList<ContentDto>> SearchContent(string term, [Description("Max results, default 25.")] int? take, CancellationToken ct) =>
        Run(() => cms.SearchContentAsync(term, take ?? 25, ct));

    [McpServerTool(Name = "get_published_content", ReadOnly = true, Idempotent = true, Title = "Published content by route")]
    [Description("Resolve a site path such as /, /blog/my-post or /de/blog/mein-beitrag to the published page (as the public site sees it). preview=true resolves drafts too. A language prefix in the path wins; otherwise culture picks the language (default language when omitted).")]
    public Task<PublishedContentDto?> GetPublishedContent([Description("Site path, e.g. /blog/my-post")] string path, bool? preview, [Description("ISO code of the language, e.g. de-DE. Optional.")] string? culture, CancellationToken ct) =>
        Run(() => cms.GetPublishedByRouteAsync(path, preview == true, culture, ct));

    [McpServerTool(Name = "create_content", Title = "Create content")]
    [Description("Create a content item of a document type under parentId (omit for a root). values are keyed by property alias; strings are stored as-is, booleans/numbers/arrays are converted. For document types that vary by culture, culture names the language the name and varying values are for (default language when omitted); add other languages with update_content. Set publish=true to publish immediately; if mandatory properties are empty you get the draft plus the validation errors.")]
    public Task<PublishResultDto> CreateContent(CreateContentRequest request, CancellationToken ct) => Run(() => cms.CreateContentAsync(request, ct));

    [McpServerTool(Name = "update_content", Title = "Update content")]
    [Description("Save the draft of a content item: name, URL segment, template and/or values (merged unless replaceValues=true; null clears a value). For content that varies by culture, culture names the language being edited; giving a name in a new language creates that translation. publish=true publishes after saving (that language).")]
    public Task<PublishResultDto> UpdateContent(Guid id, UpdateContentRequest request, CancellationToken ct) => Run(() => cms.UpdateContentAsync(id, request, ct));

    [McpServerTool(Name = "publish_content", Title = "Publish content")]
    [Description("Copy the draft to the live site. Returns success=false with errors when mandatory properties are empty. For content that varies by culture, cultures lists the languages to publish (every named language when omitted); mandatory languages must be live too.")]
    public Task<PublishResultDto> PublishContent(Guid id, [Description("ISO codes of the languages to publish, e.g. [\"en-US\", \"de-DE\"]. Optional.")] IReadOnlyList<string>? cultures, CancellationToken ct) =>
        Run(() => cms.PublishContentAsync(id, new PublishContentRequest(cultures), ct));

    [McpServerTool(Name = "unpublish_content", Title = "Unpublish content")]
    [Description("Take the item off the site. The draft is kept. For content that varies by culture, culture takes one language offline; omit it to unpublish every language.")]
    public Task<ContentDto> UnpublishContent(Guid id, [Description("ISO code of the one language to unpublish. Optional.")] string? culture, CancellationToken ct) =>
        Run(() => cms.UnpublishContentAsync(id, new UnpublishContentRequest(culture), ct));

    [McpServerTool(Name = "move_content", Title = "Reorder content")]
    [Description("Move an item one place up (direction -1) or down (1) among its siblings.")]
    public Task<ContentDto> MoveContent(Guid id, [Description("-1 = up, 1 = down")] int direction, CancellationToken ct) =>
        Run(() => cms.MoveContentAsync(id, new MoveContentRequest(direction), ct));

    [McpServerTool(Name = "delete_content", Destructive = true, Title = "Delete content")]
    [Description("Delete a content item and every descendant. There is no recycle bin.")]
    public Task<string> DeleteContent(Guid id, CancellationToken ct) =>
        Run(async () => { await cms.DeleteContentAsync(id, ct); return $"Content {id} deleted."; });

    // ---- Media --------------------------------------------------------------------------------------------------

    [McpServerTool(Name = "list_media", ReadOnly = true, Idempotent = true, Title = "List media")]
    [Description("Files and folders in the media library root, or inside folderId. Store a file's id (not its URL) in media picker properties.")]
    public Task<IReadOnlyList<MediaDto>> ListMedia(Guid? folderId, CancellationToken ct) => Run(() => cms.GetMediaChildrenAsync(folderId, ct));

    [McpServerTool(Name = "get_media", ReadOnly = true, Idempotent = true, Title = "Get media")]
    [Description("One media item with its public URL.")]
    public Task<MediaDto> GetMedia(Guid id, CancellationToken ct) => Run(() => cms.GetMediaAsync(id, ct));

    [McpServerTool(Name = "create_media_folder", Title = "Create media folder")]
    [Description("Create a folder in the media library.")]
    public Task<MediaDto> CreateMediaFolder(string name, Guid? parentId, CancellationToken ct) => Run(() => cms.CreateMediaFolderAsync(new CreateMediaFolderRequest(name, parentId), ct));

    [McpServerTool(Name = "upload_media", Title = "Upload media")]
    [Description("Upload a file (base64) into the media library. The extension decides whether it is allowed. Returns the item with its id and URL.")]
    public Task<MediaDto> UploadMedia(UploadMediaRequest request, CancellationToken ct) => Run(() => cms.UploadMediaAsync(request, ct));

    [McpServerTool(Name = "rename_media", Title = "Rename media")]
    [Description("Rename a file or folder.")]
    public Task<MediaDto> RenameMedia(Guid id, string name, CancellationToken ct) => Run(() => cms.RenameMediaAsync(id, new RenameMediaRequest(name), ct));

    [McpServerTool(Name = "delete_media", Destructive = true, Title = "Delete media")]
    [Description("Delete a file, or a folder with everything in it.")]
    public Task<string> DeleteMedia(Guid id, CancellationToken ct) =>
        Run(async () => { await cms.DeleteMediaAsync(id, ct); return $"Media {id} deleted."; });

    // ---- Administration -----------------------------------------------------------------------------------------

    [McpServerTool(Name = "list_users", ReadOnly = true, Idempotent = true, Title = "List users")]
    [Description("Back-office users and their roles (administrators only).")]
    public Task<IReadOnlyList<UserDto>> ListUsers() => Run(() => Task.FromResult(cms.GetUsers()));

    [McpServerTool(Name = "create_user", Title = "Create user")]
    [Description("Create a back-office user with roles (Editors or Administrators). Administrators only.")]
    public Task<UserDto> CreateUser(CreateUserRequest request) => Run(() => Task.FromResult(cms.CreateUser(request)));

    [McpServerTool(Name = "system_version", ReadOnly = true, Idempotent = true, Title = "System version")]
    [Description("DynCMS application version and database schema version (administrators only). Plugins need a minimum DynCMS version.")]
    public Task<SystemVersionDto> SystemVersion(CancellationToken ct) => Run(() => cms.GetSystemVersionAsync(ct));

    [McpServerTool(Name = "database_info",ReadOnly = true, Idempotent = true, Title = "Database info")]
    [Description("Database provider, location and table row counts (administrators only).")]
    public Task<DatabaseInfoDto> DatabaseInfo([Description("primary (default) or analytics.")] string? role, CancellationToken ct) => Run(() => cms.GetDatabaseInfoAsync(role, ct));

    [McpServerTool(Name = "create_backup", Title = "Create database backup")]
    [Description("Back up the database (administrators only). Do this before large structural changes.")]
    public Task<BackupDto> CreateBackup([Description("Optional note.")] string? note, [Description("primary (default) or analytics.")] string? role, CancellationToken ct) => Run(() => cms.CreateBackupAsync(new CreateBackupRequest(note), role, ct));

    // ---- Analytics ----------------------------------------------------------------------------------------------

    [McpServerTool(Name = "analytics_report", ReadOnly = true, Idempotent = true, Title = "Visitor report")]
    [Description("How the site is doing: page views, unique visitors, sessions, bounce rate and session length for a period with the previous period for comparison, visitors right now, the chart series, and the top pages, referrers, countries, browsers, operating systems, devices and languages. Bots are excluded. period: today, yesterday, 7d, 30d (default), 90d, 12m — or from/to dates (yyyy-MM-dd).")]
    public Task<AnalyticsReportDto> AnalyticsReport(
        [Description("today, yesterday, 7d, 30d, 90d or 12m. Ignored when from/to are given.")] string? period,
        [Description("First day, yyyy-MM-dd (optional).")] string? from,
        [Description("Last day, yyyy-MM-dd (optional, defaults to from).")] string? to,
        CancellationToken ct) => Run(() => cms.GetAnalyticsReportAsync(period, from, to, ct));

    [McpServerTool(Name = "analytics_pages", ReadOnly = true, Idempotent = true, Title = "Page views per page")]
    [Description("Page views and unique visitors per site path for a period, most viewed first. Optional search filters by path or title.")]
    public Task<IReadOnlyList<AnalyticsPageDto>> AnalyticsPages(string? period, string? from, string? to, [Description("How many rows, default 50.")] int? take, string? search, CancellationToken ct) =>
        Run(() => cms.GetAnalyticsPagesAsync(period, from, to, take, search, ct));

    [McpServerTool(Name = "analytics_views", ReadOnly = true, Idempotent = true, Title = "Page view log")]
    [Description("The raw page view log, newest first: each view with its visitor and session id, IP address, browser, operating system, device, language, country, region and city, referrer and whether the page was found. Filter by path (append * for a prefix), visitorId (to follow one visitor's journey), sessionId, ip, country (ISO code), free text, notFound; bots=true includes crawlers. Page with skip/take (max 500).")]
    public Task<PageViewLogDto> AnalyticsViews(string? period, string? from, string? to, string? path, string? visitorId, string? sessionId, string? ip, string? country, string? search, bool? notFound, bool? bots, int? skip, int? take, CancellationToken ct) =>
        Run(() => cms.GetAnalyticsViewsAsync(period, from, to, path, visitorId, sessionId, ip, country, search, notFound, bots, skip, take, ct));

    // ---- Plugins ------------------------------------------------------------------------------------------------

    [McpServerTool(Name = "list_plugins", ReadOnly = true, Idempotent = true, Title = "List plugins")]
    [Description("The installed plugins (administrators): id, name, version, status (running, stopped, failed) with the error when failed, the back-office and site pages each one adds, its menu items, endpoint count and data folder size.")]
    public Task<IReadOnlyList<PluginDto>> ListPlugins() => Run(() => Task.FromResult(cms.GetPlugins()));

    [McpServerTool(Name = "start_plugin", Title = "Start plugin")]
    [Description("Start a stopped or failed plugin by id. It also starts with the application from now on.")]
    public Task<PluginDto> StartPlugin([Description("Plugin id.")] string id, CancellationToken ct) => Run(() => cms.StartPluginAsync(id, ct));

    [McpServerTool(Name = "stop_plugin", Title = "Stop plugin")]
    [Description("Stop a running plugin by id: its pages, endpoints and menu items disappear until it is started again. It stays installed.")]
    public Task<PluginDto> StopPlugin([Description("Plugin id.")] string id, CancellationToken ct) => Run(() => cms.StopPluginAsync(id, ct));

    [McpServerTool(Name = "reload_plugin", Title = "Reload plugin")]
    [Description("Stop a plugin, load its assemblies from its folder again and start it. Use after a new build was copied into App_Data/plugins/{id}.")]
    public Task<PluginDto> ReloadPlugin([Description("Plugin id.")] string id, CancellationToken ct) => Run(() => cms.ReloadPluginAsync(id, ct));

    [McpServerTool(Name = "uninstall_plugin", Destructive = true, Title = "Uninstall plugin")]
    [Description("Stop and remove a plugin. Its private data folder (databases, files) is kept unless deleteData is true.")]
    public Task<string> UninstallPlugin([Description("Plugin id.")] string id, [Description("Also delete the plugin's data folder.")] bool? deleteData, CancellationToken ct) =>
        Run(async () => { await cms.UninstallPluginAsync(id, deleteData == true, ct); return $"Plugin '{id}' uninstalled."; });

    // ---- Plumbing -----------------------------------------------------------------------------------------------

    /// <summary>Surfaces management-layer errors to the agent as tool errors with the original message.</summary>
    private static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (CmsApiException ex)
        {
            var detail = ex.Details is null ? string.Empty : " " + JsonSerializer.Serialize(ex.Details, ApiJson.Options);
            throw new McpException($"{ex.StatusCode} {ex.Message}{detail}");
        }
        catch (DynCmsNotConfiguredException)
        {
            throw new McpException("503 DynCMS has not been set up yet. Open /setup in a browser.");
        }
    }
}
