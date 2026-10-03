using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DynCMS.Core.Plugins;

/// <summary>
/// Makes services that plugins register at runtime resolvable through the application's normal dependency
/// injection. Install with <c>builder.ConfigureContainer(new PluginServiceProviderFactory(...))</c> (done by
/// <c>AddDynCmsHost</c>): the application container is built as usual and wrapped in a provider that, whenever the
/// application container has no answer, asks the running plugins. Request scopes and Blazor circuit scopes created
/// through the wrapper carry a per-plugin scope alongside the application scope, so scoped plugin services behave
/// like scoped application services.
/// <para>Resolution order: a type defined in a plugin (or generic over one, e.g. <c>IOptions&lt;PluginOptions&gt;</c>)
/// is answered by the plugin first, everything else by the application first. <c>IEnumerable&lt;T&gt;</c> merges both.</para>
/// </summary>
public sealed class PluginServiceProviderFactory(ServiceProviderOptions? options = null) : IServiceProviderFactory<IServiceCollection>
{
    public IServiceCollection CreateBuilder(IServiceCollection services) => services;

    public IServiceProvider CreateServiceProvider(IServiceCollection services)
    {
        var registry = new PluginContainerRegistry();
        services.Replace(ServiceDescriptor.Singleton(registry));
        RedirectRequestScopes(services, registry);
        registry.CircuitScopesRedirected = RedirectCircuitScopes(services, registry);

        var host = options is null ? services.BuildServiceProvider() : services.BuildServiceProvider(options);
        var wrapper = new PluginHostServiceProvider(host, registry, options?.ValidateScopes ?? false);
        registry.Attach(wrapper);
        return wrapper;
    }

    /// <summary>
    /// <c>HttpContext.RequestServices</c> is a scope the <see cref="Microsoft.AspNetCore.Http.DefaultHttpContextFactory"/>
    /// creates with the <c>IServiceScopeFactory</c> of the provider it was constructed with; built by the application
    /// container that is the container's own factory. Constructing it against the wrapper instead makes every request
    /// scope a wrapper scope, so plugin services bind in minimal-API handlers and controllers.
    /// </summary>
    private static void RedirectRequestScopes(IServiceCollection services, PluginContainerRegistry registry)
    {
        services.Replace(ServiceDescriptor.Singleton<Microsoft.AspNetCore.Http.IHttpContextFactory>(sp =>
            new Microsoft.AspNetCore.Http.DefaultHttpContextFactory(registry.RootProvider ?? sp)));
    }

    /// <summary>
    /// Blazor Server creates one scope per circuit through the <c>IServiceScopeFactory</c> its (internal) circuit
    /// factory received from the application container, which is the container's own factory, not the wrapper. This
    /// re-registers the circuit factory so it is constructed against the wrapper and therefore creates wrapper scopes;
    /// that is what lets a plugin component <c>@inject</c> the plugin's services. Returns false (and leaves everything
    /// as is) when the internal types are not where this version expects them.
    /// </summary>
    private static bool RedirectCircuitScopes(IServiceCollection services, PluginContainerRegistry registry)
    {
        const string assembly = "Microsoft.AspNetCore.Components.Server";
        var contract = Type.GetType($"Microsoft.AspNetCore.Components.Server.Circuits.ICircuitFactory, {assembly}", throwOnError: false);
        var implementation = Type.GetType($"Microsoft.AspNetCore.Components.Server.Circuits.CircuitFactory, {assembly}", throwOnError: false);
        if (contract is null || implementation is null) return false;
        if (!services.Any(d => d.ServiceType == contract)) return false;
        if (!implementation.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IServiceScopeFactory)))) return false;

        services.Replace(ServiceDescriptor.Singleton(contract, sp => ActivatorUtilities.CreateInstance(registry.RootProvider ?? sp, implementation)));
        return true;
    }
}

/// <summary>The set of running plugin containers the wrapper consults. One per application; registered by <c>AddDynCms</c>.</summary>
public sealed class PluginContainerRegistry
{
    private volatile PluginContainer[] _containers = [];

