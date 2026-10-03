using System.Reflection;
using DynCMS.Core.Services;
using DynCMS.Host.Components.Layout;
using DynCMS.UI;
using Microsoft.AspNetCore.Components;

namespace DynCMS.Host;

/// <summary>How <c>DynCMS.Host</c> builds the site shell and the request pipeline. Registered as a singleton.</summary>
public sealed class DynCmsHostOptions
{
    /// <summary>The configuration section bound to <see cref="Core.DynCmsOptions"/>.</summary>
    public string ConfigurationSection { get; set; } = "DynCms";

    /// <summary>
    /// The layout public pages render in. Must be a component (normally a <c>LayoutComponentBase</c>).
    /// Defaults to the built-in <see cref="SiteLayout"/>: header with navigation, main, footer.
    /// </summary>
    public Type Layout { get; set; } = typeof(SiteLayout);

    /// <summary>The <c>lang</c> attribute of the <c>&lt;html&gt;</c> element.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Link the default site theme (<see cref="DynCmsHost.SiteStylesheet"/>). Turn off when you bring your own CSS.</summary>
    public bool IncludeSiteStylesheet { get; set; } = true;

    /// <summary>Stylesheets linked in <c>&lt;head&gt;</c> after the DynCMS ones, for example <c>"site.css"</c> from your <c>wwwroot</c>.</summary>
    public IList<string> Stylesheets { get; } = [];

    /// <summary>Scripts added at the end of <c>&lt;body&gt;</c>, after <c>blazor.web.js</c>.</summary>
    public IList<string> Scripts { get; } = [];

    /// <summary>
    /// Assemblies the router scans for routable components, in addition to <c>DynCMS.Host</c>, <c>DynCMS.UI</c>
    /// and (see <see cref="IncludeEntryAssembly"/>) the application assembly.
    /// </summary>
    public IList<Assembly> AdditionalAssemblies { get; } = [];

    /// <summary>Scan the application (entry) assembly for routable components. Default <c>true</c>.</summary>
    public bool IncludeEntryAssembly { get; set; } = true;

    /// <summary>
    /// When the database has no document types, create a starter site: a page type, a home type, Liquid
    /// templates and a published home page, so a fresh install renders something at <c>/</c>.
    /// Set to <c>false</c> when you seed your own content with an <c>IDynCmsStartupTask</c>.
    /// </summary>
    public bool SeedStarterSite { get; set; } = true;

    /// <summary>
    /// What the starter site contains when nobody was asked: the application started on an existing
    /// <c>dyncms.database.json</c> whose database turned out to be empty. The <c>/setup</c> page asks the person
    /// completing it and its answer wins over this. Default <see cref="StarterContent.EmptySite"/>: just a home page.
    /// </summary>
    public StarterContent StarterContent { get; set; } = StarterContent.EmptySite;

    /// <summary>
    /// Serve <c>wwwroot</c> with the static file middleware, ahead of routing, in addition to
    /// <c>MapStaticAssets()</c>. Default <c>true</c>. This serves files added to <c>wwwroot</c> after the build
    /// (which the static assets manifest does not know) and answers HTTP range requests correctly, which
    /// <c>MapStaticAssets</c> does not for large files (dotnet/aspnetcore#63320) — video seeking depends on it.
    /// </summary>
    public bool ServeWwwrootAtRuntime { get; set; } = true;

    /// <summary>Add <c>UseHttpsRedirection()</c>. Default <c>true</c>.</summary>
    public bool UseHttpsRedirection { get; set; } = true;

    /// <summary>Add <c>UseHsts()</c> outside Development. Default <c>true</c>.</summary>
    public bool UseHsts { get; set; } = true;

    /// <summary>Add <c>UseExceptionHandler("/Error")</c> outside Development. Default <c>true</c>.</summary>
    public bool UseExceptionHandler { get; set; } = true;

    /// <summary>
    /// Wrap the application container in the plugin-aware service provider (<c>PluginServiceProviderFactory</c>), so
    /// services a plugin registers at runtime can be injected into its components, endpoints and controllers. Default
    /// <c>true</c>. Turn off when you bring your own container (Autofac and the like); plugins then use
    /// <c>IPluginContext.Services</c> to reach their services.
    /// </summary>
    public bool PluginServiceProvider { get; set; } = true;

    /// <summary>
    /// The assemblies for the router and the endpoint, deduplicated and without <paramref name="appAssembly"/>
    /// (which the router already scans). Both places must see the same list.
    /// </summary>
    public IReadOnlyList<Assembly> GetRouterAssemblies(Assembly appAssembly)
    {
        var list = new List<Assembly>();
        Add(DynCmsHost.Assembly);
        Add(DynCmsUi.Assembly);
        if (IncludeEntryAssembly) Add(Assembly.GetEntryAssembly());
        foreach (var assembly in AdditionalAssemblies) Add(assembly);
        return list;

        void Add(Assembly? assembly)
        {
            if (assembly is not null && assembly != appAssembly && !list.Contains(assembly))
                list.Add(assembly);
        }
    }

    internal void Validate()
    {
        if (!typeof(IComponent).IsAssignableFrom(Layout))
            throw new InvalidOperationException($"{nameof(DynCmsHostOptions)}.{nameof(Layout)} must be a Blazor component; {Layout} is not.");
        if (string.IsNullOrWhiteSpace(ConfigurationSection))
            throw new InvalidOperationException($"{nameof(DynCmsHostOptions)}.{nameof(ConfigurationSection)} must not be empty.");
    }
}
