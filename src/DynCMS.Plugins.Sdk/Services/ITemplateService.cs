using DynCMS.Core.Models;

namespace DynCMS.Core.Services;

/// <summary>Where a template is used: by content, by document types and by other templates.</summary>
public sealed record TemplateUsage(
    int ContentCount,
    IReadOnlyList<ContentType> DocumentTypes,
    IReadOnlyList<Template> UsedBy,
    IReadOnlyList<string> Uses)
{
    public static readonly TemplateUsage Empty = new(0, [], [], []);
    public bool IsUsed => ContentCount > 0 || DocumentTypes.Count > 0 || UsedBy.Count > 0;
}

/// <summary>Manages the Liquid templates stored in the database and edited in the back office.</summary>
public interface ITemplateService
{
    Task<IReadOnlyList<Template>> GetAllAsync(CancellationToken ct = default);
    Task<Template?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Template?> GetByAliasAsync(string alias, CancellationToken ct = default);
    /// <summary>
    /// Validates (name, unique alias, Liquid syntax) and saves a template, then refreshes the template registry so
    /// the change is live immediately. Throws <see cref="InvalidOperationException"/> with a user-facing message.
    /// </summary>
    Task<Template> SaveAsync(Template template, CancellationToken ct = default);
    /// <summary>Number of content items (draft or published) that use the template alias.</summary>
    Task<int> CountContentAsync(string alias, CancellationToken ct = default);
    /// <summary>Everything that points at <paramref name="alias"/>: content, document types and other templates.</summary>
    Task<TemplateUsage> GetUsageAsync(string alias, Guid? ignoreTemplateId = null, CancellationToken ct = default);
    /// <summary>Deletes a template. Throws <see cref="InvalidOperationException"/> when something still uses it.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    /// <summary>Copies a template under a new name and alias, so an existing one can be used as a starting point.</summary>
    Task<Template> DuplicateAsync(Guid id, CancellationToken ct = default);
    /// <summary>
    /// Creates an editable stored template that takes over from the Blazor component registered under
    /// <paramref name="alias"/>, scaffolded from the document types that use it.
    /// </summary>
    Task<Template> CreateOverrideForComponentAsync(string alias, CancellationToken ct = default);
    /// <summary>An alias that no template uses yet, based on <paramref name="preferred"/> ("card", "card2", …).</summary>
    Task<string> GetAvailableAliasAsync(string preferred, CancellationToken ct = default);
    /// <summary>Loads every stored template into the registry (called at startup).</summary>
    Task RefreshRegistryAsync(CancellationToken ct = default);
}
