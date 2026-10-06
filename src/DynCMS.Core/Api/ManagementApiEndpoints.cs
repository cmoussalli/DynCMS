using DynCMS.Core.Api;
using DynCMS.Core.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace DynCMS.Core;

public static class ManagementApiEndpoints
{
    /// <summary>
    /// Maps the management REST API (default <c>/api/v1</c>): document types, templates, content, media, users,
    /// API keys and system information, plus the OpenAPI document at <c>{base}/openapi.json</c>. Every endpoint
    /// requires authentication (an <c>Authorization: Bearer</c> API key, or the back-office cookie) and is then
    /// authorised per operation by <see cref="ICmsAccess"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapDynCmsApi(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<DynCmsOptions>>().Value.Api;
        if (!options.Enabled) return endpoints;

        var basePath = "/" + options.BasePath.Trim('/');

        var openApi = endpoints.MapOpenApi(basePath + "/openapi.json").ExcludeFromDescription();
        if (options.PublicOpenApi) openApi.AllowAnonymous();

        var api = endpoints.MapGroup(basePath)
            .RequireAuthorization()
            .AddEndpointFilter<CmsApiExceptionFilter>();

        // ---- System -------------------------------------------------------------------------------------------
        var system = api.MapGroup("").WithTags("System");
        system.MapGet("/", (CmsManagement m, CancellationToken ct) => m.GetInfoAsync(ct))
            .WithName("GetInfo")
            .WithSummary("Who am I and what is here")
            .WithDescription("Version, readiness, endpoint paths, the caller with roles/scopes/effective permissions, item counts and modelling hints. Call this first.")
            .AllowAnonymous();
        system.MapGet("/system/version", (CmsManagement m, CancellationToken ct) => m.GetSystemVersionAsync(ct))
            .WithName("GetSystemVersion").WithSummary("Application and database versions (administrators)").WithDescription("The DynCMS version, the database schema version it expects and the one recorded in the database. Plugins declare a minimum DynCMS version and are not attached to an older one.");
        system.MapGet("/system/database",(string? role, CmsManagement m, CancellationToken ct) => m.GetDatabaseInfoAsync(role, ct))
            .WithName("GetDatabaseInfo").WithSummary("Database information (administrators)").WithDescription("role: primary (default, content and users) or analytics (page views; the primary database unless configured separately).");
        system.MapGet("/system/backups", (string? role, CmsManagement m, CancellationToken ct) => m.ListBackupsAsync(role, ct))
            .WithName("ListBackups").WithSummary("List database backups (administrators)").WithDescription("role: primary (default) or analytics.");
        system.MapPost("/system/backups", (CreateBackupRequest? body, string? role, CmsManagement m, CancellationToken ct) => m.CreateBackupAsync(body, role, ct))
            .WithName("CreateBackup").WithSummary("Create a database backup (administrators)").WithDescription("role: primary (default) or analytics (only when analytics has its own database).");