    internal PluginContainer[] Containers => _containers;

    /// <summary>True once <see cref="PluginServiceProviderFactory"/> wrapped the application container.</summary>
    public bool IsAttached => RootProvider is not null;

    /// <summary>True when Blazor circuit scopes are created through the wrapper, so plugin components can inject plugin services.</summary>
    public bool CircuitScopesRedirected { get; internal set; }

    /// <summary>The wrapper, i.e. the provider that sees plugin services. Null when the factory is not installed.</summary>
    public IServiceProvider? RootProvider { get; private set; }

    internal void Attach(IServiceProvider root) => RootProvider = root;

    internal void Add(PluginContainer container)
    {
        lock (this) _containers = [.. _containers, container];
    }

    internal void Remove(PluginContainer container)
    {
        lock (this) _containers = _containers.Where(c => c != container).ToArray();
    }
}

/// <summary>Per-scope state for plugin services: the scoped instances and the disposables created in the scope.</summary>
internal sealed class PluginScopeState(IServiceProvider provider, bool isRoot) : IDisposable, IAsyncDisposable
{
    private readonly Dictionary<(PluginContainer, ServiceDescriptor, Type), object> _scoped = [];
    private readonly List<object> _disposables = [];

    public IServiceProvider Provider { get; } = provider;
    public bool IsRoot { get; } = isRoot;

    public object GetOrAdd(PluginContainer container, ServiceDescriptor descriptor, Type requested, Func<object> create)
    {
        lock (_scoped)
        {
            if (_scoped.TryGetValue((container, descriptor, requested), out var existing)) return existing;
        }
        var created = create();
        lock (_scoped)
        {
            if (_scoped.TryGetValue((container, descriptor, requested), out var raced))
            {
                (created as IDisposable)?.Dispose();
                return raced;
            }
            _scoped[(container, descriptor, requested)] = created;
            if (created is IDisposable or IAsyncDisposable) _disposables.Add(created);
            return created;
        }
    }

    public void Track(object instance)
    {
        if (instance is IDisposable or IAsyncDisposable)
        {
            lock (_scoped) _disposables.Add(instance);
        }
    }

    /// <summary>Drops (and disposes) everything a container created in this scope; used when a plugin stops.</summary>
    public void Release(PluginContainer container)
    {
        List<object> gone = [];
        lock (_scoped)
        {
            foreach (var key in _scoped.Keys.Where(k => k.Item1 == container).ToList())
            {
                gone.Add(_scoped[key]);
                _scoped.Remove(key);
            }
            _disposables.RemoveAll(gone.Contains);
        }
        foreach (var o in gone) DisposeQuietly(o);
    }

    public void Dispose()
    {
        object[] items;
        lock (_scoped)
        {
            items = [.. _disposables];
            _disposables.Clear();
            _scoped.Clear();
        }
        for (var i = items.Length - 1; i >= 0; i--) DisposeQuietly(items[i]);
    }

    public async ValueTask DisposeAsync()
    {
        object[] items;
        lock (_scoped)
        {
            items = [.. _disposables];
            _disposables.Clear();
            _scoped.Clear();
        }
        for (var i = items.Length - 1; i >= 0; i--)
        {
            try
            {
                if (items[i] is IAsyncDisposable ad) await ad.DisposeAsync();
                else (items[i] as IDisposable)?.Dispose();
            }
            catch { /* a failing Dispose must not take the scope down */ }
        }
    }

