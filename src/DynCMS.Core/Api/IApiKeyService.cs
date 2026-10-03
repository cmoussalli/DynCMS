using DynCMS.Core.Security;

namespace DynCMS.Core.Api;

/// <summary>The result of creating a key: the only time the secret is available.</summary>
public sealed record ApiKeyCreated(ApiKey Key, string Secret);

/// <summary>What an API key resolves to at request time.</summary>
public sealed record ApiKeyPrincipal(ApiKey Key, CmsUserSummary User);

/// <summary>Issues, lists, revokes and validates API keys (see <see cref="ApiKey"/>).</summary>
public interface IApiKeyService
{
    /// <summary>Secrets start with this, so the authentication handler can tell them from identity framework tokens.</summary>
    string SecretPrefix { get; }

    Task<IReadOnlyList<ApiKey>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ApiKey>> GetForUserAsync(string userId, CancellationToken ct = default);
    Task<ApiKey?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Creates a key for <paramref name="userId"/>. <paramref name="scopes"/> must be known scopes; the caller is
    /// responsible for checking they do not exceed what the owner may do. Throws <see cref="InvalidOperationException"/>
    /// with a user-facing message.
    /// </summary>
    Task<ApiKeyCreated> CreateAsync(string userId, string userName, string name, IEnumerable<string> scopes, DateTime? expiresAt, CancellationToken ct = default);

    Task<bool> RevokeAsync(Guid id, CancellationToken ct = default);

    /// <summary>Resolves a secret to its key and owner, or null when unknown, revoked, expired, or the owner is inactive.</summary>
    Task<ApiKeyPrincipal?> ValidateAsync(string secret, CancellationToken ct = default);
}
