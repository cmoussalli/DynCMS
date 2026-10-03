namespace DynCMS.Core.Security;

/// <summary>A signed-in back-office user as resolved from an identity framework token.</summary>
public sealed record CmsPrincipal(
    string UserId,
    string UserName,
    string? FullName,
    string? Email,
    IReadOnlyList<string> RoleIds,
    DateTime TokenExpiresAt)
{
    public bool IsInRole(string roleId) => RoleIds.Contains(roleId, StringComparer.OrdinalIgnoreCase);
    public bool IsAdmin => IsInRole(CmsRoles.Admin);
}

public sealed record CmsSignInResult(bool Success, string? Token, DateTime? ExpiresAt, string? Error)
{
    public static CmsSignInResult Failed(string error) => new(false, null, null, error);
}

public sealed record CmsUserSummary(
    string Id,
    string UserName,
    string? FullName,
    string? Email,
    bool IsActive,
    bool IsLocked,
    IReadOnlyList<string> RoleIds);

/// <summary>
/// Thin, testable wrapper around the static CMouss.IdentityFramework services used by DynCMS.
/// User, role, permission and token management screens come from CMouss.IdentityFramework.BlazorUI.
/// </summary>
public interface ICmsIdentity
{
    /// <summary>How long a freshly issued token stays valid.</summary>
    TimeSpan TokenLifetime { get; }

    /// <summary>Validates a username/password pair and issues a token on success.</summary>
    CmsSignInResult SignIn(string userName, string password, string? ipAddress = null);

    /// <summary>Resolves a token to the user it belongs to, or null when it is missing, invalid or expired.</summary>
    CmsPrincipal? ValidateToken(string? token);

    int CountUsers();
    IReadOnlyList<CmsUserSummary> GetUsers();
    CmsUserSummary? FindUserByName(string userName);
    CmsUserSummary? GetUser(string userId);
    IReadOnlyList<string> GetRoles();

    /// <summary>Creates a user and grants the given roles. Returns the new user id.</summary>
    string CreateUser(string userName, string password, string fullName, string email, params string[] roleIds);

    /// <summary>Creates a role when it does not exist yet.</summary>
    void EnsureRole(string roleId, string title);

    /// <summary>Grants an entity/action permission to a role when it is not granted yet.</summary>
    void EnsurePermission(string roleId, string entityId, string actionId);

    bool UserHasRole(string userId, string roleId);
    bool UserHasPermission(string userId, string entityId, string actionId);
}
