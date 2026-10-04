using DynCMS.Core.Analytics;
using DynCMS.Core.Api;
using DynCMS.Core.Api.Mcp;
using DynCMS.Core.Data;
using DynCMS.Core.Plugins;
using DynCMS.Core.PropertyEditors;
using DynCMS.Core.Security;
using DynCMS.Core.Services;
using DynCMS.Core.Templates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DynCMS.Core;

/// <summary>Fluent builder returned by <see cref="ServiceCollectionExtensions.AddDynCms"/>.</summary>
public interface IDynCmsBuilder
{
    IServiceCollection Services { get; }
    ITemplateRegistry Templates { get; }
    IPropertyEditorRegistry PropertyEditors { get; }

    /// <summary>Registers a Blazor component as a template that document types can use.</summary>
    IDynCmsBuilder AddTemplate<TComponent>(string alias, string? name = null);
    IDynCmsBuilder AddPropertyEditor(PropertyEditorDefinition definition);

    /// <summary>
    /// Registers work that runs once the database is available: at startup when <c>dyncms.database.json</c>
    /// exists, otherwise right after the setup page has created it. Typical use: seeding content.
    /// </summary>
    IDynCmsBuilder AddStartupTask<TTask>() where TTask : class, IDynCmsStartupTask;
}

internal sealed class DynCmsBuilder(IServiceCollection services, ITemplateRegistry templates, IPropertyEditorRegistry editors) : IDynCmsBuilder
{
    public IServiceCollection Services => services;
    public ITemplateRegistry Templates => templates;
    public IPropertyEditorRegistry PropertyEditors => editors;

    public IDynCmsBuilder AddTemplate<TComponent>(string alias, string? name = null)
    {
        templates.Register(new TemplateDefinition(alias, name ?? typeof(TComponent).Name, typeof(TComponent)));
        return this;
    }

    public IDynCmsBuilder AddPropertyEditor(PropertyEditorDefinition definition)
    {
        editors.Register(definition);
        return this;
    }

