using DynCMS.Core.Models;
using DynCMS.Core.Services;
using Microsoft.AspNetCore.Components;

namespace DynCMS.UI.Rendering;

/// <summary>
/// Base class for template components. Inherit from it (<c>@inherits CmsTemplateBase</c>) and
/// register the component with <c>AddTemplate&lt;T&gt;("alias")</c> to make it selectable on document types.
/// </summary>
public abstract class CmsTemplateBase : ComponentBase
{
    /// <summary>The content item being rendered.</summary>
    [Parameter] public PublishedContent Content { get; set; } = default!;

    /// <summary>The dictionary (Settings → Dictionary), for the labels a template prints in the page's language.</summary>
    [Inject] protected IDictionaryService Dictionary { get; set; } = default!;

    /// <summary>
    /// The dictionary text for <paramref name="key"/> in the language of the page (<see cref="PublishedContent.Culture"/>),
    /// following the language's fallback chain and then the default language. When there is no text,
    /// <paramref name="fallback"/> is returned, or the key itself so a missing translation is visible.
    /// </summary>
    protected string T(string key, string? fallback = null) =>
        Dictionary.GetValue(key, Content?.Culture) ?? fallback ?? key;
}