    private static void DisposeQuietly(object o)
    {
        try
        {
            if (o is IDisposable d) d.Dispose();
            else if (o is IAsyncDisposable ad) ad.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch { }
    }
}

/// <summary>
/// The services one running plugin registered, resolved on demand on top of the application provider. Singletons
/// are cached here (and disposed when the plugin stops); scoped instances live in the <see cref="PluginScopeState"/>
/// of the resolving scope; implementation types are constructed with <see cref="ActivatorUtilities"/> against the
/// wrapper provider, so constructor parameters may be application or plugin services.
/// </summary>
internal sealed class PluginContainer : IDisposable
{
    private readonly Dictionary<Type, List<ServiceDescriptor>> _closed = [];
    private readonly Dictionary<Type, List<ServiceDescriptor>> _open = [];
    private readonly ConcurrentDictionary<(ServiceDescriptor, Type), Lazy<object>> _singletons = new();
    private readonly PluginScopeState _root;
    private readonly bool _validateScopes;

    public PluginContainer(string pluginId, AssemblyLoadContext loadContext, IServiceCollection services, IServiceProvider rootProvider, bool validateScopes)
    {
        PluginId = pluginId;
        LoadContext = loadContext;
        _validateScopes = validateScopes;
        _root = new PluginScopeState(rootProvider, isRoot: true);
        foreach (var d in services)
        {
            if (d.IsKeyedService) continue; // keyed services are not supported inside plugins
            var map = d.ServiceType.IsGenericTypeDefinition ? _open : _closed;
            if (!map.TryGetValue(d.ServiceType, out var list)) map[d.ServiceType] = list = [];
            list.Add(d);
        }
    }

    public string PluginId { get; }
    public AssemblyLoadContext LoadContext { get; }
    public int ServiceCount => _closed.Values.Sum(l => l.Count) + _open.Values.Sum(l => l.Count);

    public bool Owns(Type type)
    {
        if (AssemblyLoadContext.GetLoadContext(type.Assembly) == LoadContext) return true;
        if (type.IsGenericType)
        {
            foreach (var arg in type.GenericTypeArguments)
                if (Owns(arg)) return true;
        }
        return false;
    }

    public bool IsService(Type type)
    {
        if (_closed.ContainsKey(type)) return true;
        if (type.IsConstructedGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(IEnumerable<>)) return IsService(type.GenericTypeArguments[0]);
            if (_open.ContainsKey(def)) return true;
        }
        return false;
    }

    /// <summary>The last matching registration (the same rule as the built-in container), or null.</summary>
    public object? Resolve(Type type, PluginScopeState scope)
    {
        var descriptor = Descriptors(type).LastOrDefault();
        return descriptor is null ? null : Activate(descriptor, type, scope);
    }

    /// <summary>Every registration of <paramref name="itemType"/>, in registration order.</summary>
    public List<object> ResolveAll(Type itemType, PluginScopeState scope)
    {
        var result = new List<object>();
        foreach (var d in Descriptors(itemType)) result.Add(Activate(d, itemType, scope));
        return result;
    }

    /// <summary>Instances of <typeparamref name="T"/> the plugin registered itself (used for its hosted services).</summary>
    public List<T> GetOwn<T>() => ResolveAll(typeof(T), _root).Cast<T>().ToList();

    private IEnumerable<ServiceDescriptor> Descriptors(Type type)
    {
        if (_closed.TryGetValue(type, out var closed)) foreach (var d in closed) yield return d;
        if (type.IsConstructedGenericType && _open.TryGetValue(type.GetGenericTypeDefinition(), out var open))
            foreach (var d in open) yield return d;
    }

    private object Activate(ServiceDescriptor d, Type requested, PluginScopeState scope)
    {
        switch (d.Lifetime)
        {
            case ServiceLifetime.Singleton:
                return _singletons.GetOrAdd((d, requested), key => new Lazy<object>(() =>
                {
                    var instance = Create(key.Item1, key.Item2, _root);
                    if (!ReferenceEquals(instance, key.Item1.ImplementationInstance)) _root.Track(instance);
                    return instance;
                })).Value;

            case ServiceLifetime.Scoped:
                if (scope.IsRoot && _validateScopes)
                    throw new InvalidOperationException($"Cannot resolve scoped plugin service '{requested}' from the root provider. Create a scope first.");
                return scope.GetOrAdd(this, d, requested, () => Create(d, requested, scope));

            default:
                var transient = Create(d, requested, scope);
                scope.Track(transient);
                return transient;
        }
    }