        // ---- Plugins --------------------------------------------------------------------------------------------
        var plugins = api.MapGroup("").WithTags("Plugins");
        plugins.MapGet("/plugins", (CmsManagement m) => m.GetPlugins())
            .WithName("ListPlugins").WithSummary("Installed plugins (administrators)").WithDescription("Every plugin with its status (running, stopped, failed), pages, menu items, endpoint count and data folder size.");
        plugins.MapGet("/plugins/{id}", (string id, CmsManagement m) => m.GetPlugin(id))
            .WithName("GetPlugin").WithSummary("One plugin (administrators)");
        plugins.MapPost("/plugins/{id}/start", (string id, CmsManagement m, CancellationToken ct) => m.StartPluginAsync(id, ct))
            .WithName("StartPlugin").WithSummary("Start a plugin (administrators)").WithDescription("Loads the plugin if needed and starts it; it also starts with the application from now on.");
        plugins.MapPost("/plugins/{id}/stop", (string id, CmsManagement m, CancellationToken ct) => m.StopPluginAsync(id, ct))
            .WithName("StopPlugin").WithSummary("Stop a plugin (administrators)").WithDescription("Its pages, endpoints and menu items disappear; it stays installed and will not start with the application.");
        plugins.MapPost("/plugins/{id}/reload", (string id, CmsManagement m, CancellationToken ct) => m.ReloadPluginAsync(id, ct))
            .WithName("ReloadPlugin").WithSummary("Reload a plugin from disk (administrators)").WithDescription("Stops the plugin, loads the assemblies in its folder again and starts it. Use after copying a new build into the plugin folder.");
        plugins.MapPost("/plugins/upload", async (HttpRequest request, CmsManagement m, CancellationToken ct) =>
            {
                if (!request.HasFormContentType) throw CmsApiException.BadRequest("Send multipart/form-data with a 'file' part: a .dll or a .zip package.");
                var form = await request.ReadFormAsync(ct);
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault() ?? throw CmsApiException.BadRequest("The 'file' part is missing.");
                await using var stream = file.OpenReadStream();
                var dto = await m.InstallPluginAsync(file.FileName, stream, ct);
                return Results.Created($"{basePath}/plugins/{dto.Id}", dto);
            })
            .WithName("InstallPlugin").WithSummary("Install a plugin from a .dll or .zip (administrators)").WithDescription("Replaces a plugin with the same id (its data folder is kept) and starts it. Uploaded code runs with the rights of the application.")
            .Accepts<IFormFile>("multipart/form-data")
            .DisableAntiforgery();
        plugins.MapDelete("/plugins/{id}", async (string id, bool? deleteData, CmsManagement m, CancellationToken ct) => { await m.UninstallPluginAsync(id, deleteData == true, ct); return Results.NoContent(); })
            .WithName("UninstallPlugin").WithSummary("Uninstall a plugin (administrators)").WithDescription("Stops and removes the plugin. Its private data folder is kept unless deleteData=true.");

        // ---- Users and API keys ---------------------------------------------------------------------------------
        var users = api.MapGroup("").WithTags("Users & API keys");
        users.MapGet("/users", (CmsManagement m) => m.GetUsers()).WithName("ListUsers").WithSummary("List back-office users (administrators)");
        users.MapPost("/users", (CreateUserRequest body, CmsManagement m) => Results.Created($"{basePath}/users", m.CreateUser(body)))
            .WithName("CreateUser").WithSummary("Create a back-office user (administrators)");
        users.MapGet("/roles", (CmsManagement m) => m.GetRoles()).WithName("ListRoles").WithSummary("List roles (administrators)");

        users.MapGet("/api-keys/scopes", (CmsManagement m) => m.GetScopes()).WithName("ListScopes").WithSummary("All API key scopes and what they allow");
        users.MapGet("/api-keys/scopes/grantable", (CmsManagement m) => m.GetGrantableScopes()).WithName("ListGrantableScopes").WithSummary("Scopes the caller may put on a new key");
        users.MapGet("/api-keys", (bool? all, CmsManagement m, CancellationToken ct) => m.GetApiKeysAsync(all == true, ct))
            .WithName("ListApiKeys").WithSummary("List API keys").WithDescription("Your own keys; administrators can pass ?all=true for everyone's. Secrets are never returned.");
        users.MapPost("/api-keys", async (CreateApiKeyRequest body, CmsManagement m, CancellationToken ct) => Results.Created($"{basePath}/api-keys", await m.CreateApiKeyAsync(body, ct)))
            .WithName("CreateApiKey").WithSummary("Create an API key").WithDescription("Returns the secret once. Session users may create keys for themselves; an API key needs the api-keys:manage scope.");
        users.MapDelete("/api-keys/{id:guid}", async (Guid id, CmsManagement m, CancellationToken ct) => { await m.RevokeApiKeyAsync(id, ct); return Results.NoContent(); })
            .WithName("RevokeApiKey").WithSummary("Revoke an API key");

