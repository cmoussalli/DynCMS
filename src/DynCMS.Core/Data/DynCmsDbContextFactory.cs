using Microsoft.EntityFrameworkCore;

namespace DynCMS.Core.Data;

/// <summary>Thrown when the database is used before the application has been set up.</summary>
public sealed class DynCmsNotConfiguredException() : InvalidOperationException(
    "DynCMS has no database configuration yet. Open /setup to configure the database.");

/// <summary>
/// Creates <see cref="DynCmsDbContext"/> instances for whatever database is currently configured.
/// Options are rebuilt when the configuration changes (for example right after the setup page saves it),
/// so the application does not have to restart.
/// </summary>
internal sealed class DynCmsDbContextFactory(DatabaseConfigurationStore store) : IDbContextFactory<DynCmsDbContext>
{
    private DatabaseConfiguration? _optionsFor;
    private DbContextOptions<DynCmsDbContext>? _options;

    public DynCmsDbContext CreateDbContext() => new(GetOptions());

    private DbContextOptions<DynCmsDbContext> GetOptions()
    {
        var config = store.Current ?? throw new DynCmsNotConfiguredException();
        var cached = _options;
        if (cached is not null && ReferenceEquals(_optionsFor, config)) return cached;

        var builder = new DbContextOptionsBuilder<DynCmsDbContext>();
        Apply(builder, config, store.BasePath);
        _optionsFor = config;
        return _options = builder.Options;
    }

    /// <summary>Configures a builder for the given database. Shared with the setup service for connection tests.</summary>
    public static void Apply(DbContextOptionsBuilder builder, DatabaseConfiguration config, string basePath)
    {
        var connectionString = config.BuildConnectionString(basePath);
        switch (config.Provider)
        {
            case DatabaseProvider.Sqlite:
                builder.UseSqlite(connectionString);
                break;
            case DatabaseProvider.SqlServer:
                builder.UseSqlServer(connectionString);
                break;
            default:
                throw new InvalidOperationException($"Unknown database provider '{config.Provider}'.");
        }
    }
}
