using DynCMS.Plugins.Models;
using Microsoft.AspNetCore.Components;

namespace DynCMS.UI.Rendering;

public static class PublishedContentExtensions
{
    /// <summary>Returns the stored HTML of a rich text property ready to render.</summary>
    public static MarkupString Html(this PublishedContent content, string alias) =>
        new(content[alias] ?? string.Empty);

    /// <summary>Returns the tags stored by the tags editor.</summary>
    public static IReadOnlyList<string> Tags(this PublishedContent content, string alias) =>
        content.Value<List<string>>(alias) ?? [];

    /// <summary>Returns the media id stored by a media picker, if any.</summary>
    public static Guid? MediaId(this PublishedContent content, string alias) =>
        content.Value<Guid?>(alias);

    /// <summary>Returns the content id stored by a content picker, if any.</summary>
    public static Guid? ContentId(this PublishedContent content, string alias) =>
        content.Value<Guid?>(alias);

    public static DateTime? Date(this PublishedContent content, string alias) =>
        content.Value<DateTime?>(alias);

    public static bool Flag(this PublishedContent content, string alias) =>
        content.Value<bool>(alias);
}