        // ---- Languages ------------------------------------------------------------------------------------------
        var langs = api.MapGroup("").WithTags("Languages");
        langs.MapGet("/languages", (CmsManagement m, CancellationToken ct) => m.GetLanguagesAsync(ct))
            .WithName("ListLanguages").WithSummary("The languages of the site").WithDescription("The default language is served at the root; others under /{iso-code}/. Content of document types that vary by culture exists per language.");
        langs.MapGet("/languages/{isoCode}", (string isoCode, CmsManagement m, CancellationToken ct) => m.GetLanguageAsync(isoCode, ct))
            .WithName("GetLanguage").WithSummary("Get a language");
        langs.MapPost("/languages", async (SaveLanguageRequest body, CmsManagement m, CancellationToken ct) =>
            {
                var dto = await m.CreateLanguageAsync(body, ct);
                return Results.Created($"{basePath}/languages/{dto.IsoCode}", dto);
            }).WithName("CreateLanguage").WithSummary("Add a language").WithDescription("isoCode must be a .NET culture name such as en-US or de-DE. The first language ever added becomes the default.");
        langs.MapPut("/languages/{isoCode}", (string isoCode, SaveLanguageRequest body, CmsManagement m, CancellationToken ct) => m.UpdateLanguageAsync(isoCode, body, ct))
            .WithName("UpdateLanguage").WithSummary("Change a language (name, default, mandatory, fallback)");
        langs.MapDelete("/languages/{isoCode}", async (string isoCode, CmsManagement m, CancellationToken ct) => { await m.DeleteLanguageAsync(isoCode, ct); return Results.NoContent(); })
            .WithName("DeleteLanguage").WithSummary("Remove a language and the content stored in it (not the default language)");

        // ---- Dictionary -----------------------------------------------------------------------------------------
        var dict = api.MapGroup("").WithTags("Dictionary");
        dict.MapGet("/dictionary", (CmsManagement m, CancellationToken ct) => m.GetDictionaryItemsAsync(ct))
            .WithName("ListDictionaryItems").WithSummary("The dictionary: translated labels by key").WithDescription("Every item in tree order with its texts per language. Templates print them with {{ 'key' | dictionary }} (Liquid) or T(\"key\") (Blazor), in the page's language with fallback.");
        dict.MapGet("/dictionary/values", (string? culture, CmsManagement m, CancellationToken ct) => m.GetDictionaryValuesAsync(culture, ct))
            .WithName("GetDictionaryValues").WithSummary("Every key with its text in one language").WithDescription("Resolved the way templates see it: the language's own text, else its fallback language's, else the default language's. culture defaults to the default language.");
        dict.MapGet("/dictionary/{idOrKey}", (string idOrKey, CmsManagement m, CancellationToken ct) => m.GetDictionaryItemAsync(idOrKey, ct))
            .WithName("GetDictionaryItem").WithSummary("Get a dictionary item by id or key");
        dict.MapPost("/dictionary", async (SaveDictionaryItemRequest body, CmsManagement m, CancellationToken ct) =>
            {
                var dto = await m.CreateDictionaryItemAsync(body, ct);
                return Results.Created($"{basePath}/dictionary/{dto.Id}", dto);
            }).WithName("CreateDictionaryItem").WithSummary("Add a dictionary item").WithDescription("key is required and must be unique. translations holds the text per language ISO code; parent files the item under another one.");
        dict.MapPut("/dictionary/{idOrKey}", (string idOrKey, SaveDictionaryItemRequest body, CmsManagement m, CancellationToken ct) => m.UpdateDictionaryItemAsync(idOrKey, body, ct))
            .WithName("UpdateDictionaryItem").WithSummary("Change a dictionary item (key, parent, texts)");
        dict.MapDelete("/dictionary/{idOrKey}", async (string idOrKey, CmsManagement m, CancellationToken ct) => { await m.DeleteDictionaryItemAsync(idOrKey, ct); return Results.NoContent(); })
            .WithName("DeleteDictionaryItem").WithSummary("Delete a dictionary item and everything filed under it");

