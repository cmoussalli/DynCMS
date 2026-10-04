using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynCMS.Core.Plugins;

/// <summary>Answers <see cref="IPluginUiExtensions"/> from the running plugins.</summary>
internal sealed class PluginUiExtensionsService : IPluginUiExtensions, IDisposable
{
    private readonly PluginManager _manager;

    public PluginUiExtensionsService(PluginManager manager)
    {
        _manager = manager;
        _manager.Changed += OnChanged;
    }

    public event Action? Changed;

    public IReadOnlyList<PluginUiExtension> For(string slot) =>
        _manager.Extensions
            .Where(x => string.Equals(x.Slot, slot, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Order)
            .ToList();

    private void OnChanged() => Changed?.Invoke();

    public void Dispose() => _manager.Changed -= OnChanged;
}

/// <summary>Publishes content and media changes to <see cref="ICmsEventHandler"/>s (the site's and the running plugins').</summary>
public interface ICmsEventDispatcher
{
    Task PublishAsync(ContentEvent e, CancellationToken ct = default);
    Task PublishAsync(MediaEvent e, CancellationToken ct = default);
}

internal sealed class CmsEventDispatcher(IServiceProvider host, PluginContainerRegistry registry, ILogger<CmsEventDispatcher> logger) : ICmsEventDispatcher
{
    public Task PublishAsync(ContentEvent e, CancellationToken ct = default) =>
        DispatchAsync("content", h => h.OnContentAsync(e, ct));

    public Task PublishAsync(MediaEvent e, CancellationToken ct = default) =>
        DispatchAsync("media", h => h.OnMediaAsync(e, ct));

    private async Task DispatchAsync(string what, Func<ICmsEventHandler, Task> call)
    {
        foreach (var handler in Handlers())
        {
            try
            {
                await call(handler);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "An event handler ({Handler}) failed on a {What} event", handler.GetType().FullName, what);
            }
        }
    }

    private List<ICmsEventHandler> Handlers()
    {
        var result = new List<ICmsEventHandler>();
        // The application's own handlers first (registered in the site's startup code), then each running plugin's.
        result.AddRange(host.GetServices<ICmsEventHandler>());
        foreach (var container in registry.Containers)
        {
            try { result.AddRange(container.GetOwn<ICmsEventHandler>()); }
            catch (Exception ex) { logger.LogWarning(ex, "Plugin {Plugin}: its event handlers could not be created", container.PluginId); }
        }
        return result;
    }
}