    private static object Create(ServiceDescriptor d, Type requested, PluginScopeState scope)
    {
        if (d.ImplementationInstance is not null) return d.ImplementationInstance;
        if (d.ImplementationFactory is not null) return d.ImplementationFactory(scope.Provider);

        var implementation = d.ImplementationType ?? throw new InvalidOperationException($"Registration of '{d.ServiceType}' has no implementation.");
        if (implementation.IsGenericTypeDefinition) implementation = implementation.MakeGenericType(requested.GenericTypeArguments);
        return ActivatorUtilities.CreateInstance(scope.Provider, implementation);
    }

    /// <summary>Disposes the singletons (and root-created transients) of this plugin.</summary>
    public void Dispose()
    {
        _root.Dispose();
        _singletons.Clear();
    }
}

/// <summary>Shared resolution logic for the root wrapper and its scopes.</summary>
internal static class PluginResolver
{
    public static object? Resolve(Type type, IServiceProvider host, PluginHostServiceProvider root, PluginScopeState scope, PluginContainerRegistry registry)
    {
        if (type == typeof(IServiceProvider)) return scope.Provider;
        if (type == typeof(IServiceScopeFactory) || type == typeof(IServiceProviderIsService) || type == typeof(IServiceProviderIsKeyedService)) return root;

        var containers = registry.Containers;
        if (containers.Length == 0) return host.GetService(type);

        var pluginOwned = IsPluginOwned(type, containers, out var owner);

        if (type.IsConstructedGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            var item = type.GenericTypeArguments[0];
            List<object>? fromPlugins = null;
            foreach (var c in containers)
            {
                var part = c.ResolveAll(item, scope);
                if (part.Count == 0) continue;
                (fromPlugins ??= []).AddRange(part);
            }
            var fromHost = pluginOwned ? null : host.GetService(type) as IEnumerable;
            if (fromPlugins is null) return fromHost ?? Array.CreateInstance(item, 0);
            var all = new List<object>();
            if (fromHost is not null) foreach (var o in fromHost) all.Add(o);
            all.AddRange(fromPlugins);
            var array = Array.CreateInstance(item, all.Count);
            for (var i = 0; i < all.Count; i++) array.SetValue(all[i], i);
            return array;
        }

        if (pluginOwned)
        {
            if (owner is not null && owner.Resolve(type, scope) is { } own) return own;
            foreach (var c in containers)
            {
                if (c == owner) continue;
                if (c.Resolve(type, scope) is { } found) return found;
            }
            return host.GetService(type);
        }

        if (host.GetService(type) is { } fromHostFirst) return fromHostFirst;
        foreach (var c in containers)
        {
            if (c.Resolve(type, scope) is { } found) return found;
        }
        return null;
    }

    public static bool IsService(Type type, IServiceProvider host, PluginContainerRegistry registry)
    {
        if (host.GetService<IServiceProviderIsService>()?.IsService(type) == true) return true;
        foreach (var c in registry.Containers)
        {
            if (c.IsService(type)) return true;
        }
        return false;
    }

    private static bool IsPluginOwned(Type type, PluginContainer[] containers, out PluginContainer? owner)
    {
        foreach (var c in containers)
        {
            if (c.Owns(type))
            {
                owner = c;
                return true;
            }
        }
        owner = null;
        return false;
    }
}

