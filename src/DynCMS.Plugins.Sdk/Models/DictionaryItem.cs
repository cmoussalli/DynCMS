namespace DynCMS.Plugins.Models;

/// <summary>
/// One entry of the dictionary (Settings → Dictionary), modelled on Umbraco's dictionary: a <see cref="Key"/> that
/// templates look up, with a translation per language. Items form a tree for organisation only ("blog" holding
/// "blog.readMore"); the key alone identifies an item and is unique across the whole dictionary.
/// Templates print the translation of the page's language, falling back along the language's fallback chain and
/// then to the default language.
/// </summary>
public class DictionaryItem
{
    public const int MaxKeyLength = 200;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The item this one is filed under, for organisation; null at the root.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>The key templates look up, unique (case-insensitive) across the dictionary, for example <c>blog.readMore</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The text per language, keyed by ISO code. A missing or empty entry means "not translated".</summary>
    public Dictionary<string, string?> Translations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Depth in the tree, 0 at the root. Computed when items are listed; not stored.</summary>
    public int Level { get; set; }

    /// <summary>The translation stored for <paramref name="isoCode"/> itself (no fallback), or null when empty.</summary>
    public string? Get(string? isoCode)
    {
        if (string.IsNullOrEmpty(isoCode)) return null;
        return Translations.TryGetValue(isoCode, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    /// <summary>Stores (or, for an empty value, removes) the translation for <paramref name="isoCode"/>.</summary>
    public void Set(string isoCode, string? value)
    {
        if (string.IsNullOrWhiteSpace(isoCode)) return;
        if (string.IsNullOrWhiteSpace(value)) Translations.Remove(isoCode);
        else Translations[isoCode] = value;
    }

    public bool HasTranslation(string? isoCode) => Get(isoCode) is not null;

    /// <summary>The languages this item is translated in.</summary>
    public IReadOnlyList<string> TranslatedCultures =>
        Translations.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();

    /// <summary>
    /// The text to show for <paramref name="isoCode"/>: its own translation, else the first one along the language's
    /// fallback chain, else the default language's. Null when none of them has one.
    /// </summary>
    public string? Resolve(string? isoCode, IEnumerable<Language> languages)
    {
        var list = languages as IReadOnlyList<Language> ?? languages.ToList();
        if (!string.IsNullOrEmpty(isoCode))
        {
            foreach (var code in list.FallbackChain(isoCode))
            {
                if (Get(code) is { } hit) return hit;
            }
        }
        return Get(list.Default()?.IsoCode);
    }

    /// <summary>Keys are compared ignoring case.</summary>
    public bool Is(string? key) => string.Equals(Key, key?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A detached copy that can be edited without touching the cached original.</summary>
    public DictionaryItem Clone() => new()
    {
        Id = Id,
        ParentId = ParentId,
        Key = Key,
        Translations = new Dictionary<string, string?>(Translations, StringComparer.OrdinalIgnoreCase),
        SortOrder = SortOrder,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Level = Level
    };
}
