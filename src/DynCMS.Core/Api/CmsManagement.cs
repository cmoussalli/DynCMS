using System.Reflection;
using DynCMS.Core.Analytics;
using DynCMS.Core.Data;
using DynCMS.Plugins.Models;
using DynCMS.Core.Plugins;
using DynCMS.Core.PropertyEditors;
using DynCMS.Core.Security;
using DynCMS.Core.Services;
using DynCMS.Core.Templates;
using Microsoft.Extensions.Options;

namespace DynCMS.Core.Api;

/// <summary>
/// The application layer behind the management REST API and the MCP tools: one set of operations, expressed in
/// DTOs, with authorisation and input validation built in. Both front ends are thin adapters over this class, so a
/// capability added here is available to HTTP clients and AI agents alike.
/// </summary>
public sealed partial class CmsManagement(
    ICmsAccess access,
    ICmsIdentity identity,
    IApiKeyService apiKeys,
    IContentService content,
    IContentTypeService contentTypes,
    ILanguageService languages,
    IDictionaryService dictionary,
    IPublishedContentQuery published,
    IMediaService media,
    ITemplateService templates,
    ITemplateRegistry templateRegistry,
    IPropertyEditorRegistry editors,
    ILiquidTemplateEngine liquid,
    ITemplatePreviewService previews,
    IDatabaseMaintenanceService maintenance,
    IAnalyticsService analytics,
    IAnalyticsSettingsService analyticsSettings,
    IDynCmsRuntime runtime,
    IPluginManager plugins,
    ISystemVersionService versions,
    IOptions<DynCmsOptions> options)
{
    private static string Version => CmsVersion.Application;

    private DynCmsApiOptions Api => options.Value.Api;

    #region System

    public async Task<ApiInfoDto> GetInfoAsync(CancellationToken ct)
    {
        var caller = access.Caller;
        CallerDto? callerDto = null;
        ApiCountsDto? counts = null;

        if (caller is not null)
        {
            var permissions = new List<string>();
            foreach (var (entity, _) in CmsPermissions.Entities.All)
            foreach (var (action, _) in CmsPermissions.Actions.All)
            {
                if (access.Can(entity, action)) permissions.Add($"{entity}:{action}");
            }
            callerDto = new CallerDto(caller.UserId, caller.UserName, caller.Roles, caller.AuthMethod, caller.Scopes, caller.ApiKeyId, permissions);

            if (runtime.IsReady)
            {
                counts = new ApiCountsDto(
                    access.Can(CmsPermissions.Entities.Content, CmsPermissions.Actions.Read) ? await content.CountAsync(null, ct) : -1,
                    access.Can(CmsPermissions.Entities.Content, CmsPermissions.Actions.Read) ? await content.CountAsync(true, ct) : -1,
                    access.Can(CmsPermissions.Entities.Media, CmsPermissions.Actions.Read) ? await media.CountAsync(ct) : -1,
                    access.Can(CmsPermissions.Entities.DocumentTypes, CmsPermissions.Actions.Read) ? (await contentTypes.GetAllAsync(ct)).Count : -1,
                    access.Can(CmsPermissions.Entities.Templates, CmsPermissions.Actions.Read) ? templateRegistry.All.Count : -1,
                    access.Can(CmsPermissions.Entities.Content, CmsPermissions.Actions.Read) ? (await languages.GetAllAsync(ct)).Count : -1,
                    access.Can(CmsPermissions.Entities.Dictionary, CmsPermissions.Actions.Read) ? await dictionary.CountAsync(ct) : -1);
            }
        }

        var basePath = Api.BasePath.TrimEnd('/');
        return new ApiInfoDto(
            "DynCMS",
            Version,
            runtime.IsReady,
            new ApiEndpointsDto(basePath, $"{basePath}/openapi.json", Api.McpPath),
            callerDto,
            counts,
            [
                "Model first: create document types (with properties) before content, and templates before assigning them.",
                "Property values are strings. Rich text is HTML, toggles are \"true\"/\"false\", dates are yyyy-MM-dd, media/content pickers hold a GUID, tags are a JSON array of strings.",
                "Content has a draft and a published snapshot; save writes the draft, publish copies it to the live site.",
                "Liquid templates: output is HTML-encoded, so rich text needs {{ content.bodyText | raw }}; partials are rendered with {% render 'alias' %}.",
                "Languages: GET languages lists them. A document type with variesByCulture=true has a name, URL segment, publish state and its varying properties per language; pass culture when creating/updating such content and publish per language. Non-default languages are served under /{iso-code}/.",
                "Dictionary: GET dictionary lists translated labels by key with one text per language; templates print them with {{ 'key' | dictionary }} (Liquid) or T(\"key\") (Blazor). Missing texts fall back along the language's fallback chain, then to the default language.",
                $"Machine-readable reference: GET {basePath}/openapi.json. MCP server (Streamable HTTP): {Api.McpPath}."
            ]);
    }

    public async Task<SystemVersionDto> GetSystemVersionAsync(CancellationToken ct)
    {
        access.RequireAdmin(ApiScopes.SystemRead);
        var v = await versions.GetAsync(ct);
        var state = v.State.ToString();
        return new SystemVersionDto(v.ApplicationVersion, v.ExpectedSchemaVersion, v.DatabaseSchemaVersion, v.DatabaseApplicationVersion,
            char.ToLowerInvariant(state[0]) + state[1..], v.Runtime, v.OperatingSystem);
    }

    public async Task<DatabaseInfoDto> GetDatabaseInfoAsync(string? role, CancellationToken ct)
    {
        access.RequireAdmin(ApiScopes.SystemRead);
        var info = await maintenance.GetInfoAsync(ParseRole(role), ct);
        return new DatabaseInfoDto(info.IsReady, info.Provider.ToString(), info.Description, info.SqliteFile, info.SqliteFileSize,
            info.Server, info.Database, info.Tables.Select(t => new TableInfoDto(t.Name, t.Rows)).ToList());
    }

    public async Task<IReadOnlyList<BackupDto>> ListBackupsAsync(string? role, CancellationToken ct)
    {
        access.RequireAdmin(ApiScopes.SystemRead);
        return (await maintenance.ListBackupsAsync(ParseRole(role), ct)).Select(MapBackup).ToList();
    }

    public async Task<BackupDto> CreateBackupAsync(CreateBackupRequest? request, string? role, CancellationToken ct)
    {
        access.RequireAdmin(ApiScopes.SystemManage);
        return MapBackup(await Guard(() => maintenance.CreateBackupAsync(request?.Note, ParseRole(role), ct)));
    }

    /// <summary><c>primary</c> (default) or <c>analytics</c>.</summary>
    private static DatabaseRole ParseRole(string? role) => (role ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" or "primary" or "content" => DatabaseRole.Primary,
        "analytics" => DatabaseRole.Analytics,
        _ => throw CmsApiException.BadRequest("role must be 'primary' or 'analytics'.")
    };

    private static BackupDto MapBackup(DatabaseBackupInfo b) =>
        new(b.FileName, b.Size, b.CreatedUtc, b.Provider.ToString(), b.Database, b.Note, b.IsLocalFile);

    #endregion

    #region Users

    public IReadOnlyList<UserDto> GetUsers()
    {
        access.RequireAdmin(ApiScopes.UsersRead);
        return identity.GetUsers().Select(MapUser).ToList();
    }

    public IReadOnlyList<string> GetRoles()
    {
        access.RequireAdmin(ApiScopes.UsersRead);
        return identity.GetRoles();
    }

    public UserDto CreateUser(CreateUserRequest request)
    {
        access.RequireAdmin(ApiScopes.UsersWrite);
        if (string.IsNullOrWhiteSpace(request.UserName)) throw CmsApiException.BadRequest("userName is required.");
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6) throw CmsApiException.BadRequest("password must be at least 6 characters.");
        if (identity.FindUserByName(request.UserName.Trim()) is not null) throw CmsApiException.Conflict($"A user named '{request.UserName}' already exists.");

        var roles = (request.Roles ?? [CmsRoles.Editor]).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToArray();
        var known = identity.GetRoles();
        var unknown = roles.Where(r => !known.Contains(r, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0) throw CmsApiException.BadRequest($"Unknown role(s): {string.Join(", ", unknown)}. Roles are case sensitive.", new { roles = known });

        var id = Guard(() => identity.CreateUser(request.UserName.Trim(), request.Password, request.FullName?.Trim() ?? request.UserName.Trim(), request.Email?.Trim() ?? string.Empty, roles));
        return MapUser(identity.GetUser(id) ?? throw new CmsApiException(500, "The user was created but could not be read back."));
    }

    private static UserDto MapUser(CmsUserSummary u) => new(u.Id, u.UserName, u.FullName, u.Email, u.IsActive, u.IsLocked, u.RoleIds);

    #endregion

    #region API keys

    public IReadOnlyList<ApiScopeDefinition> GetScopes() => ApiScopes.Definitions;

    /// <summary>The scopes the current caller may put on a new key.</summary>
    public IReadOnlyList<ApiScopeDefinition> GetGrantableScopes()
    {
        access.Require();
        return access.GrantableScopes();
    }

    public async Task<IReadOnlyList<ApiKeyDto>> GetApiKeysAsync(bool all, CancellationToken ct)
    {
        var caller = RequireKeyManagement();
        var keys = all && caller.IsAdmin ? await apiKeys.GetAllAsync(ct) : await apiKeys.GetForUserAsync(caller.UserId, ct);
        return keys.Select(MapKey).ToList();
    }

    public async Task<ApiKeyCreatedDto> CreateApiKeyAsync(CreateApiKeyRequest request, CancellationToken ct)
    {
        var caller = RequireKeyManagement();
        if (request.Scopes is null || request.Scopes.Count == 0) throw CmsApiException.BadRequest("scopes is required. Use [\"*\"] for every scope the owner may use.");

        var ownerId = caller.UserId;
        var ownerName = caller.UserName;
        if (!string.IsNullOrWhiteSpace(request.ForUser) && !string.Equals(request.ForUser, caller.UserId, StringComparison.Ordinal) && !string.Equals(request.ForUser, caller.UserName, StringComparison.Ordinal))
        {
            if (!caller.IsAdmin) throw CmsApiException.Forbidden("Only administrators may create keys for other users.");
            var owner = identity.GetUser(request.ForUser.Trim()) ?? identity.FindUserByName(request.ForUser.Trim())
                ?? throw CmsApiException.NotFound($"User '{request.ForUser}'");
            ownerId = owner.Id;
            ownerName = owner.UserName;
        }

        // A key cannot be broader than the account that will use it.
        var grantable = access.GrantableScopes().Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tooBroad = request.Scopes.Where(s => s != ApiScopes.All && !grantable.Contains(s) && ApiScopes.IsKnown(s)).ToList();
        if (tooBroad.Count > 0)
            throw CmsApiException.Forbidden($"You cannot grant scope(s) you do not have yourself: {string.Join(", ", tooBroad)}.");

        var created = await Guard(() => apiKeys.CreateAsync(ownerId, ownerName, request.Name, request.Scopes, request.ExpiresAt, ct));
        return new ApiKeyCreatedDto(MapKey(created.Key), created.Secret);
    }

    public async Task RevokeApiKeyAsync(Guid id, CancellationToken ct)
    {
        var caller = RequireKeyManagement();
        var key = await apiKeys.GetAsync(id, ct) ?? throw CmsApiException.NotFound("API key");
        if (!caller.IsAdmin && key.UserId != caller.UserId) throw CmsApiException.Forbidden("You can only revoke your own API keys.");
        await apiKeys.RevokeAsync(id, ct);
    }

    /// <summary>Session users manage their own keys; an API key may only manage keys when it holds the admin scope.</summary>
    private CmsCaller RequireKeyManagement()
    {
        var caller = access.Require();
        if (caller.IsApiKey) access.RequireAdmin(ApiScopes.ApiKeysManage);
        return caller;
    }

    private static ApiKeyDto MapKey(ApiKey k) =>
        new(k.Id, k.Name, k.Prefix, k.UserId, k.UserName, k.Scopes, k.CreatedAt, k.ExpiresAt, k.LastUsedAt, k.RevokedAt, k.IsActive);

    #endregion

    #region Helpers

    /// <summary>Turns the services' <see cref="InvalidOperationException"/> (a user-facing message) into a 400/409.</summary>
    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (InvalidOperationException ex) { throw Translate(ex); }
    }

    private static async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException ex) { throw Translate(ex); }
    }

    private static T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (InvalidOperationException ex) { throw Translate(ex); }
    }

    private static CmsApiException Translate(InvalidOperationException ex)
    {
        var m = ex.Message;
        if (m.Contains("not found", StringComparison.OrdinalIgnoreCase)) return new CmsApiException(404, m);
        if (m.Contains("already", StringComparison.OrdinalIgnoreCase) || m.Contains("still", StringComparison.OrdinalIgnoreCase) || m.Contains("is used by", StringComparison.OrdinalIgnoreCase))
            return CmsApiException.Conflict(m);
        return CmsApiException.BadRequest(m);
    }

    private static Guid ParseId(string value, string what)
    {
        if (Guid.TryParse(value, out var id)) return id;
        throw CmsApiException.BadRequest($"'{value}' is not a valid {what} id.");
    }

    #endregion
}
