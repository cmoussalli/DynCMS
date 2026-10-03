using DynCMS.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DynCMS.Core.Services;

/// <summary>
/// The language the current request or Blazor circuit is working in. <see cref="IPublishedContentQuery"/> uses it
/// whenever a call does not name a culture: an explicit <see cref="Culture"/> wins, otherwise the language prefix of
/// the current path (<c>/de/…</c>) decides, otherwise the default language. Scoped, like the query itself.
/// </summary>
public interface ICultureContext
{
    /// <summary>A language set for this scope, for example by a host that resolves languages from the domain. Null means "derive from the path".</summary>
    string? Culture { get; set; }

    /// <summary>The path of the current request or navigation, when known.</summary>
    string? RequestPath { get; }
}

/// <summary>
/// Derives the request path from Blazor's <see cref="NavigationManager"/> when running in a component (server-side
/// rendering or a circuit), else from <see cref="IHttpContextAccessor"/> (API calls). Either may be missing, so both
/// are looked up lazily and defensively.
/// </summary>
public sealed class CultureContext(IServiceProvider services) : ICultureContext
{
    public string? Culture { get; set; }

    public string? RequestPath
    {
        get
        {
            try
            {
                var navigation = services.GetService<NavigationManager>();
                if (navigation is not null)
                {
                    var relative = navigation.ToBaseRelativePath(navigation.Uri);
                    return "/" + relative;
                }
            }
            catch (InvalidOperationException)
            {
                // The navigation manager exists but has not been initialised (no component is rendering).
            }

            return services.GetService<IHttpContextAccessor>()?.HttpContext?.Request.Path.Value;
        }
    }
}

/// <summary>Language resolution shared by the read-side services (<see cref="IPublishedContentQuery"/>, <see cref="IDictionaryService"/>).</summary>
public static class CultureContextExtensions
{
    /// <summary>
    /// The language a call works in: the explicit <paramref name="culture"/>, else the one set on the context, else
    /// the language prefix of the current path (<c>/de/…</c>), else the default language.
    /// </summary>
    public static Language ResolveLanguage(this ICultureContext? context, IEnumerable<Language> languages, string? culture)
    {
        var list = languages as IReadOnlyList<Language> ?? languages.ToList();
        return list.Match(culture)
            ?? list.Match(context?.Culture)
            ?? list.Match(FirstSegment(context?.RequestPath))
            ?? list.Default()
            ?? new Language { IsoCode = "en", Name = "English", IsDefault = true };
    }

    private static string? FirstSegment(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var trimmed = path.AsSpan().TrimStart('/');
        var end = trimmed.IndexOfAny('/', '?');
        var segment = end < 0 ? trimmed : trimmed[..end];
        return segment.IsEmpty ? null : segment.ToString();
    }
}
