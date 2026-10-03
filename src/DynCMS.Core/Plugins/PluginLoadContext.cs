using System.Reflection;
using System.Runtime.Loader;

namespace DynCMS.Core.Plugins;

/// <summary>
/// The load context of one plugin. Collectible, so a stopped plugin can (best effort) be unloaded. Assemblies the
/// application already ships — the framework, DynCMS, Entity Framework, anything on the trusted platform assembly
/// list — are never loaded a second time: the plugin shares them with the host, which is what makes the host's
/// types (components, services, DbContext) the same types inside the plugin. Only the plugin's own assemblies and
/// its private dependencies are loaded from the plugin folder.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly Lazy<HashSet<string>> HostAssemblyNames = new(() =>
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                names.Add(Path.GetFileNameWithoutExtension(path));
        }
        return names;
    });

    private readonly string _directory;
    private readonly AssemblyDependencyResolver? _resolver;

    public PluginLoadContext(string name, string directory, string? mainAssemblyPath)
        : base(name, isCollectible: true)
    {
        _directory = directory;
        if (mainAssemblyPath is not null)
        {
            try { _resolver = new AssemblyDependencyResolver(mainAssemblyPath); }
            catch (Exception) { _resolver = null; } // a broken .deps.json falls back to directory probing
        }
    }

    /// <summary>True when the application itself provides the assembly, so the plugin must share it rather than load its own copy.</summary>
    public static bool IsHostAssembly(string? simpleName)
    {
        if (string.IsNullOrEmpty(simpleName)) return false;
        if (HostAssemblyNames.Value.Contains(simpleName)) return true;
        foreach (var assembly in Default.Assemblies)
        {
            if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsHostAssembly(assemblyName.Name)) return null; // defer to the default context

        var path = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            var candidate = Path.Combine(_directory, assemblyName.Name + ".dll");
            if (File.Exists(candidate)) path = candidate;
        }
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