        // ---- Document types -------------------------------------------------------------------------------------
        var types = api.MapGroup("").WithTags("Document types");
        types.MapGet("/property-editors", (CmsManagement m) => m.GetPropertyEditors())
            .WithName("ListPropertyEditors").WithSummary("Property editors").WithDescription("The editors a property can use, their settings and the format each one stores its value in.");
        types.MapGet("/document-types", (CmsManagement m, CancellationToken ct) => m.GetDocumentTypesAsync(ct)).WithName("ListDocumentTypes").WithSummary("List document types");
        types.MapGet("/document-types/{idOrAlias}", (string idOrAlias, CmsManagement m, CancellationToken ct) => m.GetDocumentTypeAsync(idOrAlias, ct)).WithName("GetDocumentType").WithSummary("Get a document type by id or alias");
        types.MapPost("/document-types", async (SaveDocumentTypeRequest body, CmsManagement m, CancellationToken ct) =>
            {
                var dto = await m.CreateDocumentTypeAsync(body, ct);
                return Results.Created($"{basePath}/document-types/{dto.Id}", dto);
            }).WithName("CreateDocumentType").WithSummary("Create a document type");
        types.MapPut("/document-types/{idOrAlias}", (string idOrAlias, SaveDocumentTypeRequest body, CmsManagement m, CancellationToken ct) => m.UpdateDocumentTypeAsync(idOrAlias, body, ct))
            .WithName("UpdateDocumentType").WithSummary("Update a document type").WithDescription("Only the fields present are changed. When properties is present it is the complete list: properties not in it are removed.");
        types.MapDelete("/document-types/{idOrAlias}", async (string idOrAlias, CmsManagement m, CancellationToken ct) => { await m.DeleteDocumentTypeAsync(idOrAlias, ct); return Results.NoContent(); })
            .WithName("DeleteDocumentType").WithSummary("Delete a document type (fails while content uses it)");

        // ---- Templates --------------------------------------------------------------------------------------------
        var templates = api.MapGroup("").WithTags("Templates");
        templates.MapGet("/templates", (CmsManagement m) => m.GetTemplates()).WithName("ListTemplates").WithSummary("List templates (stored Liquid and compiled components)");
        templates.MapGet("/templates/liquid-reference", (CmsManagement m) => m.GetLiquidReference()).WithName("GetLiquidReference").WithSummary("Liquid variables, filters and rules for template authors");
        templates.MapGet("/templates/{idOrAlias}", (string idOrAlias, CmsManagement m, CancellationToken ct) => m.GetTemplateAsync(idOrAlias, ct)).WithName("GetTemplate").WithSummary("Get a template with its source and usage");
        templates.MapPost("/templates", async (SaveTemplateRequest body, CmsManagement m, CancellationToken ct) =>
            {
                var dto = await m.CreateTemplateAsync(body, ct);
                return Results.Created($"{basePath}/templates/{dto.Id}", dto);
            }).WithName("CreateTemplate").WithSummary("Create a stored Liquid template (page or partial)");
        templates.MapPut("/templates/{idOrAlias}", (string idOrAlias, SaveTemplateRequest body, CmsManagement m, CancellationToken ct) => m.UpdateTemplateAsync(idOrAlias, body, ct))
            .WithName("UpdateTemplate").WithSummary("Update a stored template (the change is live immediately)");
        templates.MapDelete("/templates/{idOrAlias}", async (string idOrAlias, CmsManagement m, CancellationToken ct) => { await m.DeleteTemplateAsync(idOrAlias, ct); return Results.NoContent(); })
            .WithName("DeleteTemplate").WithSummary("Delete a stored template (fails while something uses it)");
        templates.MapPost("/templates/{idOrAlias}/duplicate", (string idOrAlias, CmsManagement m, CancellationToken ct) => m.DuplicateTemplateAsync(idOrAlias, ct))
            .WithName("DuplicateTemplate").WithSummary("Copy a stored template under a new alias");
        templates.MapPost("/templates/{alias}/override", (string alias, CmsManagement m, CancellationToken ct) => m.CreateOverrideAsync(alias, ct))
            .WithName("CreateTemplateOverride").WithSummary("Create an editable Liquid version of a component template");
        templates.MapPost("/templates/validate", (ValidateTemplateRequest body, CmsManagement m) => m.ValidateTemplate(body))
            .WithName("ValidateTemplate").WithSummary("Check Liquid syntax without saving");
        templates.MapPost("/templates/preview", (PreviewTemplateRequest body, CmsManagement m, CancellationToken ct) => m.PreviewTemplateAsync(body, ct))
            .WithName("PreviewTemplate").WithSummary("Render Liquid source against real or sample content");

