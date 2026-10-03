using DynCMS.Core.Models;
using DynCMS.Core.Security;

namespace DynCMS.Core.Api;

public sealed partial class CmsManagement
{
    #region Dictionary

    public async Task<IReadOnlyList<DictionaryItemDto>> GetDictionaryItemsAsync(CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Dictionary, CmsPermissions.Actions.Read);
        var all = await dictionary.GetAllAsync(ct);
        return all.Select(i => MapDictionaryItem(i, all)).ToList();
    }

    public async Task<DictionaryItemDto> GetDictionaryItemAsync(string idOrKey, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Dictionary, CmsPermissions.Actions.Read);
        var all = await dictionary.GetAllAsync(ct);
        return MapDictionaryItem(FindDictionaryItem(all, idOrKey), all);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetDictionaryValuesAsync(string? culture, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Dictionary, CmsPermissions.Actions.Read);
        if (!string.IsNullOrWhiteSpace(culture) && (await languages.GetAllAsync(ct)).Match(culture) is null)
            throw CmsApiException.NotFound($"Language '{culture}'");
        return await dictionary.GetValuesAsync(culture, ct);
    }

    public async Task<DictionaryItemDto> CreateDictionaryItemAsync(SaveDictionaryItemRequest request, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Dictionary, CmsPermissions.Actions.Create);
        if (string.IsNullOrWhiteSpace(request.Key)) throw CmsApiException.BadRequest("key is required, e.g. blog.readMore.");
        var all = await dictionary.GetAllAsync(ct);
        var item = new DictionaryItem { Key = request.Key.Trim() };
        await ApplyAsync(item, request, all, ct);
        var saved = await Guard(() => dictionary.SaveAsync(item, ct));
        return MapDictionaryItem(saved, await dictionary.GetAllAsync(ct));
    }

    public async Task<DictionaryItemDto> UpdateDictionaryItemAsync(string idOrKey, SaveDictionaryItemRequest request, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Dictionary, CmsPermissions.Actions.Update);
        var all = await dictionary.GetAllAsync(ct);
        var item = FindDictionaryItem(all, idOrKey).Clone();
        if (!string.IsNullOrWhiteSpace(request.Key)) item.Key = request.Key.Trim();
        await ApplyAsync(item, request, all, ct);
        var saved = await Guard(() => dictionary.SaveAsync(item, ct));
        return MapDictionaryItem(saved, await dictionary.GetAllAsync(ct));
    }

    public async Task DeleteDictionaryItemAsync(string idOrKey, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Dictionary, CmsPermissions.Actions.Delete);
        var item = FindDictionaryItem(await dictionary.GetAllAsync(ct), idOrKey);
        await Guard(() => dictionary.DeleteAsync(item.Id, ct));
    }

    private async Task ApplyAsync(DictionaryItem item, SaveDictionaryItemRequest r, IReadOnlyList<DictionaryItem> all, CancellationToken ct)
    {
        if (r.Parent is not null)
        {
            // "" clears the parent (moves the item to the root); anything else names an existing item by id or key.
            item.ParentId = string.IsNullOrWhiteSpace(r.Parent) ? null : FindDictionaryItem(all, r.Parent).Id;
        }
        if (r.SortOrder is not null) item.SortOrder = r.SortOrder.Value;

        if (r.Translations is not null)
        {
            var known = await languages.GetAllAsync(ct);
            if (r.ReplaceTranslations == true) item.Translations.Clear();
            foreach (var (code, text) in r.Translations)
            {
                var language = known.Match(code) ?? throw CmsApiException.BadRequest($"'{code}' is not a language of this site. See GET /languages.");
                item.Set(language.IsoCode, text);
            }
        }
    }

    private static DictionaryItem FindDictionaryItem(IReadOnlyList<DictionaryItem> all, string idOrKey)
    {
        var item = Guid.TryParse(idOrKey, out var id)
            ? all.FirstOrDefault(i => i.Id == id)
            : all.FirstOrDefault(i => i.Is(idOrKey));
        return item ?? throw CmsApiException.NotFound($"Dictionary item '{idOrKey}'");
    }

    private static DictionaryItemDto MapDictionaryItem(DictionaryItem i, IReadOnlyList<DictionaryItem> all)
    {
        var parent = i.ParentId is { } p ? all.FirstOrDefault(x => x.Id == p) : null;
        return new DictionaryItemDto(
            i.Id, i.Key, i.ParentId, parent?.Key, i.Level,
            i.Translations.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase),
            i.SortOrder, i.CreatedAt, i.UpdatedAt);
    }

    #endregion
}
