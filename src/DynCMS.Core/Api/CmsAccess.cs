using System.Security.Claims;
using DynCMS.Core.Security;
using Microsoft.AspNetCore.Http;

namespace DynCMS.Core.Api;

/// <summary>
/// Thrown by the management layer when a request cannot be served: the status code says why (400 invalid input,
/// 401 not signed in, 403 not allowed, 404 not found, 409 conflict). The message is written for the caller to read
/// and is returned as-is by the REST API and the MCP tools.
/// </summary>
public sealed class CmsApiException(int statusCode, string message, object? details = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public object? Details { get; } = details;

    public static CmsApiException BadRequest(string message, object? details = null) => new(StatusCodes.Status400BadRequest, message, details);
    public static CmsApiException Unauthorized(string message = "Authentication is required.") => new(StatusCodes.Status401Unauthorized, message);
    public static CmsApiException Forbidden(string message) => new(StatusCodes.Status403Forbidden, message);
    public static CmsApiException NotFound(string what) => new(StatusCodes.Status404NotFound, $"{what} was not found.");
    public static CmsApiException Conflict(string message) => new(StatusCodes.Status409Conflict, message);
}

/// <summary>Claim types DynCMS adds to the principal (in addition to the standard name, id, email and role claims).</summary>
public static class CmsClaimTypes
{
    /// <summary>How the caller authenticated: <c>session</c> (identity framework token) or <c>api-key</c>.</summary>
    public const string AuthMethod = "dyncms:auth";
    /// <summary>One claim per granted API scope; absent for session callers.</summary>
    public const string Scope = "dyncms:scope";
    /// <summary>Id of the API key used, when any.</summary>
    public const string ApiKeyId = "dyncms:api-key";

    public const string SessionMethod = "session";
    public const string ApiKeyMethod = "api-key";
}

/// <summary>Who is calling the management layer and what they may do.</summary>
public sealed record CmsCaller(
    string UserId,
    string UserName,
    IReadOnlyList<string> Roles,
    string AuthMethod,
    IReadOnlyList<string> Scopes,
    Guid? ApiKeyId)
{
    public bool IsAdmin => Roles.Contains(CmsRoles.Admin, StringComparer.OrdinalIgnoreCase);
    public bool IsApiKey => AuthMethod == CmsClaimTypes.ApiKeyMethod;

    public static CmsCaller? From(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return null;
        var method = principal.FindFirstValue(CmsClaimTypes.AuthMethod) ?? CmsClaimTypes.SessionMethod;
        var keyId = Guid.TryParse(principal.FindFirstValue(CmsClaimTypes.ApiKeyId), out var g) ? g : (Guid?)null;
        return new CmsCaller(
            userId,
            principal.Identity.Name ?? userId,
            principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList(),
            method,
            principal.FindAll(CmsClaimTypes.Scope).Select(c => c.Value).ToList(),
            keyId);
    }
}

/// <summary>
/// Authorisation for the management API and the MCP tools. Every check has two layers: an API key must carry the
/// matching scope, and then the user behind the call (session or key owner) must be an administrator or hold the
/// identity framework permission for the entity and action.
/// </summary>
public interface ICmsAccess
{
    /// <summary>The caller, or null when the request is anonymous.</summary>
    CmsCaller? Caller { get; }

    /// <summary>The caller; throws 401 when anonymous.</summary>
    CmsCaller Require();

    /// <summary>Throws 401/403 unless the caller may perform <paramref name="action"/> on <paramref name="entity"/>.</summary>
    CmsCaller Require(string entity, string action);

    /// <summary>Throws 401/403 unless the caller is an administrator (and, for API keys, holds <paramref name="scope"/>).</summary>
    CmsCaller RequireAdmin(string scope);

    /// <summary>True when the caller may perform <paramref name="action"/> on <paramref name="entity"/>.</summary>
    bool Can(string entity, string action);

    /// <summary>The scopes the caller could grant to a new API key (never more than they can do themselves).</summary>
    IReadOnlyList<ApiScopeDefinition> GrantableScopes();
}

/// <summary>
/// The authorisation rules, independent of where the principal comes from, so the Blazor back office (which has
/// no reliable HttpContext inside a circuit) can apply the same rules as the API.
/// </summary>
public static class CmsAuthorization
{
    public static bool Can(ICmsIdentity identity, CmsCaller? caller, string entity, string action)
    {
        if (caller is null) return false;
        if (caller.IsApiKey && !ApiScopes.Covers(caller.Scopes, ApiScopes.For(entity, action))) return false;
        return caller.IsAdmin || identity.UserHasPermission(caller.UserId, entity, action);
    }

    /// <summary>The scopes <paramref name="caller"/> could put on a new API key: never more than they can do themselves.</summary>
    public static IReadOnlyList<ApiScopeDefinition> GrantableScopes(ICmsIdentity identity, CmsCaller? caller)
    {
        if (caller is null) return [];
        if (caller.IsAdmin) return ApiScopes.Definitions;
        return ApiScopes.Definitions.Where(d => !d.AdminOnly && Grantable(identity, caller, d.Id)).ToList();
    }

    private static bool Grantable(ICmsIdentity identity, CmsCaller caller, string scope)
    {
        // A scope is grantable when at least one entity/action it gates is permitted to the user.
        foreach (var (entity, _) in CmsPermissions.Entities.All)
        foreach (var (action, _) in CmsPermissions.Actions.All)
        {
            if (ApiScopes.For(entity, action) == scope && identity.UserHasPermission(caller.UserId, entity, action))
                return true;
        }
        return false;
    }

    public static string Describe(string entity) => entity switch
    {
        CmsPermissions.Entities.Content => "content",
        CmsPermissions.Entities.Media => "media",
        CmsPermissions.Entities.DocumentTypes => "document types",
        CmsPermissions.Entities.Templates => "templates",
        CmsPermissions.Entities.Dictionary => "the dictionary",
        _ => entity.ToLowerInvariant()
    };
}

public sealed class CmsAccess(IHttpContextAccessor http, ICmsIdentity identity) : ICmsAccess
{
    private CmsCaller? _caller;
    private bool _resolved;

    public CmsCaller? Caller
    {
        get
        {
            if (!_resolved)
            {
                _caller = CmsCaller.From(http.HttpContext?.User);
                _resolved = true;
            }
            return _caller;
        }
    }

    public CmsCaller Require() => Caller ?? throw CmsApiException.Unauthorized();

    public CmsCaller Require(string entity, string action)
    {
        var caller = Require();
        if (caller.IsApiKey)
        {
            var scope = ApiScopes.For(entity, action);
            if (!ApiScopes.Covers(caller.Scopes, scope))
                throw CmsApiException.Forbidden($"This API key does not have the '{scope}' scope.");
        }
        if (caller.IsAdmin || identity.UserHasPermission(caller.UserId, entity, action)) return caller;
        throw CmsApiException.Forbidden($"User '{caller.UserName}' may not {action.ToLowerInvariant()} {CmsAuthorization.Describe(entity)}.");
    }

    public CmsCaller RequireAdmin(string scope)
    {
        var caller = Require();
        if (caller.IsApiKey && !ApiScopes.Covers(caller.Scopes, scope))
            throw CmsApiException.Forbidden($"This API key does not have the '{scope}' scope.");
        if (!caller.IsAdmin) throw CmsApiException.Forbidden("Only administrators may do this.");
        return caller;
    }

    public bool Can(string entity, string action) => CmsAuthorization.Can(identity, Caller, entity, action);

    public IReadOnlyList<ApiScopeDefinition> GrantableScopes() => CmsAuthorization.GrantableScopes(identity, Caller);
}