        // ---- Content ------------------------------------------------------------------------------------------------
        var content = api.MapGroup("").WithTags("Content");
        content.MapGet("/content/tree", (Guid? rootId, int? depth, CmsManagement m, CancellationToken ct) => m.GetContentTreeAsync(rootId, depth, ct))
            .WithName("GetContentTree").WithSummary("The content tree").WithDescription("Nested nodes with ids, aliases and publish state. Optional rootId to start below a node and depth to limit levels.");
        content.MapGet("/content", (Guid? parentId, CmsManagement m, CancellationToken ct) => m.GetChildrenAsync(parentId, ct))
            .WithName("ListContent").WithSummary("Root items, or the children of parentId");
        content.MapGet("/content/search", (string q, int? take, CmsManagement m, CancellationToken ct) => m.SearchContentAsync(q, take ?? 25, ct))
            .WithName("SearchContent").WithSummary("Search content by name");
        content.MapGet("/content/{id:guid}", (Guid id, CmsManagement m, CancellationToken ct) => m.GetContentAsync(id, ct))
            .WithName("GetContent").WithSummary("Get a content item with draft values and published snapshot");
        content.MapPost("/content", async (CreateContentRequest body, CmsManagement m, CancellationToken ct) =>
            {
                var result = await m.CreateContentAsync(body, ct);
                return result.Content is null ? Results.BadRequest(result) : Results.Created($"{basePath}/content/{result.Content.Id}", result);
            }).WithName("CreateContent").WithSummary("Create content").WithDescription("Creates a draft under parentId (or at the root) and optionally publishes it. Values are keyed by property alias.");
        content.MapPut("/content/{id:guid}", (Guid id, UpdateContentRequest body, CmsManagement m, CancellationToken ct) => m.UpdateContentAsync(id, body, ct))
            .WithName("UpdateContent").WithSummary("Save the draft (name, URL segment, template, values) and optionally publish");
        content.MapPost("/content/{id:guid}/publish", (Guid id, PublishContentRequest? body, CmsManagement m, CancellationToken ct) => m.PublishContentAsync(id, body, ct))
            .WithName("PublishContent").WithSummary("Publish the draft").WithDescription("Fails with success=false and the validation errors when mandatory properties are empty. For content that varies by culture, the optional body names the languages to publish (all named languages when omitted).");
        content.MapPost("/content/{id:guid}/unpublish", (Guid id, UnpublishContentRequest? body, CmsManagement m, CancellationToken ct) => m.UnpublishContentAsync(id, body, ct))
            .WithName("UnpublishContent").WithSummary("Take the item off the site (the draft is kept)").WithDescription("For content that varies by culture, the optional body names one language to take offline; without it every language is unpublished.");
        content.MapPost("/content/{id:guid}/move", (Guid id, MoveContentRequest body, CmsManagement m, CancellationToken ct) => m.MoveContentAsync(id, body, ct))
            .WithName("MoveContent").WithSummary("Move up or down among its siblings");
        content.MapDelete("/content/{id:guid}", async (Guid id, CmsManagement m, CancellationToken ct) => { await m.DeleteContentAsync(id, ct); return Results.NoContent(); })
            .WithName("DeleteContent").WithSummary("Delete the item and everything below it");

