using DynCMS.Plugins.Models;

namespace DynCMS.Plugins;

/// <summary>Names of the places in the back office and on the site where a plugin can show its own components.</summary>
public static class PluginSlots
{
    /// <summary>A widget in the dashboard grid. The component takes no parameters.</summary>
    public const string AdminDashboard = "admin.dashboard";

    /// <summary>
    /// A panel below the content editor. The component may declare <c>[Parameter] public Guid ContentId</c> (the node
    /// being edited) to know what it belongs to.
    /// </summary>
    public const string AdminContentEditor = "admin.content.editor";

    /// <summary>
    /// Markup for the head of every site page: the component renders plain tags (<c>&lt;meta&gt;</c>, a JSON-LD
    /// <c>&lt;script&gt;</c>, a stylesheet <c>&lt;link&gt;</c>); DynCMS puts them into the head. Do not use
    /// <c>&lt;HeadContent&gt;</c> inside the component. It may read the current page (<c>NavigationManager</c>, the
    /// content services) to vary the tags.
    /// </summary>
    public const string SiteHead = "site.head";

    /// <summary>Markup at the end of every site page's body (scripts, a cookie banner, a chat widget).</summary>
    public const string SiteBodyEnd = "site.body-end";
}

/// <summary>
/// A component a plugin adds to one of the <see cref="PluginSlots"/>. It exists while the plugin runs. Components are
/// created like the plugin's pages: their services are the plugin's own services plus the application's.
/// </summary>
/// <param name="Slot">One of <see cref="PluginSlots"/> (or a slot another plugin defines).</param>
/// <param name="ComponentType">A Blazor component of the plugin.</param>
/// <param name="Order">Sort order among the components of the slot.</param>
/// <param name="Roles">Comma-separated role ids that see the component (for example <c>Administrators</c>); null shows it to everybody who can see the slot.</param>
public sealed record PluginUiExtension(string Slot, Type ComponentType, int Order = 0, string? Roles = null);

/// <summary>The components the running plugins put into the slots. Implemented by DynCMS; resolve it to render a slot of your own.</summary>
public interface IPluginUiExtensions
{
    /// <summary>The components registered for <paramref name="slot"/>, in order.</summary>
    IReadOnlyList<PluginUiExtension> For(string slot);

    /// <summary>Raised when a plugin started or stopped, so slots can re-render.</summary>
    event Action? Changed;
}

/// <summary>What happened to a content node.</summary>
public enum ContentEventKind
{
    /// <summary>A new draft node exists.</summary>
    Created,
    /// <summary>The draft was saved.</summary>
    Saved,
    /// <summary>The node (or some of its languages) went live.</summary>
    Published,
    /// <summary>The node (or one language) was taken off the site.</summary>
    Unpublished,
    /// <summary>The node and its descendants were deleted. <see cref="ContentEvent.Node"/> is the node as it was before.</summary>
    Deleted,
    /// <summary>The node was moved up or down among its siblings.</summary>
    Moved
}

/// <summary>A change to the content tree.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Node">The node after the change (before it for <see cref="ContentEventKind.Deleted"/>).</param>
/// <param name="Cultures">For publish and unpublish: the languages concerned, null for all or for invariant content.</param>
public sealed record ContentEvent(ContentEventKind Kind, ContentNode Node, IReadOnlyList<string>? Cultures = null);

/// <summary>What happened to a media item.</summary>
public enum MediaEventKind { FolderCreated, Uploaded, Renamed, Deleted }

/// <summary>A change in the media library.</summary>
public sealed record MediaEvent(MediaEventKind Kind, MediaItem Item);

/// <summary>
/// Reacts to changes in content and media. Register an implementation as a singleton (or any lifetime that can be
/// resolved from the root) in <see cref="IDynCmsPlugin.ConfigureServices"/>: <c>services.AddSingleton&lt;ICmsEventHandler, MyHandler&gt;()</c>.
/// It receives events while the plugin runs. Sites can register handlers the same way in their own startup code.
/// <para>
/// Handlers run after the change is saved, one after the other, in the request that made the change. A handler that
/// throws is logged and skipped; it never fails the operation. Keep them quick; queue slow work.
/// </para>
/// </summary>
public interface ICmsEventHandler
{
    Task OnContentAsync(ContentEvent e, CancellationToken ct) => Task.CompletedTask;

    Task OnMediaAsync(MediaEvent e, CancellationToken ct) => Task.CompletedTask;
}
