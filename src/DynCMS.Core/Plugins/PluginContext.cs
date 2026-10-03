using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DynCMS.Core.Plugins;

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
