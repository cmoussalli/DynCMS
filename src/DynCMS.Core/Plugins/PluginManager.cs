using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynCMS.Core.Plugins;

public enum PluginStatus
{
    /// <summary>Installed, not running: no pages, endpoints or services. Will not start on the next boot.</summary>
    Stopped,
    Running,
    /// <summary>Loading or starting failed; <see cref="PluginInfo.Error"/> says why.</summary>
    Failed
}

/// <summary>A snapshot of one installed plugin as the back office and the API show it.</summary>
public sealed record PluginInfo(
    string Id,
    string Name,
    string? Description,
    string? Version,
    string? Author,
    string Icon,
    PluginStatus Status,
    bool Enabled,
    string? Error,
    string Directory,
    string DataDirectory,
    string? AssemblyName,
    string? DeclaredId,
    DateTime? InstalledAt,
    DateTime? StartedAt,
    IReadOnlyList<PluginPage> Pages,
    IReadOnlyList<PluginMenuItem> MenuItems,
    int EndpointCount,
    int ServiceCount,
    bool HasControllers,
    bool HasStaticAssets,
    long DataSizeBytes,
    string? MinimumCmsVersion,
    PluginCompatibility Compatibility)
{
    public bool IsRunning => Status == PluginStatus.Running;
}

/// <summary>Installs, starts, stops and removes plugins at runtime. One instance per application.</summary>
public interface IPluginManager
{
    /// <summary><c>DynCms:Plugins:Enabled</c>.</summary>
    bool IsEnabled { get; }

    /// <summary><c>DynCms:Plugins:AllowUpload</c>.</summary>
    bool AllowUpload { get; }

    /// <summary>The absolute plugin folder.</summary>
    string RootPath { get; }

    /// <summary>
    /// True when the plugin-aware service provider is installed, so plugin services can be injected into components,
    /// endpoints and controllers. False means plugins still run, but must reach their own services through
    /// <see cref="IPluginContext.Services"/>.
    /// </summary>
    bool ServiceProviderAttached { get; }

    /// <summary>Every installed plugin, running or not, sorted by name.</summary>
    IReadOnlyList<PluginInfo> Plugins { get; }

    PluginInfo? Find(string id);

    /// <summary>Raised after any change: install, start, stop, reload, uninstall, rescan.</summary>
    event Action? Changed;

    /// <summary>Starts a stopped plugin (loading it first when needed) and marks it to start on the next boot.</summary>
    Task<PluginInfo> StartAsync(string id, CancellationToken ct = default);

    /// <summary>Stops a running plugin: its pages, endpoints and menu items disappear, its services are disposed. It stays installed and will not start on the next boot.</summary>
    Task<PluginInfo> StopAsync(string id, CancellationToken ct = default);

    /// <summary>Stops the plugin, unloads its assemblies and loads them again from disk, then starts it. Use after copying a new build into the plugin folder.</summary>
    Task<PluginInfo> ReloadAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Installs a plugin from an uploaded package (a single <c>.dll</c>, or a <c>.zip</c> with the assemblies and an
    /// optional <c>wwwroot</c> folder) and starts it. A plugin with the same id is replaced; its data folder is kept.
    /// </summary>
    Task<PluginInfo> InstallAsync(Stream package, string fileName, CancellationToken ct = default);

    /// <summary>Stops and removes a plugin. Its private data folder is kept unless <paramref name="deleteData"/> is true.</summary>
    Task UninstallAsync(string id, bool deleteData, CancellationToken ct = default);

    /// <summary>Picks up plugin folders that were copied to disk since the last scan (and forgets folders that disappeared).</summary>
    Task RescanAsync(CancellationToken ct = default);
}

internal sealed partial class PluginManager : IPluginManager, IHostedService
{
    private const string ShadowFolder = ".shadow";
    private const string StagingFolder = ".staging";
    private const string StateFile = "plugins.json";

    private sealed class PluginEntry
    {
        public required string Id { get; init; }
        public required string Directory { get; set; }
        public required string BinDirectory { get; set; }
        public string DataDirectory => Path.Combine(Directory, "data");
        public string? StaticRoot { get; set; }
        public bool Enabled { get; set; } = true;
        public DateTime? InstalledAt { get; set; }
        public PluginStatus Status { get; set; } = PluginStatus.Stopped;
        public string? Error { get; set; }
        public DateTime? StartedAt { get; set; }

        // Loaded state (assemblies in memory).
        public PluginLoadContext? LoadContext { get; set; }
        public Assembly? Assembly { get; set; }
        public IDynCmsPlugin? Plugin { get; set; }
        public string? ShadowDirectory { get; set; }
        public bool HasControllers { get; set; }
        public bool IsLoaded => Plugin is not null;

