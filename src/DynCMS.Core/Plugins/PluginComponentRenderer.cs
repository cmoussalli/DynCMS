using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynCMS.Core.Plugins;

/// <summary>
/// Renders a component that uses plugin services to HTML outside of the page's own renderer. Blazor's server-side
/// prerendering builds components from the application's container, which does not know the plugins' services, so a
/// plugin page (or slot component) cannot be created there. This renders it in a scope of the plugin-aware provider
/// instead, which is what makes plugin pages and slots appear in the prerendered HTML (search engines, no-JavaScript
/// visitors, first paint) before the interactive circuit takes over.
/// </summary>
public interface IPluginComponentRenderer
{
    /// <summary>
    /// Renders <paramref name="componentType"/> with <paramref name="parameters"/> to an HTML string. The navigation and
    /// authentication state of <paramref name="caller"/> (the services of the component that asks) are forwarded.
    /// Returns null, and logs, when the component throws.
    /// </summary>
    Task<string?> RenderAsync(IServiceProvider caller, Type componentType, IReadOnlyDictionary<string, object?>? parameters = null);
}

internal sealed class PluginComponentRenderer(IServiceProvider host, PluginContainerRegistry registry, ILogger<PluginComponentRenderer> logger) : IPluginComponentRenderer
{
    public async Task<string?> RenderAsync(IServiceProvider caller, Type componentType, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        try
        {
            var root = registry.RootProvider ?? host;
            await using var scope = root.CreateAsyncScope();
            var services = scope.ServiceProvider;

            // A fresh scope starts with an uninitialised navigation manager and no user; give it the caller's.
            var navigation = caller.GetService<NavigationManager>();
            if (navigation is not null && services.GetService<NavigationManager>() is { } target && !IsInitialized(target))
                Initialize(target, navigation.BaseUri, navigation.Uri);
            if (caller.GetService<AuthenticationStateProvider>() is { } auth
                && services.GetService<AuthenticationStateProvider>() is IHostEnvironmentAuthenticationStateProvider authTarget)
            {
                authTarget.SetAuthenticationState(auth.GetAuthenticationStateAsync());
            }

            await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            var values = parameters is null ? [] : new Dictionary<string, object?>(parameters);
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync(componentType, ParameterView.FromDictionary(values));
                return output.ToHtmlString();
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Prerendering plugin component {Component} failed; it will appear once the page is interactive", componentType.FullName);
            return null;
        }
    }

    private static bool IsInitialized(NavigationManager navigation)
    {
        try
        {
            _ = navigation.Uri;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Initialises a navigation manager the way the framework does for a request: through
    /// <see cref="IHostEnvironmentNavigationManager"/> when it implements it, else through the protected
    /// <c>NavigationManager.Initialize</c> (the circuit's manager does not expose the interface).
    /// </summary>
    private static void Initialize(NavigationManager navigation, string baseUri, string uri)
    {
        try
        {
            if (navigation is IHostEnvironmentNavigationManager host) host.Initialize(baseUri, uri);
        }
        catch (InvalidOperationException) { /* fall through to the protected method */ }

        if (IsInitialized(navigation)) return;
        typeof(NavigationManager)
            .GetMethod("Initialize", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [typeof(string), typeof(string)])
            ?.Invoke(navigation, [baseUri, uri]);
    }
}
