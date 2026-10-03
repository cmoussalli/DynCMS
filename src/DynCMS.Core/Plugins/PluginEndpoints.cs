using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace DynCMS.Core.Plugins;

/// <summary>
/// The endpoint data source that carries the minimal-API endpoints of the running plugins. Added to the
/// application's endpoint sources once by <c>MapDynCmsPlugins()</c>; its change token makes ASP.NET Core rebuild
/// the route table when a plugin starts or stops.
/// </summary>
internal sealed class PluginEndpointDataSource : EndpointDataSource
{
    private readonly object _lock = new();
    private CancellationTokenSource _cts = new();
    private IChangeToken _token;
    private IReadOnlyList<Endpoint> _endpoints = [];

    public PluginEndpointDataSource() => _token = new CancellationChangeToken(_cts.Token);

    public override IReadOnlyList<Endpoint> Endpoints => _endpoints;

    public override IChangeToken GetChangeToken() => _token;

    /// <summary>Replaces the endpoint list with the endpoints of the given sources and signals the change.</summary>
    public void Update(IEnumerable<EndpointDataSource> sources)
    {
        var endpoints = new List<Endpoint>();
        foreach (var source in sources) endpoints.AddRange(source.Endpoints);

        CancellationTokenSource previous;
        lock (_lock)
        {
            _endpoints = endpoints;
            previous = _cts;
            _cts = new CancellationTokenSource();
            _token = new CancellationChangeToken(_cts.Token);
        }
        previous.Cancel();
        previous.Dispose();
    }
}

/// <summary>
/// The <see cref="IEndpointRouteBuilder"/> a plugin maps its endpoints on: <c>MapGet</c>, <c>MapGroup</c> and friends
/// add their data sources to <see cref="DataSources"/>, which the plugin manager then publishes through
/// <see cref="PluginEndpointDataSource"/>. Nothing is mapped on the application directly, so stopping the plugin
/// removes its endpoints again.
/// </summary>
internal sealed class PluginEndpointRouteBuilder(IEndpointRouteBuilder application, IServiceProvider services) : IEndpointRouteBuilder
{
    public IServiceProvider ServiceProvider => services;
    public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
    public IApplicationBuilder CreateApplicationBuilder() => application.CreateApplicationBuilder();
}

/// <summary>Tells MVC that the set of controllers changed (a plugin with API controllers started or stopped).</summary>
internal sealed class PluginActionDescriptorChangeProvider : IActionDescriptorChangeProvider
{
    private CancellationTokenSource _cts = new();

    public IChangeToken GetChangeToken() => new CancellationChangeToken(_cts.Token);

    public void NotifyChanges()
    {
        var previous = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        previous.Cancel();
        previous.Dispose();
    }
}
