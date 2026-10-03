using DynCMS.Core.Models;
using DynCMS.Core.Security;

namespace DynCMS.Core.Api;

public sealed partial class CmsManagement
{
    #region Languages

    public async Task<IReadOnlyList<LanguageDto>> GetLanguagesAsync(CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Read);
        return (await languages.GetAllAsync(ct)).Select(MapLanguage).ToList();
    }

    public async Task<LanguageDto> GetLanguageAsync(string isoCode, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Content, CmsPermissions.Actions.Read);
        var all = await languages.GetAllAsync(ct);
        return MapLanguage(all.Match(isoCode) ?? throw CmsApiException.NotFound($"Language '{isoCode}'"));
    }

    public async Task<LanguageDto> CreateLanguageAsync(SaveLanguageRequest request, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.DocumentTypes, CmsPermissions.Actions.Create);
        if (string.IsNullOrWhiteSpace(request.IsoCode)) throw CmsApiException.BadRequest("isoCode is required, e.g. en-US or de-DE.");
        var language = new Language { IsoCode = request.IsoCode.Trim() };
        Apply(language, request);
        return MapLanguage(await Guard(() => languages.SaveAsync(language, ct)));
    }

    public async Task<LanguageDto> UpdateLanguageAsync(string isoCode, SaveLanguageRequest request, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.DocumentTypes, CmsPermissions.Actions.Update);
        var all = await languages.GetAllAsync(ct);
        var language = all.Match(isoCode) ?? throw CmsApiException.NotFound($"Language '{isoCode}'");
        if (!string.IsNullOrWhiteSpace(request.IsoCode)) language.IsoCode = request.IsoCode.Trim();
        Apply(language, request);
        if (request.IsDefault == false && language.IsDefault)
            throw CmsApiException.BadRequest("A site always has a default language: make another language the default instead.");
        return MapLanguage(await Guard(() => languages.SaveAsync(language, ct)));
    }

    public async Task DeleteLanguageAsync(string isoCode, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.DocumentTypes, CmsPermissions.Actions.Delete);
        var all = await languages.GetAllAsync(ct);
        var language = all.Match(isoCode) ?? throw CmsApiException.NotFound($"Language '{isoCode}'");
        await Guard(() => languages.DeleteAsync(language.IsoCode, ct));
    }

    private static void Apply(Language language, SaveLanguageRequest r)
    {
        if (r.Name is not null) language.Name = r.Name.Trim();
        if (r.IsDefault == true) language.IsDefault = true;
        if (r.IsMandatory is not null) language.IsMandatory = r.IsMandatory.Value;
        if (r.FallbackIsoCode is not null) language.FallbackIsoCode = string.IsNullOrWhiteSpace(r.FallbackIsoCode) ? null : r.FallbackIsoCode.Trim();
        if (r.SortOrder is not null) language.SortOrder = r.SortOrder.Value;
    }

    private static LanguageDto MapLanguage(Language l) =>
        new(l.Id, l.IsoCode, l.Name, l.IsDefault, l.IsMandatory, l.FallbackIsoCode, l.IsDefault ? "/" : "/" + l.UrlPrefix, l.SortOrder, l.CreatedAt);

    #endregion
}
