using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DynCMS.Core.Plugins;

/// <summary>What a plugin gets from the host: its folders, configuration, logger and the application's services.</summary>
public interface IPluginContext
{
    /// <summary>The plugin id (the folder name under <c>App_Data/plugins</c>).</summary>
    string PluginId { get; }

    /// <summary>The plugin folder: <c>App_Data/plugins/{id}</c>. Holds <c>bin</c>, optionally <c>wwwroot</c> and <c>settings.json</c>, and <c>data</c>.</summary>
    string PluginDirectory { get; }

    /// <summary>
    /// The plugin's private folder for anything it stores: SQLite databases, uploads, caches. Created before the plugin
    /// starts; kept when the plugin is stopped or updated, and only deleted on uninstall when the administrator asks for it.
    /// </summary>
    string DataDirectory { get; }

    /// <summary>Request path under which the plugin's <c>wwwroot</c> is served, e.g. <c>/_content/My.Plugin</c> (no trailing slash).</summary>
    string StaticAssetsRequestPath { get; }

    /// <summary>
    /// Plugin configuration: the host's <c>Plugins:{id}</c> configuration section (appsettings.json, environment
    /// variables) overlaid with <c>settings.json</c> from the plugin folder when present. Bind it with
    /// <c>Configuration.Get&lt;MyOptions&gt;()</c> or <c>services.Configure&lt;MyOptions&gt;(context.Configuration)</c>.
    /// </summary>
    IConfiguration Configuration { get; }

    /// <summary>
    /// The application's root service provider, extended with the plugin's own services once the plugin has started.
    /// Resolve scoped services (content, media, the plugin's own scoped services) inside a scope: <c>Services.CreateScope()</c>.
    /// </summary>
    IServiceProvider Services { get; }

    ILogger Logger { get; }

    IHostEnvironment Environment { get; }

    /// <summary>A path inside <see cref="DataDirectory"/>.</summary>
    string GetDataPath(string relativePath);

    /// <summary>A SQLite connection string for a database file inside <see cref="DataDirectory"/>.</summary>
    string SqliteConnectionString(string fileName = "plugin.db");
}

internal sealed class PluginContext : IPluginContext, IDisposable
{
    private readonly Func<IServiceProvider> _services;
    private readonly IConfigurationRoot _configuration;

    public PluginContext(
        string pluginId,
        string pluginDirectory,
        string dataDirectory,
        string staticAssetsRequestPath,
        IConfiguration hostConfiguration,
        Func<IServiceProvider> services,
        ILogger logger,
        IHostEnvironment environment)
    {
        PluginId = pluginId;
        PluginDirectory = pluginDirectory;
        DataDirectory = dataDirectory;
        StaticAssetsRequestPath = staticAssetsRequestPath;
        _services = services;
        Logger = logger;
        Environment = environment;

        Directory.CreateDirectory(dataDirectory);
        _configuration = new ConfigurationBuilder()
            .AddConfiguration(hostConfiguration.GetSection($"Plugins:{pluginId}"))
            .AddJsonFile(Path.Combine(pluginDirectory, "settings.json"), optional: true, reloadOnChange: true)
            .Build();
    }

    public string PluginId { get; }
    public string PluginDirectory { get; }
    public string DataDirectory { get; }
    public string StaticAssetsRequestPath { get; }
    public IConfiguration Configuration => _configuration;
    public IServiceProvider Services => _services();
    public ILogger Logger { get; }
    public IHostEnvironment Environment { get; }

    public string GetDataPath(string relativePath) => Path.GetFullPath(Path.Combine(DataDirectory, relativePath));

    public string SqliteConnectionString(string fileName = "plugin.db") => $"Data Source={GetDataPath(fileName)}";

    public void Dispose() => (_configuration as IDisposable)?.Dispose();
}
