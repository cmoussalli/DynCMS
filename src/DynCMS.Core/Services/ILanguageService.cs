using DynCMS.Core.Models;

namespace DynCMS.Core.Services;

/// <summary>The languages of the site (Settings → Languages).</summary>
public interface ILanguageService
{
    /// <summary>All languages, default first, then by sort order and name.</summary>
    Task<IReadOnlyList<Language>> GetAllAsync(CancellationToken ct = default);

    /// <summary>The default language. Throws when there is none (the database has not been initialised).</summary>
    Task<Language> GetDefaultAsync(CancellationToken ct = default);

    /// <summary>A language by ISO code (case-insensitive), or null.</summary>
    Task<Language?> GetAsync(string isoCode, CancellationToken ct = default);

    /// <summary>
    /// Creates or updates a language. Validates the ISO code against the .NET cultures, keeps exactly one default,
    /// and rejects a fallback that does not exist or points at the language itself.
    /// </summary>
    Task<Language> SaveAsync(Language language, CancellationToken ct = default);

    /// <summary>Makes <paramref name="isoCode"/> the default language (the previous default keeps its other settings).</summary>
    Task<Language> SetDefaultAsync(string isoCode, CancellationToken ct = default);

    /// <summary>
    /// Deletes a language and the per-language content stored in it. The default language cannot be deleted;
    /// languages that used it as fallback lose the fallback.
    /// </summary>
    Task DeleteAsync(string isoCode, CancellationToken ct = default);

    /// <summary>Creates the default language when the site has none yet. Called at startup.</summary>
    Task<Language> EnsureDefaultAsync(string isoCode, CancellationToken ct = default);

    /// <summary>Validates a culture name and returns its display name, or null when .NET does not know it.</summary>
    string? DisplayNameOf(string? isoCode);
}