    public IDynCmsBuilder AddStartupTask<TTask>() where TTask : class, IDynCmsStartupTask
    {
        services.AddScoped<IDynCmsStartupTask, TTask>();
        return this;
    }
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the DynCMS core: persistence (SQLite or SQL Server, configured by <c>dyncms.database.json</c>
    /// or the first-run setup page), content services, registries and the CMouss.IdentityFramework based
    /// authentication scheme for the back office.
    /// </summary>
    public static IDynCmsBuilder AddDynCms(this IServiceCollection services, Action<DynCmsOptions>? configure = null)
    {
        var options = services.AddOptions<DynCmsOptions>();
        if (configure is not null) options.Configure(configure);

        var templates = new TemplateRegistry();
        var editors = new PropertyEditorRegistry();
        services.TryAddSingleton<ITemplateRegistry>(templates);
        services.TryAddSingleton<IPropertyEditorRegistry>(editors);

        services.TryAddSingleton<DynCmsPaths>();

        // Database: the configuration file decides the provider at runtime, so the DbContext factory reads it
        // on demand instead of being bound to one provider here.
        services.TryAddSingleton<DatabaseConfigurationStore>();
        services.TryAddSingleton<IDbContextFactory<DynCmsDbContext>, DynCmsDbContextFactory>();
        services.TryAddSingleton<DynCmsRuntime>();
        services.TryAddSingleton<IDynCmsRuntime>(sp => sp.GetRequiredService<DynCmsRuntime>());
        services.TryAddSingleton<StartupContext>();
        services.TryAddSingleton<IDatabaseSetupService, DatabaseSetupService>();
        services.TryAddSingleton<IDatabaseMaintenanceService, DatabaseMaintenanceService>();

        services.TryAddSingleton<IMediaStorage, FileSystemMediaStorage>();
        services.TryAddScoped<ILanguageService, LanguageService>();
        services.TryAddSingleton<DictionaryCache>();
        services.TryAddScoped<IDictionaryService, DictionaryService>();
        services.TryAddScoped<ICultureContext, CultureContext>();
        services.TryAddScoped<IContentTypeService, ContentTypeService>();
        services.TryAddScoped<IContentService, ContentService>();
        services.TryAddScoped<IPublishedContentQuery, PublishedContentQuery>();
        services.TryAddScoped<IMediaService, MediaService>();
        services.TryAddScoped<IDynCmsInitializer, DynCmsInitializer>();
        services.TryAddSingleton<ISettingsStore, SettingsStore>();
        services.TryAddSingleton<ISystemVersionService, SystemVersionService>();

        // Analytics: page views are queued by the renderer (scoped tracker + visitor context, filled from the request
        // or the circuit's connection) and written, geolocated and purged by one background worker.
        services.TryAddSingleton<IDbContextFactory<AnalyticsDbContext>, AnalyticsDbContextFactory>();
        services.TryAddSingleton<IAnalyticsSettingsService, AnalyticsSettingsService>();
        services.TryAddSingleton<AnalyticsQueue>();
        services.TryAddSingleton<IAnalyticsTracker>(sp => sp.GetRequiredService<AnalyticsQueue>());
        services.TryAddSingleton<IGeoLocator, HttpGeoLocator>();
        services.TryAddSingleton<IAnalyticsService, AnalyticsService>();
        services.TryAddScoped<AnalyticsVisitorContext>();
        services.TryAddScoped<IAnalyticsPageTracker, AnalyticsPageTracker>();
        services.AddScoped<CircuitHandler, AnalyticsCircuitHandler>();
        services.AddHostedService<AnalyticsWorker>();
        services.AddHttpClient(HttpGeoLocator.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DynCMS-Analytics/1.0");
        }).RemoveAllLoggers(); // one lookup per new address; the default per-request log lines are noise

        // Plugins: Razor class libraries loaded from App_Data/plugins at runtime. The registry is what the plugin-aware
        // service provider (PluginServiceProviderFactory, installed by AddDynCmsHost) consults; the router mounts plugin
        // pages behind the catch-all pages; the endpoint data source publishes their minimal APIs; MVC is added so a
        // plugin (or the site) can bring [ApiController]s, which the change provider makes MVC pick up at runtime.
        services.TryAddSingleton<PluginContainerRegistry>();
        services.TryAddSingleton<PluginRouter>();
        services.TryAddSingleton<IPluginRouter>(sp => sp.GetRequiredService<PluginRouter>());
        services.TryAddSingleton<PluginEndpointDataSource>();
        services.TryAddSingleton<PluginActionDescriptorChangeProvider>();
        services.AddSingleton<IActionDescriptorChangeProvider>(sp => sp.GetRequiredService<PluginActionDescriptorChangeProvider>());
        services.TryAddSingleton<PluginManager>();
        services.TryAddSingleton<IPluginManager>(sp => sp.GetRequiredService<PluginManager>());
        services.AddHostedService(sp => sp.GetRequiredService<PluginManager>());
        services.TryAddSingleton<IPluginUiExtensions, PluginUiExtensionsService>();
        services.TryAddSingleton<IPluginComponentRenderer, PluginComponentRenderer>();
        services.TryAddSingleton<CmsEventDispatcher>();
        services.TryAddSingleton<ICmsEventDispatcher>(sp => sp.GetRequiredService<CmsEventDispatcher>());
        services.AddControllers();

        // Stored templates: edited in the back office, rendered with Fluid (Liquid).
        services.TryAddSingleton<ILiquidTemplateEngine, FluidTemplateEngine>();
        services.TryAddScoped<ITemplateService, TemplateService>();
        services.TryAddScoped<IStoredTemplateRenderer, StoredTemplateRenderer>();
        services.TryAddScoped<ITemplatePreviewService, TemplatePreviewService>();

        // Security: CMouss.IdentityFramework issues and validates tokens; the handler below exposes
        // them to ASP.NET Core so [Authorize] and AuthorizeView work in Blazor.
        services.TryAddSingleton<ICmsIdentity, CmsIdentity>();
        services.AddAuthentication(DynCmsAuthDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, DynCmsAuthenticationHandler>(DynCmsAuthDefaults.Scheme, null);
        services.AddAuthorization();

        // Management API and MCP server: one application layer (CmsManagement) behind both, authorised per call
        // by CmsAccess from the current principal (back-office cookie or API key).
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IApiKeyService, ApiKeyService>();
        services.TryAddScoped<ICmsAccess, CmsAccess>();
        services.TryAddScoped<CmsManagement>();
        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
            o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
        });
        services.AddOpenApi();
        services.AddOptions<Microsoft.AspNetCore.OpenApi.OpenApiOptions>("v1")
            .Configure<IOptions<DynCmsOptions>>((o, cms) => ManagementApiEndpoints.ConfigureOpenApi(o, cms.Value.Api));
        services.AddMcpServer(o =>
            {
                o.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "DynCMS", Version = typeof(ServiceCollectionExtensions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" };
                o.ServerInstructions =
                    "You manage a DynCMS website. Call cms_overview first to learn who you are and what you may do. " +
                    "Model first: document types (with properties), then templates, then content. Property values are strings in the editor's stored format " +
                    "(rich text = HTML, toggle = \"true\"/\"false\", pickers = GUIDs, tags = JSON array). Content has a draft and a published snapshot; " +
                    "publish to make changes visible. Templates are Liquid; output is HTML-encoded so rich text needs | raw. " +
                    "Sites can have several languages (list_languages): document types that vary by culture hold a name, URL and the varying property values per language; " +
                    "pass culture on content calls and publish per language. Non-default languages are served under /{iso-code}/. " +
                    "The dictionary (list_dictionary_items) holds translated labels templates print by key ({{ 'blog.readMore' | dictionary }}), one text per language.";
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<DynCmsMcpTools>(ApiJson.Options);

        return new DynCmsBuilder(services, templates, editors);
    }
}
