using System.Reflection;
using DynCMS.Core;
using Microsoft.AspNetCore.Builder;

namespace DynCMS.Host;

/// <summary>
/// Entry points for hosting DynCMS with no code of your own: <see cref="RunAsync"/> is a complete
/// <c>Program.cs</c>. For more control use <see cref="DynCmsHostExtensions.AddDynCmsHost"/> and
/// <see cref="DynCmsHostExtensions.UseDynCmsHost(WebApplication)"/> on your own builder and app.
/// </summary>
public static class DynCmsHost
{
    /// <summary>The host assembly (shell components, catch-all page, error and not-found pages).</summary>
    public static Assembly Assembly => typeof(DynCmsHost).Assembly;

    /// <summary>Styles the shell needs regardless of layout: the reconnect dialog and the Blazor error bar.</summary>
    public const string ShellStylesheet = "_content/DynCMS.Host/dyncms-shell.css";

    /// <summary>The default site theme used by <c>SiteLayout</c> and the starter Liquid templates.</summary>
    public const string SiteStylesheet = "_content/DynCMS.Host/dyncms-site.css";

    /// <summary>Builds, configures and runs a DynCMS site: the whole program.</summary>
    /// <param name="args">Command line arguments, passed to <see cref="WebApplication.CreateBuilder(string[])"/>.</param>
    /// <param name="options">Host options: layout, extra stylesheets, router assemblies, starter site, pipeline switches.</param>
    /// <param name="cms">Extra CMS registrations: component templates, property editors, startup tasks.</param>
    public static Task RunAsync(string[] args, Action<DynCmsHostOptions>? options = null, Action<IDynCmsBuilder>? cms = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        var dyncms = builder.AddDynCmsHost(options);
        cms?.Invoke(dyncms);

        var app = builder.Build();
        app.UseDynCmsHost();
        return app.RunAsync();
    }
}