/// <summary>The root provider handed to the host: the application container plus the running plugins.</summary>
internal sealed class PluginHostServiceProvider :
    IServiceProvider, ISupportRequiredService, IKeyedServiceProvider, IServiceScopeFactory,
    IServiceProviderIsService, IServiceProviderIsKeyedService, IDisposable, IAsyncDisposable
{
    private static readonly ConditionalWeakTable<IServiceProvider, PluginHostScope> ScopesByHostScope = [];

    private readonly ServiceProvider _host;
    private readonly PluginContainerRegistry _registry;
    private readonly PluginScopeState _rootState;
    private readonly IServiceScopeFactory _hostScopes;

    public PluginHostServiceProvider(ServiceProvider host, PluginContainerRegistry registry, bool validateScopes)
    {
        _host = host;
        _registry = registry;
        ValidateScopes = validateScopes;
        _rootState = new PluginScopeState(this, isRoot: true);
        _hostScopes = host.GetRequiredService<IServiceScopeFactory>();
    }

    public bool ValidateScopes { get; }
    internal PluginContainerRegistry Registry => _registry;
    internal ServiceProvider Host => _host;

    /// <summary>The wrapper scope that owns <paramref name="hostScopeProvider"/>, when the scope was created through the wrapper.</summary>
    public static IServiceProvider? FindWrapper(IServiceProvider hostScopeProvider) =>
        ScopesByHostScope.TryGetValue(hostScopeProvider, out var scope) ? scope : null;

    public object? GetService(Type serviceType) => PluginResolver.Resolve(serviceType, _host, this, _rootState, _registry);

    public object GetRequiredService(Type serviceType) =>
        GetService(serviceType) ?? throw new InvalidOperationException($"No service for type '{serviceType}' has been registered.");

    public object? GetKeyedService(Type serviceType, object? serviceKey) => _host.GetKeyedService(serviceType, serviceKey);

    public object GetRequiredKeyedService(Type serviceType, object? serviceKey) => _host.GetRequiredKeyedService(serviceType, serviceKey);

    public IServiceScope CreateScope()
    {
        var hostScope = _hostScopes.CreateScope();
        var scope = new PluginHostScope(this, hostScope);
        ScopesByHostScope.AddOrUpdate(hostScope.ServiceProvider, scope);
        return scope;
    }

    public bool IsService(Type serviceType) => PluginResolver.IsService(serviceType, _host, _registry);

    public bool IsKeyedService(Type serviceType, object? serviceKey) =>
        _host.GetService<IServiceProviderIsKeyedService>()?.IsKeyedService(serviceType, serviceKey) == true;

    /// <summary>Called when a plugin stops: drops what it created at root level.</summary>
    internal void Release(PluginContainer container) => _rootState.Release(container);

    public void Dispose()
    {
        _rootState.Dispose();
        _host.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _rootState.DisposeAsync();
        await _host.DisposeAsync();
    }
}

/// <summary>A scope of the wrapper: an application scope plus the plugin instances created in it.</summary>
internal sealed class PluginHostScope : IServiceScope, IAsyncDisposable, IServiceProvider, ISupportRequiredService, IKeyedServiceProvider
{
    private readonly PluginHostServiceProvider _root;
    private readonly IServiceScope _hostScope;
    private readonly PluginScopeState _state;

    public PluginHostScope(PluginHostServiceProvider root, IServiceScope hostScope)
    {
        _root = root;
        _hostScope = hostScope;
        _state = new PluginScopeState(this, isRoot: false);
    }

    public IServiceProvider ServiceProvider => this;

    public object? GetService(Type serviceType) => PluginResolver.Resolve(serviceType, _hostScope.ServiceProvider, _root, _state, _root.Registry);

    public object GetRequiredService(Type serviceType) =>
        GetService(serviceType) ?? throw new InvalidOperationException($"No service for type '{serviceType}' has been registered.");

    public object? GetKeyedService(Type serviceType, object? serviceKey) =>
        (_hostScope.ServiceProvider as IKeyedServiceProvider)?.GetKeyedService(serviceType, serviceKey);

    public object GetRequiredKeyedService(Type serviceType, object? serviceKey) =>
        _hostScope.ServiceProvider.GetRequiredKeyedService(serviceType, serviceKey);

    public void Dispose()
    {
        _state.Dispose();
        _hostScope.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _state.DisposeAsync();
        if (_hostScope is IAsyncDisposable ad) await ad.DisposeAsync();
        else _hostScope.Dispose();
    }
}
