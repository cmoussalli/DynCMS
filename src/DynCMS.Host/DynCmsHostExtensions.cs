using DynCMS.Core;
using DynCMS.Core.Plugins;
using DynCMS.Host.Components;
using DynCMS.UI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DynCMS.Host;

public static class DynCmsHostExtensions
{
    /// <summary>
    /// Registers everything a DynCMS site needs: Razor components with interactive server rendering, the
    /// core services (bound to the <c>DynCms</c> configuration section), the admin UI, the starter-site
    /// seeder and the startup initialisation. Returns the CMS builder so templates, property editors and
    /// startup tasks can be chained on.
    /// </summary>
    public static IDynCmsBuilder AddDynCmsHost(this IHostApplicationBuilder builder, Action<DynCmsHostOptions>? configure = null)
    {
        var options = new DynCmsHostOptions();
        configure?.Invoke(options);
        options.Validate();

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddRazorComponents().AddInteractiveServerComponents();

        var cms = services
            .AddDynCms(o => builder.Configuration.GetSection(options.ConfigurationSection).Bind(o))
            .AddDynCmsUI();

        if (options.SeedStarterSite)
        {
            services.AddScoped<DemoBlogSeeder>();
            cms.AddStartupTask<StarterSiteSeeder>();
        }

        // Hosted services start before the server listens, so the database is ready (or setup mode is on)
        // before the first request. Replaces the explicit InitializeDynCmsAsync() call.
        services.AddHostedService<DynCmsInitializationService>();

        if (options.PluginServiceProvider)
        {
            // The application container is built as usual, then wrapped so services plugins register at runtime resolve
            // through normal DI (@inject in plugin components, parameters of plugin endpoints and controllers). The
            // validation switches mirror what the default builder uses in Development.
            var development = builder.Environment.IsDevelopment();
            builder.ConfigureContainer(new PluginServiceProviderFactory(new ServiceProviderOptions
            {
                ValidateScopes = development,
                ValidateOnBuild = development
            }));
        }

        return cms;
    }

    /// <summary>
    /// Wires the whole request pipeline with the built-in shell (<c>App</c>, <c>Routes</c>, the layout from
    /// <see cref="DynCmsHostOptions.Layout"/> and the catch-all content page). Call once, then <c>app.Run()</c>.
    /// </summary>
    public static WebApplication UseDynCmsHost(this WebApplication app) => app.UseDynCmsHost<App>();

    /// <summary>
    /// Same as <see cref="UseDynCmsHost(WebApplication)"/> but with your own root component (your
    /// <c>App.razor</c> and <c>Routes.razor</c>). Give the router the assemblies from
    /// <see cref="DynCmsHostOptions.GetRouterAssemblies"/> so the admin, setup and content pages are found.
    /// </summary>
    public static WebApplication UseDynCmsHost<TRootComponent>(this WebApplication app) where TRootComponent : IComponent
    {
        var options = app.Services.GetRequiredService<DynCmsHostOptions>();

        if (!app.Environment.IsDevelopment())
        {
            if (options.UseExceptionHandler) app.UseExceptionHandler("/Error", createScopeForErrors: true);
            if (options.UseHsts) app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        if (options.UseHttpsRedirection) app.UseHttpsRedirection();

        if (options.ServeWwwrootAtRuntime)
        {
            // Before routing on purpose: once an endpoint is matched (the /{*Path} catch-all page matches
            // anything) the static file middleware steps aside, so it has to run first.
            app.UseStaticFiles();
            app.UseRouting();
        }

        app.UseDynCmsSetup();          // no dyncms.database.json yet? every page goes to /setup

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapDynCmsMedia();          // serves uploaded files from App_Data/media at /media
        app.MapDynCmsBackups();        // admin-only download of database backups (Data tab)
        app.MapDynCmsAnalyticsExport(); // CSV export of page views (Analytics section)
        app.MapDynCmsApi();            // management REST API (+ OpenAPI) for integrations and AI agents
        app.MapDynCmsMcp();            // MCP server for AI agents, same tools as the API
        app.MapDynCmsPlugins();        // endpoints and static files of running plugins (their pages go through the catch-alls)
        if (app.Services.GetRequiredService<IOptions<DynCmsOptions>>().Value.Plugins.EnableControllers)
            app.MapControllers();      // [ApiController]s of plugins (picked up at runtime) and of the site itself
        app.MapRazorComponents<TRootComponent>()
            .AddInteractiveServerRenderMode()
            .AddAdditionalAssemblies([.. options.GetRouterAssemblies(typeof(TRootComponent).Assembly)]);

        return app;
    }
}
