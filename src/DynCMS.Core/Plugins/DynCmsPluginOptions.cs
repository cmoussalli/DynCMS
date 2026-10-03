namespace DynCMS.Core.Plugins;

/// <summary>Settings for the plugin system (section <c>DynCms:Plugins</c>).</summary>
public sealed class DynCmsPluginOptions
{
    /// <summary>Load plugins at all. Off: nothing under the plugin folder is touched and the Plugins page only explains that.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Folder that holds one sub-folder per plugin. Relative to the application root.</summary>
    public string RootPath { get; set; } = Path.Combine("App_Data", "plugins");

    /// <summary>
    /// Let administrators upload plugin packages (a <c>.dll</c> or a <c>.zip</c>) from the back office and the API.
    /// An uploaded plugin runs with the rights of the application, so turn this off on sites where the administrator
    /// account is not fully trusted and deploy plugins by copying them to <see cref="RootPath"/> instead.
    /// </summary>
    public bool AllowUpload { get; set; } = true;

    /// <summary>Largest plugin package accepted by upload, in bytes.</summary>
    public long MaxPackageBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>Discover MVC API controllers in plugin assemblies (and in the application). Needs <c>MapControllers()</c>, which <c>UseDynCmsHost</c> adds when this is on.</summary>
    public bool EnableControllers { get; set; } = true;

    /// <summary>Request path prefix for plugin static files: <c>{prefix}/{plugin id}/…</c>. The default matches the Razor class library convention.</summary>
    public string StaticAssetsRequestPath { get; set; } = "/_content";
}
