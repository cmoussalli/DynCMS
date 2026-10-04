using DynCMS.Plugins.Models;

namespace DynCMS.Plugins.Services;

/// <summary>
/// The dictionary (Settings → Dictionary): translated texts that templates print by key, in the language of the
/// page — the labels, buttons and messages that are not content. Modelled on Umbraco's dictionary.
/// </summary>
public interface IDictionaryService
{
    /// <summary>Every item, in tree order (parents before children, siblings by sort order then key), with <see cref="DictionaryItem.Level"/> set.</summary>
    Task<IReadOnlyList<DictionaryItem>> GetAllAsync(CancellationToken ct = default);

    Task<DictionaryItem?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>An item by key (case-insensitive), or null.</summary>
    Task<DictionaryItem?> GetByKeyAsync(string key, CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates or updates an item. The key is trimmed and must be unique; the parent must exist and may not be the
    /// item itself or one of its descendants. Translations for languages the site does not have are dropped.
    /// </summary>
    Task<DictionaryItem> SaveAsync(DictionaryItem item, CancellationToken ct = default);

    /// <summary>Deletes an item and everything filed under it.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// The text for <paramref name="key"/> in <paramref name="culture"/> (the current language when null): the
    /// language's own translation, else the first along its fallback chain, else the default language's, else null.
    /// </summary>
    Task<string?> GetValueAsync(string key, string? culture = null, CancellationToken ct = default);

    /// <summary>Every key that has a text in <paramref name="culture"/> (with fallback), for templates that print many labels.</summary>
    Task<IReadOnlyDictionary<string, string>> GetValuesAsync(string? culture = null, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="GetValueAsync"/> but answered from memory, for rendering code that cannot await (Blazor
    /// markup). The dictionary is loaded at startup and refreshed on every change, so this only returns null for a
    /// key that has no text — or before the first load, in which case a load is started for the next render.
    /// </summary>
    string? GetValue(string key, string? culture = null);

    /// <inheritdoc cref="GetValuesAsync"/>
    IReadOnlyDictionary<string, string> GetValues(string? culture = null);

    /// <summary>Reloads the in-memory copy from the database. Called at startup and after the languages change.</summary>
    Task RefreshAsync(CancellationToken ct = default);
}
