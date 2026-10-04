using DynCMS.Plugins.Models;

namespace DynCMS.Plugins.Services;

public interface IContentTypeService
{
    Task<IReadOnlyList<ContentType>> GetAllAsync(CancellationToken ct = default);
    Task<ContentType?> GetAsync(Guid id, CancellationToken ct = default);
    Task<ContentType?> GetByAliasAsync(string alias, CancellationToken ct = default);
    Task<bool> AliasExistsAsync(string alias, Guid? excludeId = null, CancellationToken ct = default);
    Task<ContentType> SaveAsync(ContentType contentType, CancellationToken ct = default);
    Task<int> CountContentAsync(Guid contentTypeId, CancellationToken ct = default);
    /// <summary>Deletes a document type. Throws <see cref="InvalidOperationException"/> when content still uses it.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
