using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DynCMS.Core.Data;
using DynCMS.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DynCMS.Core.Api;

/// <summary>
/// Stores API keys in the <c>ApiKeys</c> table (hash only) and validates bearer secrets. Validation results are
/// cached briefly so an agent making many calls does not hit the database for every one; revoking a key clears the cache.
/// </summary>
public sealed class ApiKeyService(
    IDbContextFactory<DynCmsDbContext> factory,
    ICmsIdentity identity,
    ILogger<ApiKeyService> logger) : IApiKeyService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (ApiKeyPrincipal? Principal, DateTime CachedAt)> _cache = new(StringComparer.Ordinal);

    public string SecretPrefix => "dcms_";

    public async Task<IReadOnlyList<ApiKey>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ApiKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ApiKey>> GetForUserAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ApiKeys.AsNoTracking().Where(k => k.UserId == userId).OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
    }

    public async Task<ApiKey?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, ct);
    }

    public async Task<ApiKeyCreated> CreateAsync(string userId, string userName, string name, IEnumerable<string> scopes, DateTime? expiresAt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new InvalidOperationException("An API key needs an owner.");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Give the key a name so you can recognise it later.");

        var scopeList = scopes
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (scopeList.Count == 0) throw new InvalidOperationException("Pick at least one scope.");
        var unknown = scopeList.Where(s => !ApiScopes.IsKnown(s)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException($"Unknown scope(s): {string.Join(", ", unknown)}. Known scopes: {string.Join(", ", ApiScopes.Definitions.Select(d => d.Id).Append(ApiScopes.All))}.");
        if (scopeList.Contains(ApiScopes.All)) scopeList = [ApiScopes.All];
        if (expiresAt is not null && expiresAt <= DateTime.UtcNow) throw new InvalidOperationException("The expiry date is in the past.");

        var secret = SecretPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var key = new ApiKey
        {
            Name = name.Trim(),
            Prefix = secret[..(SecretPrefix.Length + 8)],
            Hash = Hash(secret),
            UserId = userId,
            UserName = userName,
            Scopes = scopeList,
            ExpiresAt = expiresAt?.ToUniversalTime()
        };

        await using var db = await factory.CreateDbContextAsync(ct);
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("API key {Prefix}… ({Name}) created for {User} with scopes {Scopes}", key.Prefix, key.Name, userName, string.Join(",", scopeList));
        return new ApiKeyCreated(key, secret);
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null) return false;
        if (key.RevokedAt is null)
        {
            key.RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("API key {Prefix}… ({Name}) revoked", key.Prefix, key.Name);
        }
        _cache.TryRemove(key.Hash, out _);
        return true;
    }

    public async Task<ApiKeyPrincipal?> ValidateAsync(string secret, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(secret) || !secret.StartsWith(SecretPrefix, StringComparison.Ordinal)) return null;
        var hash = Hash(secret);

        if (_cache.TryGetValue(hash, out var cached) && DateTime.UtcNow - cached.CachedAt < CacheLifetime)
        {
            // Expiry can pass while the entry is cached; revocation removes the entry.
            return cached.Principal is { } p && !p.Key.IsActive ? null : cached.Principal;
        }

        ApiKeyPrincipal? principal = null;
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Hash == hash, ct);
            if (key is not null && key.IsActive)
            {
                var user = identity.GetUser(key.UserId);
                if (user is { IsActive: true, IsLocked: false })
                {
                    principal = new ApiKeyPrincipal(key, user);
                    if (key.LastUsedAt is null || DateTime.UtcNow - key.LastUsedAt > LastUsedGranularity)
                    {
                        key.LastUsedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);
                    }
                }
                else
                {
                    logger.LogWarning("API key {Prefix}… rejected: owner {User} is missing, inactive or locked", key.Prefix, key.UserName);
                }
            }
        }
        catch (DynCmsNotConfiguredException)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "API key validation failed");
            return null;
        }

        _cache[hash] = (principal, DateTime.UtcNow);
        if (_cache.Count > 1000)
        {
            foreach (var stale in _cache.Where(e => DateTime.UtcNow - e.Value.CachedAt >= CacheLifetime).Select(e => e.Key).ToList())
                _cache.TryRemove(stale, out _);
        }
        return principal;
    }

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
