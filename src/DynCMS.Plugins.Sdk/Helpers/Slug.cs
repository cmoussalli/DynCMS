using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DynCMS.Plugins.Helpers;

public static partial class Slug
{
    /// <summary>
    /// Letters that are transliterated rather than just stripped of their accent, the way Umbraco's URL segment
    /// provider does by default: "Über uns" becomes "ueber-uns", not "uber-uns".
    /// </summary>
    private static readonly (string From, string To)[] Transliterations =
    [
        ("ä", "ae"), ("ö", "oe"), ("ü", "ue"), ("Ä", "ae"), ("Ö", "oe"), ("Ü", "ue"), ("ß", "ss"),
        ("æ", "ae"), ("Æ", "ae"), ("ø", "oe"), ("Ø", "oe"), ("å", "aa"), ("Å", "aa"), ("œ", "oe"), ("Œ", "oe"), ("þ", "th"), ("ð", "d")
    ];

    /// <summary>Creates a URL segment such as "my-page" from free text.</summary>
    public static string ToUrlSegment(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        foreach (var (from, to) in Transliterations) input = input.Replace(from, to, StringComparison.Ordinal);
        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        var slug = NonAlphaNumeric().Replace(sb.ToString(), "-");
        return MultiDash().Replace(slug, "-").Trim('-');
    }

    /// <summary>Creates a camelCase alias such as "myProperty" from free text.</summary>
    public static string ToAlias(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var parts = ToUrlSegment(input).Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return string.Empty;
        var sb = new StringBuilder(parts[0]);
        foreach (var p in parts.Skip(1))
        {
            sb.Append(char.ToUpperInvariant(p[0]));
            if (p.Length > 1) sb.Append(p, 1, p.Length - 1);
        }
        var alias = sb.ToString();
        if (char.IsDigit(alias[0])) alias = "p" + alias;
        return alias;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphaNumeric();

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultiDash();
}
