namespace DynCMS.Core.Api;

/// <summary>
/// A long-lived credential a back-office user hands to an integration or an AI agent so it can use the management
/// API and the MCP server on that user's behalf. Only a SHA-256 hash of the secret is stored; the secret itself is
/// shown once, when the key is created. A key never grants more than its owner can do: every call is checked
/// against the key's <see cref="Scopes"/> and then against the owner's roles and permissions.
/// </summary>
public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What the key is for ("Claude Code on my laptop", "Nightly import job").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The first characters of the secret, so a key can be recognised in lists without revealing it.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>SHA-256 of the secret, hex encoded.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>The identity framework user the key acts as.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>User name of the owner at creation time (display only; the user id is authoritative).</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Granted scopes (see <see cref="ApiScopes"/>); <c>*</c> means every scope the owner may use.</summary>
    public List<string> Scopes { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public bool IsRevoked => RevokedAt is not null;
    public bool IsExpired => ExpiresAt is not null && ExpiresAt <= DateTime.UtcNow;
    public bool IsActive => !IsRevoked && !IsExpired;
}