        // ---- Analytics ------------------------------------------------------------------------------------------
        var analytics = api.MapGroup("").WithTags("Analytics");
        analytics.MapGet("/analytics", (string? period, string? from, string? to, CmsManagement m, CancellationToken ct) => m.GetAnalyticsReportAsync(period, from, to, ct))
            .WithName("GetAnalyticsReport").WithSummary("Visitor report for a period")
            .WithDescription("Page views, visitors, sessions, bounce rate and session length (with the previous period for comparison), visitors right now, the chart series and the top pages, referrers, countries, browsers, operating systems, devices and languages. period: today, yesterday, 7d, 30d (default), 90d, 12m; or from/to as yyyy-MM-dd. Bots are excluded.");
        analytics.MapGet("/analytics/pages", (string? period, string? from, string? to, int? take, string? search, CmsManagement m, CancellationToken ct) => m.GetAnalyticsPagesAsync(period, from, to, take, search, ct))
            .WithName("GetAnalyticsPages").WithSummary("Page views and visitors per page");
        analytics.MapGet("/analytics/views", (string? period, string? from, string? to, string? path, string? visitorId, string? sessionId, string? ip, string? country, string? search, bool? notFound, bool? bots, int? skip, int? take, CmsManagement m, CancellationToken ct) =>
                m.GetAnalyticsViewsAsync(period, from, to, path, visitorId, sessionId, ip, country, search, notFound, bots, skip, take, ct))
            .WithName("GetAnalyticsViews").WithSummary("The page view log, newest first")
            .WithDescription("Every recorded page view with its visitor, session, IP address, browser, device and location. Filter by path (append * for a prefix), visitorId, sessionId, ip, country (ISO code), free-text search, notFound=true; bots=true includes crawlers. Pages with skip/take (max 500).");
        analytics.MapGet("/analytics/settings", (CmsManagement m) => m.GetAnalyticsSettings())
            .WithName("GetAnalyticsSettings").WithSummary("Analytics settings (administrators)");
        analytics.MapPut("/analytics/settings", (UpdateAnalyticsSettingsRequest body, CmsManagement m, CancellationToken ct) => m.UpdateAnalyticsSettingsAsync(body, ct))
            .WithName("UpdateAnalyticsSettings").WithSummary("Change analytics settings (administrators)").WithDescription("Only the fields sent are changed: enabled, storeIpAddress, anonymizeIp, trackSignedInUsers, trackBots, retentionDays, geoLookupEnabled, geoLookupUrl, trustProxyHeaders, excludedPaths.");
        analytics.MapGet("/analytics/data", (CmsManagement m, CancellationToken ct) => m.GetAnalyticsDataInfoAsync(ct))
            .WithName("GetAnalyticsDataInfo").WithSummary("How much analytics data is stored and what the worker is doing (administrators)");
        analytics.MapDelete("/analytics/views", async (int? olderThanDays, CmsManagement m, CancellationToken ct) => Results.Ok(new { deleted = await m.DeleteAnalyticsViewsAsync(olderThanDays, ct) }))
            .WithName("DeleteAnalyticsViews").WithSummary("Delete page views (administrators)").WithDescription("olderThanDays keeps the recent ones; without it every page view is deleted.");

        var published = api.MapGroup("").WithTags("Published content");
        published.MapGet("/published/route", async (string path, bool? preview, string? culture, CmsManagement m, CancellationToken ct) =>
                await m.GetPublishedByRouteAsync(path, preview == true, culture, ct) is { } dto ? Results.Ok(dto) : Results.NotFound(new ProblemDto(404, "Not found", $"No published content at '{path}'.", null)))
            .WithName("GetPublishedByRoute").WithSummary("Resolve a site URL (e.g. /blog/my-post or /de/blog/mein-beitrag) to published content").WithDescription("A language prefix in the path wins; otherwise the optional culture (ISO code) applies, else the default language.");
        published.MapGet("/published/{id:guid}", (Guid id, bool? preview, string? culture, CmsManagement m, CancellationToken ct) => m.GetPublishedAsync(id, preview == true, culture, ct))
            .WithName("GetPublished").WithSummary("Published content by id (preview=true shows the draft; culture picks the language)");
        published.MapGet("/published/{id:guid}/children", (Guid id, bool? preview, string? culture, CmsManagement m, CancellationToken ct) => m.GetPublishedChildrenAsync(id, preview == true, culture, ct))
            .WithName("GetPublishedChildren").WithSummary("Published children of an item");
        published.MapGet("/published/by-type/{alias}", (string alias, bool? preview, string? culture, CmsManagement m, CancellationToken ct) => m.GetPublishedByTypeAsync(alias, preview == true, culture, ct))
            .WithName("GetPublishedByType").WithSummary("All published items of a document type");

