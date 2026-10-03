using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DynCMS.Core;

/// <summary>Resolved absolute paths derived from <see cref="DynCmsOptions"/>.</summary>
public sealed class DynCmsPaths
{
    public DynCmsPaths(IOptions<DynCmsOptions> options, IHostEnvironment? environment = null)
    {
        var o = options.Value;
        BasePath = o.BasePath ?? environment?.ContentRootPath ?? AppContext.BaseDirectory;
        DatabaseConfigPath = Path.GetFullPath(Path.Combine(BasePath, o.DatabaseConfigFile));
        MediaRootPath = Path.GetFullPath(Path.Combine(BasePath, o.MediaRootPath));
        BackupRootPath = Path.GetFullPath(Path.Combine(BasePath, o.BackupRootPath));
        PluginsRootPath = Path.GetFullPath(Path.Combine(BasePath, o.Plugins.RootPath));
        MediaRequestPath = "/" + o.MediaRequestPath.Trim('/');
    }

    /// <summary>The application root; relative paths (including SQLite database files) resolve against it.</summary>
    public string BasePath { get; }

    /// <summary>Absolute path of <c>dyncms.database.json</c>.</summary>
    public string DatabaseConfigPath { get; }

    public string MediaRootPath { get; }
    public string MediaRequestPath { get; }

    /// <summary>Folder that holds database backups created from the back office.</summary>
    public string BackupRootPath { get; }

    /// <summary>Folder that holds one sub-folder per plugin (binaries, static files, private data).</summary>
    public string PluginsRootPath { get; }
}