        // Running state.
        public PluginContainer? Container { get; set; }
        public PluginContext? Context { get; set; }
        public List<EndpointDataSource> Endpoints { get; set; } = [];
        public int EndpointCount { get; set; }
        public IReadOnlyList<PluginPage> Pages { get; set; } = [];
        public IReadOnlyList<PluginMenuItem> MenuItems { get; set; } = [];
        public AssemblyPart? Part { get; set; }
        public IReadOnlyList<PluginUiExtension> UiExtensions { get; set; } = [];
        public List<string> EditorAliases { get; } = [];
        public List<string> TemplateAliases { get; } = [];
        public List<IHostedService> HostedServices { get; } = [];
    }

    private sealed class PluginState
    {
        public bool Enabled { get; set; } = true;
        public DateTime? InstalledAt { get; set; }
    }

    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly DynCmsPluginOptions _options;
    private readonly DynCmsPaths _paths;
    private readonly PluginContainerRegistry _registry;
    private readonly PluginRouter _router;
    private readonly PluginEndpointDataSource _endpointSource;
    private readonly PluginActionDescriptorChangeProvider _controllerChanges;
    private readonly IServiceProvider _hostServices;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, PluginEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyList<PluginInfo> _snapshot = [];
    private volatile IReadOnlyList<PluginUiExtension> _extensions = [];
    private IEndpointRouteBuilder? _applicationEndpoints;
    private bool _initialized;
    private bool _warnedNoEndpoints;

    public PluginManager(
        IOptions<DynCmsOptions> options,
        DynCmsPaths paths,
        PluginContainerRegistry registry,
        PluginRouter router,
        PluginEndpointDataSource endpointSource,
        PluginActionDescriptorChangeProvider controllerChanges,
        IServiceProvider hostServices,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value.Plugins;
        _paths = paths;
        _registry = registry;
        _router = router;
        _endpointSource = endpointSource;
        _controllerChanges = controllerChanges;
        _hostServices = hostServices;
        _configuration = configuration;
        _environment = environment;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PluginManager>();
    }

    public bool IsEnabled => _options.Enabled;
    public bool AllowUpload => _options.Enabled && _options.AllowUpload;
    public string RootPath => _paths.PluginsRootPath;
    public bool ServiceProviderAttached => _registry.IsAttached;
    public IReadOnlyList<PluginInfo> Plugins => _snapshot;
    /// <summary>The slot components of the running plugins.</summary>
    internal IReadOnlyList<PluginUiExtension> Extensions => _extensions;
    public event Action? Changed;

    /// <summary>The provider plugins see: the plugin-aware wrapper when installed, else the application container.</summary>
    private IServiceProvider Services => _registry.RootProvider ?? _hostServices;

    private ApplicationPartManager? PartManager => _hostServices.GetService<ApplicationPartManager>();

    public PluginInfo? Find(string id) => _snapshot.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Called by <c>MapDynCmsPlugins()</c>: where plugin endpoints are built against.</summary>
    internal void AttachEndpoints(IEndpointRouteBuilder endpoints) => _applicationEndpoints = endpoints;