        // ---- Media ---------------------------------------------------------------------------------------------------
        var media = api.MapGroup("").WithTags("Media");
        media.MapGet("/media", (Guid? folderId, CmsManagement m, CancellationToken ct) => m.GetMediaChildrenAsync(folderId, ct))
            .WithName("ListMedia").WithSummary("Items in the library root, or in folderId");
        media.MapGet("/media/{id:guid}", (Guid id, CmsManagement m, CancellationToken ct) => m.GetMediaAsync(id, ct)).WithName("GetMedia").WithSummary("Get a media item");
        media.MapPost("/media/folders", async (CreateMediaFolderRequest body, CmsManagement m, CancellationToken ct) =>
            {
                var dto = await m.CreateMediaFolderAsync(body, ct);
                return Results.Created($"{basePath}/media/{dto.Id}", dto);
            }).WithName("CreateMediaFolder").WithSummary("Create a folder");
        media.MapPost("/media/upload", async (UploadMediaRequest body, CmsManagement m, CancellationToken ct) =>
            {
                var dto = await m.UploadMediaAsync(body, ct);
                return Results.Created($"{basePath}/media/{dto.Id}", dto);
            }).WithName("UploadMediaJson").WithSummary("Upload a file as base64 JSON");
        media.MapPost("/media/upload-file", async (HttpRequest request, CmsManagement m, CancellationToken ct) =>
            {
                if (!request.HasFormContentType) throw CmsApiException.BadRequest("Send multipart/form-data with a 'file' part and an optional 'folderId' field.");
                var form = await request.ReadFormAsync(ct);
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault() ?? throw CmsApiException.BadRequest("The 'file' part is missing.");
                Guid? folderId = Guid.TryParse(form["folderId"], out var g) ? g : null;
                await using var stream = file.OpenReadStream();
                var dto = await m.UploadMediaAsync(folderId, file.FileName, file.ContentType, stream, ct);
                return Results.Created($"{basePath}/media/{dto.Id}", dto);
            })
            .WithName("UploadMediaFile").WithSummary("Upload a file as multipart/form-data (field 'file', optional 'folderId')")
            .Accepts<IFormFile>("multipart/form-data")
            .DisableAntiforgery();
        media.MapPut("/media/{id:guid}", (Guid id, RenameMediaRequest body, CmsManagement m, CancellationToken ct) => m.RenameMediaAsync(id, body, ct)).WithName("RenameMedia").WithSummary("Rename a file or folder");
        media.MapDelete("/media/{id:guid}", async (Guid id, CmsManagement m, CancellationToken ct) => { await m.DeleteMediaAsync(id, ct); return Results.NoContent(); })
            .WithName("DeleteMedia").WithSummary("Delete a file, or a folder with everything in it");

        return endpoints;
    }

    /// <summary>Turns <see cref="CmsApiException"/> and unexpected errors into JSON problem responses.</summary>
    private sealed class CmsApiExceptionFilter(ILogger<CmsApiExceptionFilter> logger) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            try
            {
                return await next(context);
            }
            catch (CmsApiException ex)
            {
                return Results.Json(new ProblemDto(ex.StatusCode, Title(ex.StatusCode), ex.Message, ex.Details), ApiJson.Options, statusCode: ex.StatusCode);
            }
            catch (BadHttpRequestException ex)
            {
                return Results.Json(new ProblemDto(400, "Bad request", ex.Message, null), ApiJson.Options, statusCode: 400);
            }
            catch (DynCmsNotConfiguredException)
            {
                return Results.Json(new ProblemDto(503, "Not configured", "DynCMS has not been set up yet. Open /setup in a browser.", null), ApiJson.Options, statusCode: 503);
            }
            catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in {Method} {Path}", context.HttpContext.Request.Method, context.HttpContext.Request.Path);
                return Results.Json(new ProblemDto(500, "Server error", ex.Message, null), ApiJson.Options, statusCode: 500);
            }
        }

        private static string Title(int status) => status switch
        {
            400 => "Bad request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not found",
            409 => "Conflict",
            _ => "Error"
        };
    }

    /// <summary>Adds the API description and the bearer security scheme to the generated OpenAPI document.</summary>
    internal static void ConfigureOpenApi(OpenApiOptions options, DynCmsApiOptions api)
    {
        var basePath = "/" + api.BasePath.Trim('/');
        options.ShouldInclude = description => description.RelativePath?.StartsWith(basePath.TrimStart('/'), StringComparison.OrdinalIgnoreCase) == true;
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "DynCMS management API",
                Version = "v1",
                Description =
                    "Manage a DynCMS site programmatically: languages, the dictionary, document types, templates, content, media, users and API keys. " +
                    "Authenticate with `Authorization: Bearer <api key>` (create keys in the back office under System → API & AI agents). " +
                    $"AI agents can also use the MCP server at `{api.McpPath}` with the same key. Start with `GET {basePath}/` to see who you are and what you may do."
            };
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                Description = "A DynCMS API key (starts with dcms_)."
            };
            document.Security ??= [];
            document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiKey", document)] = [] });
            return Task.CompletedTask;
        });
    }
}
