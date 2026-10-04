using DynCMS.Plugins.Models;

namespace DynCMS.Plugins.Services;

/// <summary>A reason content cannot be published. <paramref name="Culture"/> names the language the error belongs to, or is null for shared fields.</summary>
public sealed record ContentValidationError(string PropertyAlias, string Message, string? Culture = null);

public sealed record PublishResult(bool Success, IReadOnlyList<ContentValidationError> Errors, ContentNode? Node)
{
    public static PublishResult Ok(ContentNode node) => new(true, [], node);
    public static PublishResult Failed(IReadOnlyList<ContentValidationError> errors) => new(false, errors, null);
}

public interface IContentService
{
    Task<IReadOnlyList<ContentNode>> GetRootsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ContentNode>> GetChildrenAsync(Guid parentId, CancellationToken ct = default);
    Task<bool> HasChildrenAsync(Guid id, CancellationToken ct = default);
    Task<ContentNode?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ContentNode>> GetAncestorsAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ContentNode>> GetDescendantsAsync(Guid id, CancellationToken ct = default);
    /// <summary>All nodes, ordered depth-first the way a tree would display them.</summary>
    Task<IReadOnlyList<ContentNode>> GetTreeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ContentNode>> GetRecentAsync(int take = 10, CancellationToken ct = default);
    Task<IReadOnlyList<ContentNode>> SearchAsync(string term, int take = 25, CancellationToken ct = default);
    Task<int> CountAsync(bool? published = null, CancellationToken ct = default);

    /// <summary>Document types that may be created under <paramref name="parentId"/> (or at the root when null).</summary>
    Task<IReadOnlyList<ContentType>> GetAllowedChildTypesAsync(Guid? parentId, CancellationToken ct = default);

    /// <summary>
    /// Creates a draft. For a document type that varies by culture, <paramref name="name"/> becomes the name in
    /// <paramref name="culture"/> (the default language when null); other languages are added by saving a name for them.
    /// </summary>
    Task<ContentNode> CreateAsync(Guid contentTypeId, Guid? parentId, string name, string? culture = null, CancellationToken ct = default);

    /// <summary>Saves the draft state (name, URL segment, template and property values), in every language the node carries.</summary>
    Task<ContentNode> SaveAsync(ContentNode node, CancellationToken ct = default);

    /// <summary>
    /// The reasons the node cannot be published in <paramref name="culture"/> (name and mandatory properties).
    /// For invariant content the culture is ignored.
    /// </summary>
    IReadOnlyList<ContentValidationError> Validate(ContentNode node, string? culture = null);

    /// <summary>
    /// Publishes the draft. For content that varies by culture, <paramref name="cultures"/> names the languages to
    /// publish (every language that has a name when null); mandatory languages must be published too, or the call
    /// fails with the reasons. Invariant content ignores the list.
    /// </summary>
    Task<PublishResult> PublishAsync(Guid id, IReadOnlyList<string>? cultures = null, CancellationToken ct = default);

    /// <summary>Takes the node off the site: one language when <paramref name="culture"/> is given, otherwise every language.</summary>
    Task<ContentNode?> UnpublishAsync(Guid id, string? culture = null, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task MoveAsync(Guid id, int direction, CancellationToken ct = default);
}
