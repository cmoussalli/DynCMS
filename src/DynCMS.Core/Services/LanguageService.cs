using System.Globalization;
using DynCMS.Core.Data;
using DynCMS.Plugins.Models;
using Microsoft.EntityFrameworkCore;

namespace DynCMS.Core.Services;

public sealed class LanguageService(IDbContextFactory<DynCmsDbContext> factory, DictionaryCache dictionary) : ILanguageService
{
    public async Task<IReadOnlyList<Language>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await LoadAsync(db, ct);
    }

    /// <summary>Loads the languages with a context the caller already has open (used by the content services).</summary>
    internal static async Task<List<Language>> LoadAsync(DynCmsDbContext db, CancellationToken ct) =>
        await db.Languages.AsNoTracking()
            .OrderByDescending(l => l.IsDefault).ThenBy(l => l.SortOrder).ThenBy(l => l.Name)
            .ToListAsync(ct);

    public async Task<Language> GetDefaultAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await LoadAsync(db, ct)).Default()
            ?? throw new InvalidOperationException("No language has been configured. Add one under Settings → Languages.");
    }

    public async Task<Language?> GetAsync(string isoCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(isoCode)) return null;
        await using var db = await factory.CreateDbContextAsync(ct);
        var code = isoCode.Trim().ToLowerInvariant();
        return await db.Languages.AsNoTracking().FirstOrDefaultAsync(l => l.IsoCode.ToLower() == code, ct);
    }

    public async Task<Language> SaveAsync(Language language, CancellationToken ct = default)
    {
        var isoCode = NormalizeIsoCode(language.IsoCode)
            ?? throw new InvalidOperationException($"'{language.IsoCode}' is not a known culture. Use a name such as en, en-US or de-DE.");
        var name = string.IsNullOrWhiteSpace(language.Name) ? DisplayNameOf(isoCode)! : language.Name.Trim();

        await using var db = await factory.CreateDbContextAsync(ct);
        var all = await db.Languages.ToListAsync(ct);

        var existing = all.FirstOrDefault(l => l.Id == language.Id);
        if (all.Any(l => l.Id != language.Id && l.Is(isoCode)))
            throw new InvalidOperationException($"The language '{isoCode}' already exists.");

        var fallback = NormalizeIsoCode(language.FallbackIsoCode);
        if (!string.IsNullOrEmpty(language.FallbackIsoCode) && fallback is null)
            throw new InvalidOperationException($"'{language.FallbackIsoCode}' is not a known culture.");
        if (fallback is not null)
        {
            if (string.Equals(fallback, isoCode, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A language cannot fall back to itself.");
            var target = all.FirstOrDefault(l => l.Is(fallback))
                ?? throw new InvalidOperationException($"The fallback language '{fallback}' does not exist. Add it first.");
            fallback = target.IsoCode;
        }

        if (existing is null)
        {
            existing = new Language
            {
                Id = language.Id == Guid.Empty ? Guid.NewGuid() : language.Id,
                CreatedAt = DateTime.UtcNow,
                SortOrder = all.Count == 0 ? 0 : all.Max(l => l.SortOrder) + 1
            };
            db.Languages.Add(existing);
            all.Add(existing);
        }

        existing.IsoCode = isoCode;
        existing.Name = name;
        existing.IsMandatory = language.IsMandatory;
        existing.FallbackIsoCode = fallback;
        if (language.SortOrder != 0 || existing.SortOrder == 0) existing.SortOrder = language.SortOrder;

        // Exactly one default: the first language ever saved becomes it; flagging another one moves the flag.
        var isDefault = language.IsDefault || !all.Any(l => l.IsDefault) || all.All(l => l.Id == existing.Id);
        if (isDefault)
        {
            foreach (var other in all) other.IsDefault = other.Id == existing.Id;
        }

        await db.SaveChangesAsync(ct);
        await dictionary.ReloadAsync(ct);   // fallback chains and the default language are part of the dictionary snapshot
        return (await GetAsync(existing.IsoCode, ct))!;
    }

    public async Task<Language> SetDefaultAsync(string isoCode, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var all = await db.Languages.ToListAsync(ct);
        var target = all.FirstOrDefault(l => l.Is(isoCode?.Trim()))
            ?? throw new InvalidOperationException($"The language '{isoCode}' does not exist.");
        foreach (var l in all) l.IsDefault = l.Id == target.Id;
        await db.SaveChangesAsync(ct);
        await dictionary.ReloadAsync(ct);
        return (await GetAsync(target.IsoCode, ct))!;
    }

    public async Task DeleteAsync(string isoCode, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var all = await db.Languages.ToListAsync(ct);
        var target = all.FirstOrDefault(l => l.Is(isoCode?.Trim()));
        if (target is null) return;
        if (target.IsDefault)
            throw new InvalidOperationException("The default language cannot be deleted. Make another language the default first.");

        foreach (var l in all.Where(l => string.Equals(l.FallbackIsoCode, target.IsoCode, StringComparison.OrdinalIgnoreCase)))
            l.FallbackIsoCode = null;
        db.Languages.Remove(target);

        // The per-language content of that language goes with it (the JSON cannot be queried, so filter in memory).
        var nodes = await db.ContentNodes.ToListAsync(ct);
        foreach (var node in nodes)
        {
            if (!node.Cultures.ContainsKey(target.IsoCode)) continue;
            var cultures = new Dictionary<string, ContentCulture>(node.Cultures, StringComparer.OrdinalIgnoreCase);
            cultures.Remove(target.IsoCode);
            node.Cultures = cultures;
            if (node.IsPublished && !cultures.Values.Any(c => c.IsPublished) && cultures.Count > 0) node.IsPublished = false;
        }

        // ... and so do its dictionary translations.
        foreach (var item in await db.DictionaryItems.ToListAsync(ct))
        {
            var match = item.Translations.Keys.FirstOrDefault(k => string.Equals(k, target.IsoCode, StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;
            var translations = new Dictionary<string, string?>(item.Translations, StringComparer.OrdinalIgnoreCase);
            translations.Remove(match);
            item.Translations = translations;
        }

        await db.SaveChangesAsync(ct);
        await dictionary.ReloadAsync(ct);
    }

    public async Task<Language> EnsureDefaultAsync(string isoCode, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var all = await LoadAsync(db, ct);
        if (all.Default() is { } current)
        {
            if (!all.Any(l => l.IsDefault))
            {
                // Legacy rows without a default flag: make the first one the default.
                var tracked = await db.Languages.FirstAsync(l => l.Id == current.Id, ct);
                tracked.IsDefault = true;
                await db.SaveChangesAsync(ct);
            }
            return current;
        }

        var code = NormalizeIsoCode(isoCode) ?? "en";
        var language = new Language { IsoCode = code, Name = DisplayNameOf(code) ?? code, IsDefault = true, SortOrder = 0 };
        db.Languages.Add(language);
        await db.SaveChangesAsync(ct);
        return language;
    }

    public string? DisplayNameOf(string? isoCode)
    {
        var code = NormalizeIsoCode(isoCode);
        if (code is null) return null;
        var culture = CultureInfo.GetCultureInfo(code);
        return culture.EnglishName;
    }

    /// <summary>The culture name as .NET spells it (<c>en-us</c> → <c>en-US</c>), or null when it is not a real culture.</summary>
    internal static string? NormalizeIsoCode(string? isoCode)
    {
        if (string.IsNullOrWhiteSpace(isoCode)) return null;
        var code = isoCode.Trim();
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            // Unknown names are accepted by the OS on some platforms (as custom cultures); reject those.
            if (culture.Equals(CultureInfo.InvariantCulture)) return null;
            if ((culture.CultureTypes & CultureTypes.UserCustomCulture) != 0 && !CultureInfo.GetCultures(CultureTypes.AllCultures).Any(c => string.Equals(c.Name, code, StringComparison.OrdinalIgnoreCase)))
                return null;
            return culture.Name;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
