using System.Globalization;

namespace DynCMS.Plugins.Models;

/// <summary>
/// A language the site publishes content in (Settings → Languages). Content of a document type that
/// <see cref="ContentType.VariesByCulture">varies by culture</see> has a name, a URL segment, a publish state and
/// values for its varying properties per language; everything else is shared. The default language is served
/// without a URL prefix, every other language under <c>/{iso-code}/…</c>.
/// </summary>
public class Language
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The culture name, for example <c>en</c>, <c>en-US</c> or <c>de-DE</c>. Must be a valid .NET culture.</summary>
    public string IsoCode { get; set; } = string.Empty;

    /// <summary>Display name shown in the back office, for example "English (United States)".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The language served at the root URL and used when no other language applies. Exactly one language is the default.</summary>
    public bool IsDefault { get; set; }

    /// <summary>A mandatory language must be published before a content item can be published in any other language.</summary>
    public bool IsMandatory { get; set; }

    /// <summary>
    /// The language whose property values are shown when a varying property is empty in this one. Chains are
    /// followed (fr → de → en); cycles stop at the first repeat.
    /// </summary>
    public string? FallbackIsoCode { get; set; }

    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The URL prefix of this language: the ISO code in lower case, for example <c>de-de</c>.</summary>
    public string UrlPrefix => IsoCode.ToLowerInvariant();

    /// <summary>The .NET culture for this language, or the invariant culture when the code is not recognised.</summary>
    public CultureInfo Culture
    {
        get
        {
            try { return CultureInfo.GetCultureInfo(IsoCode); }
            catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
        }
    }

    public bool Is(string? isoCode) => string.Equals(IsoCode, isoCode, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A link to the same page in another language, used by language switchers.</summary>
public sealed record CultureLink(string IsoCode, string Name, string Url, bool IsCurrent, bool IsDefault);

/// <summary>Helpers for matching URL segments and culture codes against the configured languages.</summary>
public static class LanguageExtensions
{
    /// <summary>
    /// The language a URL segment or culture code refers to: an exact match on the ISO code first, then the
    /// only language whose primary subtag matches (so <c>/de/…</c> reaches <c>de-DE</c> when it is the only German).
    /// </summary>
    public static Language? Match(this IEnumerable<Language> languages, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var list = languages as IReadOnlyList<Language> ?? languages.ToList();
        code = code.Trim();

        var exact = list.FirstOrDefault(l => l.Is(code));
        if (exact is not null) return exact;

        var primary = Primary(code);
        var candidates = list.Where(l => string.Equals(Primary(l.IsoCode), primary, StringComparison.OrdinalIgnoreCase)).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>The default language, or the first one when none is flagged.</summary>
    public static Language? Default(this IEnumerable<Language> languages)
    {
        var list = languages as IReadOnlyList<Language> ?? languages.ToList();
        return list.FirstOrDefault(l => l.IsDefault) ?? list.OrderBy(l => l.SortOrder).FirstOrDefault();
    }

    /// <summary>The fallback chain of <paramref name="isoCode"/>, starting with itself, without repeats.</summary>
    public static IReadOnlyList<string> FallbackChain(this IEnumerable<Language> languages, string isoCode)
    {
        var list = languages as IReadOnlyList<Language> ?? languages.ToList();
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = isoCode;
        while (!string.IsNullOrEmpty(current) && seen.Add(current))
        {
            chain.Add(current);
            current = list.FirstOrDefault(l => l.Is(current))?.FallbackIsoCode;
        }
        return chain;
    }

    private static string Primary(string code)
    {
        var i = code.IndexOf('-');
        return i < 0 ? code : code[..i];
    }
}
