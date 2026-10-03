using System.Text.RegularExpressions;

namespace DynCMS.Core.Templates;

/// <summary>
/// Finds the partials a Liquid template pulls in, so the back office can show where a partial is used
/// and refuse to delete one that another template still renders.
/// </summary>
public static partial class TemplateReferences
{
    /// <summary>Matches <c>{% render 'alias' %}</c> and <c>{% include "alias" %}</c>, with or without whitespace control.</summary>
    [GeneratedRegex(@"\{%-?\s*(?:render|include)\s+['""]([^'""]+)['""]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RenderTag();

    /// <summary>Commented-out and literal blocks, which do not render anything.</summary>
    [GeneratedRegex(@"\{%-?\s*(comment|raw)\s*-?%\}[\s\S]*?\{%-?\s*end\1\s*-?%\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InertBlock();

    /// <summary>The aliases <paramref name="source"/> renders, in the order they first appear. Comments are ignored.</summary>
    public static IReadOnlyList<string> Find(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return [];

        var live = InertBlock().Replace(source, string.Empty);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (Match match in RenderTag().Matches(live))
        {
            var alias = match.Groups[1].Value.Trim();
            if (alias.EndsWith(".liquid", StringComparison.OrdinalIgnoreCase)) alias = alias[..^".liquid".Length];
            if (alias.Length > 0 && seen.Add(alias)) result.Add(alias);
        }
        return result;
    }

    /// <summary>Whether <paramref name="source"/> renders <paramref name="alias"/>.</summary>
    public static bool Uses(string? source, string alias) =>
        Find(source).Contains(alias, StringComparer.OrdinalIgnoreCase);
}
