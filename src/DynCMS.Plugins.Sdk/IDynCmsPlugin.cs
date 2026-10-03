using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DynCMS.Core.Plugins;

/// <summary>
/// A DynCMS plugin: a Razor class library that is dropped into <c>App_Data/plugins</c> (or uploaded from the back
/// office) and takes part in the running site without a restart. Implement this in exactly one public class of the
/// plugin assembly, with a parameterless constructor; <see cref="DynCmsPlugin"/> is the convenient base.
/// <para>
/// What a plugin can bring:
/// <list type="bullet">
/// <item>Blazor pages. Every component with <c>@page</c> is mounted at the route it declares: routes under
/// <c>/admin/…</c> render inside the back office (signed-in users only), all other routes render inside the site
/// layout.</item>
/// <item>Back-office navigation: <see cref="MenuItems"/> appear in the top bar or under Settings.</item>
/// <item>Services (<see cref="ConfigureServices"/>), injectable into the plugin's own components, endpoints and
/// controllers as usual.</item>
/// <item>Minimal-API endpoints (<see cref="MapEndpoints"/>) and MVC API controllers found in the assembly.</item>
/// <item>A private folder for its own files, for example a SQLite database (<see cref="IPluginContext.DataDirectory"/>).</item>
/// <item>Static files: a <c>wwwroot</c> folder next to the binaries is served at <c>/_content/{plugin id}/…</c>.</item>
/// </list>
/// </para>
/// </summary>
public interface IDynCmsPlugin
{
    /// <summary>Unique, URL-safe id (letters, digits, <c>.</c>, <c>-</c>, <c>_</c>). It names the plugin folder. Defaults to the assembly name.</summary>
    string Id { get; }

    /// <summary>Display name in the back office.</summary>
    string Name { get; }

    string? Description { get; }
    string? Version { get; }
    string? Author { get; }

    /// <summary>
    /// The lowest DynCMS version (<see cref="CmsVersion.Application"/>) this plugin works with, for example
    /// <c>0.2.0</c>. Required: a plugin that leaves it empty, or that needs a newer DynCMS than the one running,
    /// is not attached; the back office asks the administrator to update DynCMS first.
    /// </summary>
    string? MinimumCmsVersion => null;

    /// <summary>Icon name from the back office icon set (for example <c>box</c>, <c>zap</c>, <c>message</c>).</summary>
    string? Icon { get; }

    /// <summary>Links the plugin adds to the back-office navigation while it runs.</summary>
    IReadOnlyList<PluginMenuItem> MenuItems { get; }

    /// <summary>
    /// Registers the plugin's services. They live in a container owned by the plugin that falls back to the
    /// application's services, so a plugin service can depend on <c>IContentService</c>, <c>ILogger&lt;T&gt;</c> and
    /// anything else the host registers, and the plugin's components, endpoints and controllers can inject the
    /// plugin's services. Called every time the plugin starts.
    /// </summary>
    void ConfigureServices(IServiceCollection services, IPluginContext context);

    /// <summary>
    /// Maps minimal-API endpoints. They go live when the plugin starts and disappear when it stops. Prefer routes
    /// under <c>/api/plugins/{id}/…</c>; add <c>RequireAuthorization()</c> for anything that changes data.
    /// </summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context);

    /// <summary>
    /// Runs when the plugin starts, after its services are available (<see cref="IPluginContext.Services"/>).
    /// Typical work: create or migrate the plugin's database, warm caches. Throwing marks the plugin as failed.
    /// </summary>
    Task StartAsync(IPluginContext context, CancellationToken ct);

    /// <summary>Runs when the plugin is stopped, reloaded or uninstalled, before its services are disposed.</summary>
    Task StopAsync(IPluginContext context, CancellationToken ct);
}

/// <summary>
/// Base class for plugins: metadata comes from the assembly attributes (<c>AssemblyTitle</c>, <c>Description</c>,
/// <c>Company</c>, <c>InformationalVersion</c>, all settable in the project file) and every hook is a no-op, so a
/// plugin overrides only what it needs.
/// </summary>
public abstract class DynCmsPlugin : IDynCmsPlugin
{
    private Assembly Assembly => GetType().Assembly;

    public virtual string Id => Assembly.GetName().Name ?? GetType().Name;

    public virtual string Name => Assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title is { Length: > 0 } title ? title : Id;

    public virtual string? Description => Assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description;

    public virtual string? Version =>
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetName().Version?.ToString(3);

    public virtual string? Author => Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;

    /// <summary>Read from <c>[assembly: DynCmsMinimumVersion("0.1.0")]</c> (or the <c>DynCmsMinimumVersion</c> assembly metadata), unless overridden.</summary>
    public virtual string? MinimumCmsVersion =>
        Assembly.GetCustomAttribute<DynCmsMinimumVersionAttribute>()?.Version
        ?? Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "DynCmsMinimumVersion")?.Value;

    public virtual string? Icon => "box";

    public virtual IReadOnlyList<PluginMenuItem> MenuItems => [];

    public virtual void ConfigureServices(IServiceCollection services, IPluginContext context) { }

    public virtual void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context) { }

    public virtual Task StartAsync(IPluginContext context, CancellationToken ct) => Task.CompletedTask;

    public virtual Task StopAsync(IPluginContext context, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Declares the lowest DynCMS version a plugin assembly supports: <c>[assembly: DynCmsMinimumVersion("0.1.0")]</c>.
/// <see cref="DynCmsPlugin"/> exposes it as <see cref="IDynCmsPlugin.MinimumCmsVersion"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class DynCmsMinimumVersionAttribute(string version) : Attribute
{
    public string Version { get; } = version;
}

/// <summary>Thrown when a plugin cannot be attached because of the DynCMS version it declares.</summary>
public sealed class PluginIncompatibleException(string message) : InvalidOperationException(message);

/// <summary>Where a plugin's navigation link is shown.</summary>
public enum PluginMenuPlacement
{
    /// <summary>The back-office top bar, after the built-in sections.</summary>
    Main,
    /// <summary>The tree on the left of the Settings section, in a "Plugins" group.</summary>
    Settings
}

/// <summary>A link a plugin adds to the back-office navigation.</summary>
/// <param name="Title">Link text.</param>
/// <param name="Url">Absolute path, normally one of the plugin's own <c>/admin/…</c> pages.</param>
/// <param name="Icon">Icon name from the back office icon set.</param>
/// <param name="Placement">Top bar or Settings tree.</param>
/// <param name="Roles">Comma-separated role ids that may see the link (for example <c>Administrators</c>); null shows it to every signed-in user.</param>
/// <param name="Order">Sort order among plugin links.</param>
public sealed record PluginMenuItem(
    string Title,
    string Url,
    string Icon = "box",
    PluginMenuPlacement Placement = PluginMenuPlacement.Main,
    string? Roles = null,
    int Order = 0);