    // ---- Lifecycle driven by the host --------------------------------------------------------------------------

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask; // the runtime starts plugins once the database is ready

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)) return;
        try
        {
            foreach (var entry in _entries.Values.Where(e => e.Status == PluginStatus.Running).ToList())
                await TeardownAsync(entry, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Loads the plugin folder and starts every enabled plugin. Runs once; later calls return at once.</summary>
    internal async Task EnsureStartedAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            _initialized = true;

            Directory.CreateDirectory(RootPath);
            CleanShadowCopies();
            if (!_registry.IsAttached)
                _logger.LogWarning("The plugin-aware service provider is not installed (PluginServiceProviderFactory). Plugins run, but their services cannot be injected into components or endpoints; use IPluginContext.Services instead.");

            var state = LoadState();
            Discover(state);
            foreach (var entry in _entries.Values.Where(e => e.Enabled).ToList())
            {
                try { await StartEntryAsync(entry, ct); }
                catch (Exception ex) { MarkFailed(entry, ex); }
            }
            SaveState();
            _logger.LogInformation("Plugins: {Running} running, {Total} installed in {Path}", _entries.Values.Count(e => e.Status == PluginStatus.Running), _entries.Count, RootPath);
        }
        finally
        {
            _gate.Release();
        }
        Publish();
    }

    // ---- Public operations --------------------------------------------------------------------------------------

    public async Task<PluginInfo> StartAsync(string id, CancellationToken ct = default)
    {
        EnsureEnabled();
        await _gate.WaitAsync(ct);
        try
        {
            var entry = Get(id);
            entry.Enabled = true;
            try { await StartEntryAsync(entry, ct); }
            catch (Exception ex)
            {
                MarkFailed(entry, ex);
                SaveState();
                throw new InvalidOperationException($"Plugin '{entry.Id}' could not be started: {ex.Message}", ex);
            }
            SaveState();
            return Info(entry);
        }
        finally
        {
            _gate.Release();
            Publish();
        }
    }

    public async Task<PluginInfo> StopAsync(string id, CancellationToken ct = default)
    {
        EnsureEnabled();
        await _gate.WaitAsync(ct);
        try
        {
            var entry = Get(id);
            entry.Enabled = false;
            if (entry.Status == PluginStatus.Running) await TeardownAsync(entry, ct);
            entry.Status = PluginStatus.Stopped;
            entry.Error = null;
            SaveState();
            return Info(entry);
        }
        finally
        {
            _gate.Release();
            Publish();
        }
    }

    public async Task<PluginInfo> ReloadAsync(string id, CancellationToken ct = default)
    {
        EnsureEnabled();
        await _gate.WaitAsync(ct);
        try
        {
            var entry = Get(id);
            if (entry.Status == PluginStatus.Running) await TeardownAsync(entry, ct);
            Unload(entry);
            entry.Enabled = true;
            try { await StartEntryAsync(entry, ct); }
            catch (Exception ex)
            {
                MarkFailed(entry, ex);
                SaveState();
                throw new InvalidOperationException($"Plugin '{entry.Id}' could not be reloaded: {ex.Message}", ex);
            }
            SaveState();
            return Info(entry);
        }
        finally
        {
            _gate.Release();
            Publish();
        }
    }

    public async Task<PluginInfo> InstallAsync(Stream package, string fileName, CancellationToken ct = default)
    {
        EnsureEnabled();
        if (!AllowUpload) throw new InvalidOperationException("Plugin upload is turned off (DynCms:Plugins:AllowUpload). Copy the plugin into the plugin folder instead.");
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".dll" or ".zip")) throw new InvalidOperationException("Upload a plugin assembly (.dll) or a package (.zip with the assemblies and an optional wwwroot folder).");

        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(RootPath);
            var staging = Path.Combine(RootPath, StagingFolder, Guid.NewGuid().ToString("N"));
            var stagingBin = Path.Combine(staging, "bin");
            Directory.CreateDirectory(stagingBin);

            var probe = new PluginEntry { Id = "staging-" + Path.GetFileName(staging), Directory = staging, BinDirectory = stagingBin };
            string id;
            try
            {
                if (extension == ".dll")
                {
                    await using var file = File.Create(Path.Combine(stagingBin, Path.GetFileName(fileName)));
                    await package.CopyToAsync(file, ct);
                }
                else
                {
                    await ExtractPackageAsync(package, staging, stagingBin, ct);
                }

                Load(probe);
                id = probe.Plugin!.Id?.Trim() ?? string.Empty;
                if (!IsValidId(id))
                {
                    Unload(probe);
                    throw new InvalidOperationException($"The plugin declares the id '{id}', which is not valid. Use letters, digits, '.', '-' and '_' only.");
                }
                if (PluginLoadContext.IsHostAssembly(id) && !string.Equals(id, probe.Assembly!.GetName().Name, StringComparison.OrdinalIgnoreCase))
                {
                    Unload(probe);
                    throw new InvalidOperationException($"The plugin id '{id}' collides with an application assembly.");
                }
                // Checked before anything is replaced: an incompatible upload leaves the installed version untouched.
                try { EnsureCompatible(probe.Plugin!); }
                catch
                {
                    Unload(probe);
                    throw;
                }
            }
            catch
            {
                TryDeleteDirectory(staging);
                throw;
            }

            // Replace an existing installation of the same plugin; its data folder survives.
            if (_entries.TryGetValue(id, out var existing))
            {
                if (existing.Status == PluginStatus.Running) await TeardownAsync(existing, ct);
                Unload(existing);
                TryDeleteDirectory(Path.Combine(existing.Directory, "bin"));
                TryDeleteDirectory(Path.Combine(existing.Directory, "wwwroot"));
                if (existing.BinDirectory == existing.Directory)
                {
                    // Drop-in layout (dlls directly in the plugin folder): remove the binaries, keep data and settings.
                    foreach (var file in Directory.GetFiles(existing.Directory))
                    {
                        if (!file.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase)) TryDeleteFile(file);
                    }
                }
            }

            var directory = Path.Combine(RootPath, id);
            Directory.CreateDirectory(directory);
            MoveDirectory(stagingBin, Path.Combine(directory, "bin"));
            var stagedAssets = Path.Combine(staging, "wwwroot");
            if (Directory.Exists(stagedAssets)) MoveDirectory(stagedAssets, Path.Combine(directory, "wwwroot"));
            var stagedSettings = Path.Combine(staging, "settings.json");
            if (File.Exists(stagedSettings) && !File.Exists(Path.Combine(directory, "settings.json"))) File.Move(stagedSettings, Path.Combine(directory, "settings.json"));
            TryDeleteDirectory(staging);

            var entry = existing ?? new PluginEntry { Id = id, Directory = directory, BinDirectory = Path.Combine(directory, "bin") };
            entry.Directory = directory;
            entry.BinDirectory = Path.Combine(directory, "bin");
            entry.StaticRoot = FindStaticRoot(entry);
            entry.Enabled = true;
            entry.InstalledAt = DateTime.UtcNow;
            entry.Error = null;
            // Adopt the assemblies already loaded from the staging copy; they live in a shadow folder, not in staging.
            entry.LoadContext = probe.LoadContext;
            entry.Assembly = probe.Assembly;
            entry.Plugin = probe.Plugin;
            entry.ShadowDirectory = probe.ShadowDirectory;
            entry.HasControllers = probe.HasControllers;
            _entries[id] = entry;

            try { await StartEntryAsync(entry, ct); }
            catch (Exception ex) { MarkFailed(entry, ex); }
            SaveState();
            _logger.LogInformation("Plugin {Plugin} {Version} installed from {File}", entry.Id, entry.Plugin?.Version, fileName);
            return Info(entry);
        }
        finally
        {
            _gate.Release();
            Publish();
        }
    }

    public async Task UninstallAsync(string id, bool deleteData, CancellationToken ct = default)
    {
        EnsureEnabled();
        await _gate.WaitAsync(ct);
        try
        {
            var entry = Get(id);
            if (entry.Status == PluginStatus.Running) await TeardownAsync(entry, ct);
            Unload(entry);

            if (deleteData)
            {
                TryDeleteDirectory(entry.Directory);
            }
            else if (entry.BinDirectory != entry.Directory)
            {
                TryDeleteDirectory(entry.BinDirectory);
                TryDeleteDirectory(Path.Combine(entry.Directory, "wwwroot"));
            }
            else
            {
                foreach (var file in Directory.GetFiles(entry.Directory)) TryDeleteFile(file);
                foreach (var dir in Directory.GetDirectories(entry.Directory))
                {
                    if (!string.Equals(Path.GetFileName(dir), "data", StringComparison.OrdinalIgnoreCase)) TryDeleteDirectory(dir);
                }
            }
            if (Directory.Exists(entry.Directory) && !Directory.EnumerateFileSystemEntries(entry.Directory).Any()) TryDeleteDirectory(entry.Directory);

            _entries.Remove(entry.Id);
            SaveState();
            _logger.LogInformation("Plugin {Plugin} uninstalled ({Data})", entry.Id, deleteData ? "data deleted" : "data kept");
        }
        finally
        {
            _gate.Release();
            Publish();
        }
    }

    public async Task RescanAsync(CancellationToken ct = default)
    {
        EnsureEnabled();
        await _gate.WaitAsync(ct);
        try
        {
            var state = LoadState();
            var known = _entries.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Discover(state);
            foreach (var entry in _entries.Values.Where(e => !known.Contains(e.Id) && e.Enabled).ToList())
            {
                try { await StartEntryAsync(entry, ct); }
                catch (Exception ex) { MarkFailed(entry, ex); }
            }
            SaveState();
        }
        finally
        {
            _gate.Release();
            Publish();
        }
    }

    // ---- Static files -------------------------------------------------------------------------------------------

    /// <summary>Serves a file from the <c>wwwroot</c> of a running plugin (mapped at <c>{prefix}/{plugin}/{**path}</c>).</summary>
    internal IResult ServeStaticFile(string plugin, string path, HttpContext http)
    {
        PluginEntry? entry;
        lock (_entries) _entries.TryGetValue(plugin, out entry);
        if (entry is null || entry.Status != PluginStatus.Running || entry.StaticRoot is null || string.IsNullOrEmpty(path)) return Results.NotFound();

        var root = Path.GetFullPath(entry.StaticRoot);
        var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return Results.NotFound();

        if (!ContentTypes.TryGetContentType(full, out var contentType)) contentType = "application/octet-stream";
        http.Response.Headers.CacheControl = _environment.IsDevelopment() ? "no-cache" : "public,max-age=86400";
        return Results.File(full, contentType, lastModified: File.GetLastWriteTimeUtc(full), enableRangeProcessing: true);
    }

    // ---- Start / stop of one plugin ----------------------------------------------------------------------------

    private async Task StartEntryAsync(PluginEntry entry, CancellationToken ct)
    {
        if (entry.Status == PluginStatus.Running) return;
        if (!entry.IsLoaded) Load(entry);

        var plugin = entry.Plugin!;
        EnsureCompatible(plugin);
        var logger = _loggerFactory.CreateLogger($"DynCMS.Plugins.{entry.Id}");
        var context = new PluginContext(entry.Id, entry.Directory, entry.DataDirectory, StaticRequestPath(entry.Id), _configuration, () => Services, logger, _environment);
        entry.Context = context;
        try
        {
            var services = new ServiceCollection();
            plugin.ConfigureServices(services, context);
            var validateScopes = _registry.RootProvider is PluginHostServiceProvider wrapper && wrapper.ValidateScopes;
            var container = new PluginContainer(entry.Id, entry.LoadContext!, services, Services, validateScopes);
            entry.Container = container;
            _registry.Add(container);

            foreach (var hosted in container.GetOwn<IHostedService>())
            {
                await hosted.StartAsync(ct);
                entry.HostedServices.Add(hosted);
            }

            await plugin.StartAsync(context, ct);

            entry.Pages = PluginRouter.Discover(entry.Id, entry.Assembly!, logger);
            entry.MenuItems = (plugin.MenuItems ?? []).OrderBy(m => m.Order).ToList();
            entry.UiExtensions = (plugin.UiExtensions ?? []).OrderBy(x => x.Order).ToList();
            RegisterContributions(entry, plugin, logger);

            if (_applicationEndpoints is not null)
            {
                var builder = new PluginEndpointRouteBuilder(_applicationEndpoints, Services);
                plugin.MapEndpoints(builder, context);
                entry.Endpoints = [.. builder.DataSources];
                entry.EndpointCount = entry.Endpoints.Sum(ds => ds.Endpoints.Count);
            }
            else if (!_warnedNoEndpoints)
            {
                _warnedNoEndpoints = true;
                _logger.LogWarning("MapDynCmsPlugins() was not called; plugin endpoints and static files are not served.");
            }

            if (_options.EnableControllers && entry.HasControllers && PartManager is { } parts)
            {
                entry.Part = new AssemblyPart(entry.Assembly!);
                parts.ApplicationParts.Add(entry.Part);
            }

            entry.Status = PluginStatus.Running;
            entry.Error = null;
            entry.StartedAt = DateTime.UtcNow;
            _logger.LogInformation("Plugin {Plugin} {Version} started: {Pages} page(s), {Endpoints} endpoint(s), {Services} service(s){Controllers}",
                entry.Id, plugin.Version, entry.Pages.Count, entry.EndpointCount, container.ServiceCount, entry.Part is null ? string.Empty : ", API controllers");
        }
        catch
        {
            await TeardownAsync(entry, CancellationToken.None);
            throw;
        }
    }

    private async Task TeardownAsync(PluginEntry entry, CancellationToken ct)
    {
        if (entry.Part is not null && PartManager is { } parts)
        {
            parts.ApplicationParts.Remove(entry.Part);
            entry.Part = null;
        }
        entry.Pages = [];
        entry.MenuItems = [];
        entry.UiExtensions = [];
        UnregisterContributions(entry);
        entry.Endpoints = [];
        entry.EndpointCount = 0;

        if (entry.Plugin is not null && entry.Context is not null)
        {
            try { await entry.Plugin.StopAsync(entry.Context, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Plugin {Plugin}: StopAsync failed", entry.Id); }
        }
        for (var i = entry.HostedServices.Count - 1; i >= 0; i--)
        {
            try { await entry.HostedServices[i].StopAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Plugin {Plugin}: a hosted service failed to stop", entry.Id); }
        }
        entry.HostedServices.Clear();

        if (entry.Container is not null)
        {
            _registry.Remove(entry.Container);
            (_registry.RootProvider as PluginHostServiceProvider)?.Release(entry.Container);
            try { entry.Container.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Plugin {Plugin}: disposing its services failed", entry.Id); }
            entry.Container = null;
        }
        entry.Context?.Dispose();
        entry.Context = null;
        entry.Status = PluginStatus.Stopped;
        entry.StartedAt = null;
        _logger.LogInformation("Plugin {Plugin} stopped", entry.Id);
    }

    /// <summary>
    /// Adds the property editors and component templates a plugin brings to the application's registries. An alias an
    /// existing editor or template already owns is skipped with a warning (a plugin must not replace a built-in).
    /// </summary>
    private void RegisterContributions(PluginEntry entry, IDynCmsPlugin plugin, ILogger logger)
    {
        var editors = _hostServices.GetService<IPropertyEditorRegistry>();
        if (editors is not null)
        {
            foreach (var editor in plugin.PropertyEditors ?? [])
            {
                if (editors.Get(editor.Alias) is not null)
                {
                    logger.LogWarning("Property editor '{Alias}' already exists; the plugin's editor with that alias is ignored.", editor.Alias);
                    continue;
                }
                editors.Register(editor);
                entry.EditorAliases.Add(editor.Alias);
            }
        }

        var templates = _hostServices.GetService<ITemplateRegistry>();
        if (templates is not null)
        {
            foreach (var template in plugin.Templates ?? [])
            {
                if (template.ComponentType is null)
                {
                    logger.LogWarning("Template '{Alias}' has no ComponentType and is ignored.", template.Alias);
                    continue;
                }
                if (templates.Get(template.Alias) is not null)
                {
                    logger.LogWarning("Template '{Alias}' already exists; the plugin's template with that alias is ignored.", template.Alias);
                    continue;
                }
                templates.Register(template);
                entry.TemplateAliases.Add(template.Alias);
            }
        }
    }

    private void UnregisterContributions(PluginEntry entry)
    {
        var editors = _hostServices.GetService<IPropertyEditorRegistry>();
        foreach (var alias in entry.EditorAliases) editors?.Unregister(alias);
        entry.EditorAliases.Clear();

        var templates = _hostServices.GetService<ITemplateRegistry>();
        foreach (var alias in entry.TemplateAliases) templates?.Unregister(alias);
        entry.TemplateAliases.Clear();
    }

    /// <summary>Refuses a plugin that needs a newer DynCMS than the one running (or does not say what it needs).</summary>
    private static void EnsureCompatible(IDynCmsPlugin plugin)
    {
        var minimum = plugin.MinimumCmsVersion;
        var label = $"Plugin '{plugin.Id}'" + (string.IsNullOrWhiteSpace(plugin.Version) ? string.Empty : $" {plugin.Version}");
        switch (CmsVersion.CheckPlugin(minimum))
        {
            case PluginCompatibility.NeedsNewerCms:
                throw new PluginIncompatibleException($"{label} requires DynCMS {minimum} or newer, but this site runs DynCMS {CmsVersion.Application}. Update DynCMS first, then install the plugin again.");
            case PluginCompatibility.Undeclared:
                throw new PluginIncompatibleException($"{label} does not declare the minimum DynCMS version it supports, so it cannot be attached. Ask its author for a build with [assembly: DynCmsMinimumVersion(\"x.y.z\")] (or override MinimumCmsVersion).");
            case PluginCompatibility.Invalid:
                throw new PluginIncompatibleException($"{label} declares '{minimum}' as its minimum DynCMS version, which is not a version number (expected for example 0.1.0).");
        }
    }

    private void MarkFailed(PluginEntry entry, Exception ex)
    {
        entry.Status = PluginStatus.Failed;
        entry.Error = ex.InnerException is { } inner && ex is TargetInvocationException ? inner.Message : ex.Message;
        _logger.LogError(ex, "Plugin {Plugin} failed", entry.Id);
    }

    // ---- Loading ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Copies the plugin binaries to a shadow folder and loads them from there, so the files in the plugin folder
    /// stay replaceable while the plugin runs (Windows locks loaded assemblies).
    /// </summary>
    private void Load(PluginEntry entry)
    {
        if (!Directory.Exists(entry.BinDirectory)) throw new InvalidOperationException($"The plugin folder {entry.BinDirectory} does not exist.");

        var shadow = Path.Combine(RootPath, ShadowFolder, $"{entry.Id}-{DateTime.UtcNow:yyyyMMddHHmmssfff}");
        var skip = entry.BinDirectory == entry.Directory ? new[] { "data", "wwwroot" } : [];
        CopyDirectory(entry.BinDirectory, shadow, skip);

        var candidates = Directory.GetFiles(shadow, "*.dll", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => string.Equals(Path.GetFileNameWithoutExtension(f), entry.Id, StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0) throw new InvalidOperationException("The package contains no .dll file.");

        var preferred = candidates.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), entry.Id, StringComparison.OrdinalIgnoreCase));
        var context = new PluginLoadContext($"plugin:{entry.Id}", shadow, preferred);
        try
        {
            var loadedAny = false;
            foreach (var dll in candidates)
            {
                AssemblyName name;
                try { name = AssemblyName.GetAssemblyName(dll); }
                catch (BadImageFormatException) { continue; } // native library or not an assembly
                if (PluginLoadContext.IsHostAssembly(name.Name)) continue;

                var assembly = context.LoadFromAssemblyPath(dll);
                loadedAny = true;
                var pluginType = FindPluginType(assembly);
                if (pluginType is null) continue;

                var plugin = Activator.CreateInstance(pluginType) as IDynCmsPlugin
                    ?? throw new InvalidOperationException($"{pluginType.FullName} could not be instantiated. It needs a public parameterless constructor.");

                entry.LoadContext = context;
                entry.Assembly = assembly;
                entry.Plugin = plugin;
                entry.ShadowDirectory = shadow;
                entry.HasControllers = SafeTypes(assembly).Any(t => !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t));
                return;
            }
            throw new InvalidOperationException(loadedAny
                ? "No public class implementing IDynCmsPlugin was found. Add one that derives from DynCmsPlugin."
                : "The package contains no .NET assembly of its own (files that the application already ships are ignored).");
        }
        catch
        {
            try { context.Unload(); } catch { }
            TryDeleteDirectory(shadow);
            throw;
        }
    }

    private static Type? FindPluginType(Assembly assembly) =>
        SafeTypes(assembly).FirstOrDefault(t => t.IsClass && !t.IsAbstract && typeof(IDynCmsPlugin).IsAssignableFrom(t));

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    private void Unload(PluginEntry entry)
    {
        var context = entry.LoadContext;
        var shadow = entry.ShadowDirectory;
        entry.LoadContext = null;
        entry.Assembly = null;
        entry.Plugin = null;
        entry.ShadowDirectory = null;
        entry.HasControllers = false;
        if (context is null) return;

        try { context.Unload(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Plugin {Plugin}: unload request failed", entry.Id); }
        // Unloading is best effort: the framework caches component and controller types, which keeps the context
        // alive. The shadow copy is removed when possible and otherwise on the next start of the application.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (shadow is not null) TryDeleteDirectory(shadow);
    }

    // ---- Discovery and state ------------------------------------------------------------------------------------

    private void Discover(Dictionary<string, PluginState> state)
    {
        foreach (var directory in Directory.GetDirectories(RootPath))
        {
            var id = Path.GetFileName(directory);
            if (id.StartsWith('.')) continue;
            var bin = Path.Combine(directory, "bin");
            var binDirectory = Directory.Exists(bin) ? bin : directory;
            if (!Directory.EnumerateFiles(binDirectory, "*.dll").Any()) continue;

            if (_entries.TryGetValue(id, out var entry))
            {
                entry.StaticRoot = FindStaticRoot(entry);
                continue;
            }
            entry = new PluginEntry { Id = id, Directory = directory, BinDirectory = binDirectory };
            entry.StaticRoot = FindStaticRoot(entry);
            if (state.TryGetValue(id, out var saved))
            {
                entry.Enabled = saved.Enabled;
                entry.InstalledAt = saved.InstalledAt;
            }
            else
            {
                entry.InstalledAt = Directory.GetCreationTimeUtc(directory);
            }
            _entries[id] = entry;
        }

        // Folders that disappeared while the application was running (only ones that are not loaded).
        foreach (var gone in _entries.Values.Where(e => !e.IsLoaded && !Directory.Exists(e.BinDirectory)).ToList())
            _entries.Remove(gone.Id);
    }

    private static string? FindStaticRoot(PluginEntry entry)
    {
        var inPlugin = Path.Combine(entry.Directory, "wwwroot");
        if (Directory.Exists(inPlugin)) return inPlugin;
        var inBin = Path.Combine(entry.BinDirectory, "wwwroot");
        return entry.BinDirectory != entry.Directory && Directory.Exists(inBin) ? inBin : null;
    }

    private Dictionary<string, PluginState> LoadState()
    {
        var file = Path.Combine(RootPath, StateFile);
        if (!File.Exists(file)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, PluginState>>(File.ReadAllText(file), StateJson) ?? [];
            return new Dictionary<string, PluginState>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{File} could not be read; every plugin is treated as enabled", file);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveState()
    {
        try
        {
            var state = _entries.Values.ToDictionary(e => e.Id, e => new PluginState { Enabled = e.Enabled, InstalledAt = e.InstalledAt }, StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(RootPath);
            File.WriteAllText(Path.Combine(RootPath, StateFile), JsonSerializer.Serialize(state, StateJson));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The plugin state file could not be written");
        }
    }

    /// <summary>Republishes routes, endpoints, controllers and the snapshot after a change.</summary>
    private void Publish()
    {
        List<PluginEntry> running;
        List<PluginInfo> infos;
        lock (_entries)
        {
            running = _entries.Values.Where(e => e.Status == PluginStatus.Running).ToList();
            infos = _entries.Values.Select(Info).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        _router.Rebuild(running.SelectMany(e => e.Pages));
        _extensions = running.SelectMany(e => e.UiExtensions).ToList();
        _endpointSource.Update(running.SelectMany(e => e.Endpoints));
        _controllerChanges.NotifyChanges();
        _snapshot = infos;
        Changed?.Invoke();
    }

    private PluginInfo Info(PluginEntry e) => new(
        e.Id,
        e.Plugin?.Name is { Length: > 0 } name ? name : e.Id,
        e.Plugin?.Description,
        e.Plugin?.Version,
        e.Plugin?.Author,
        e.Plugin?.Icon is { Length: > 0 } icon ? icon : "box",
        e.Status,
        e.Enabled,
        e.Error,
        e.Directory,
        e.DataDirectory,
        e.Assembly?.GetName().Name,
        e.Plugin?.Id,
        e.InstalledAt,
        e.StartedAt,
        e.Pages,
        e.MenuItems,
        e.EndpointCount,
        e.Container?.ServiceCount ?? 0,
        e.HasControllers,
        e.StaticRoot is not null,
        DirectorySize(e.DataDirectory),
        e.Plugin?.MinimumCmsVersion,
        e.Plugin is null ? PluginCompatibility.Compatible : CmsVersion.CheckPlugin(e.Plugin.MinimumCmsVersion));

    private PluginEntry Get(string id) =>
        _entries.TryGetValue(id?.Trim() ?? string.Empty, out var entry) ? entry : throw new InvalidOperationException($"Plugin '{id}' was not found.");

    private void EnsureEnabled()
    {
        if (!_options.Enabled) throw new InvalidOperationException("Plugins are turned off (DynCms:Plugins:Enabled).");
    }

    private string StaticRequestPath(string id) => "/" + _options.StaticAssetsRequestPath.Trim('/') + "/" + id;

    // ---- Packages and files -------------------------------------------------------------------------------------

    private static async Task ExtractPackageAsync(Stream package, string staging, string stagingBin, CancellationToken ct)
    {
        var extract = Path.Combine(staging, "extract");
        Directory.CreateDirectory(extract);
        if (!package.CanSeek)
        {
            var buffered = new MemoryStream();
            await package.CopyToAsync(buffered, ct);
            buffered.Position = 0;
            package = buffered;
        }
        ZipFile.ExtractToDirectory(package, extract, overwriteFiles: true); // rejects entries that escape the folder

        // A zip made from a folder often wraps everything in one directory.
        var root = extract;
        while (Directory.GetFiles(root).Length == 0 && Directory.GetDirectories(root) is { Length: 1 } only
               && !string.Equals(Path.GetFileName(only[0]), "wwwroot", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(Path.GetFileName(only[0]), "bin", StringComparison.OrdinalIgnoreCase))
        {
            root = only[0];
        }

        var bin = Path.Combine(root, "bin");
        var source = Directory.Exists(bin) ? bin : root;
        foreach (var file in Directory.GetFiles(source)) File.Move(file, Path.Combine(stagingBin, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals("wwwroot", StringComparison.OrdinalIgnoreCase) || name.Equals("data", StringComparison.OrdinalIgnoreCase)) continue;
            MoveDirectory(dir, Path.Combine(stagingBin, name));
        }
        var assets = Path.Combine(root, "wwwroot");
        if (Directory.Exists(assets)) MoveDirectory(assets, Path.Combine(staging, "wwwroot"));
        var settings = Path.Combine(root, "settings.json");
        if (File.Exists(settings)) File.Move(settings, Path.Combine(staging, "settings.json"), overwrite: true);
        if (source != root)
        {
            var settingsInBin = Path.Combine(stagingBin, "settings.json");
            if (File.Exists(settingsInBin) && !File.Exists(Path.Combine(staging, "settings.json"))) File.Move(settingsInBin, Path.Combine(staging, "settings.json"));
        }
        TryDeleteDirectory(extract);
        if (!Directory.EnumerateFiles(stagingBin, "*.dll").Any()) throw new InvalidOperationException("The package contains no .dll file.");
    }

    private void CleanShadowCopies()
    {
        var shadow = Path.Combine(RootPath, ShadowFolder);
        if (Directory.Exists(shadow))
        {
            foreach (var dir in Directory.GetDirectories(shadow)) TryDeleteDirectory(dir);
        }
        var staging = Path.Combine(RootPath, StagingFolder);
        if (Directory.Exists(staging))
        {
            foreach (var dir in Directory.GetDirectories(staging)) TryDeleteDirectory(dir);
        }
    }

    private static void CopyDirectory(string source, string target, string[] skipTopLevelDirectories)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (skipTopLevelDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            CopyDirectory(dir, Path.Combine(target, name), []);
        }
    }

    private static void MoveDirectory(string source, string target)
    {
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try { Directory.Move(source, target); }
        catch (IOException)
        {
            CopyDirectory(source, target, []);
            TryDeleteDirectory(source);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception) { /* locked by a context that did not unload yet; cleaned up on the next start */ }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { }
    }

    private static long DirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception) { return 0; }
    }

    private static bool IsValidId(string id) => id.Length is > 0 and <= 100 && IdPattern().IsMatch(id) && id != "." && id != "..";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex IdPattern();
}
