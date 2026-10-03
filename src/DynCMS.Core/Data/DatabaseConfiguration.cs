using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace DynCMS.Core.Data;

/// <summary>Database engines DynCMS can run on.</summary>
public enum DatabaseProvider
{
    Sqlite,
    SqlServer
}

/// <summary>The databases DynCMS talks to. Each can be configured on its own.</summary>
public enum DatabaseRole
{
    /// <summary>Content, media records, templates, languages, dictionary, settings, users and sessions.</summary>
    Primary,

    /// <summary>Visitor analytics: page views and the geolocation cache. Shares the primary database unless configured separately.</summary>
    Analytics
}

/// <summary>
/// The database settings DynCMS persists in <c>dyncms.database.json</c> in the application root.
/// Content and identity tables live in the same (primary) database; analytics can be kept in its own
/// database through <see cref="Analytics"/>.
/// </summary>
public sealed class DatabaseConfiguration
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    public SqliteSettings? Sqlite { get; set; }
    public SqlServerSettings? SqlServer { get; set; }

    /// <summary>
    /// Where visitor analytics is stored when it is kept apart from the content: a database of either provider.
    /// <c>null</c> (the default) keeps the analytics tables in the primary database.
    /// </summary>
    public DatabaseConfiguration? Analytics { get; set; }

    /// <summary>Connection string for the configured provider. Relative SQLite paths resolve against <paramref name="basePath"/>.</summary>
    public string BuildConnectionString(string basePath) => Provider switch
    {
        DatabaseProvider.Sqlite => (Sqlite ?? throw Missing("Sqlite")).BuildConnectionString(basePath),
        DatabaseProvider.SqlServer => (SqlServer ?? throw Missing("SqlServer")).BuildConnectionString(),
        _ => throw new InvalidOperationException($"Unknown database provider '{Provider}'.")
    };

    /// <summary>Human readable description of the target, safe to log (no password).</summary>
    public string Describe(string basePath) => Provider switch
    {
        DatabaseProvider.Sqlite => $"SQLite {Sqlite?.ResolvePath(basePath)}",
        DatabaseProvider.SqlServer => $"SQL Server {SqlServer?.Server},{SqlServer?.Port} database '{SqlServer?.Database}'",
        _ => Provider.ToString()
    };

    public void Validate()
    {
        switch (Provider)
        {
            case DatabaseProvider.Sqlite:
                if (string.IsNullOrWhiteSpace(Sqlite?.DatabasePath)) throw new InvalidOperationException("SQLite: the database file path is required.");
                break;
            case DatabaseProvider.SqlServer:
                if (SqlServer is null) throw Missing("SqlServer");
                SqlServer.Validate();
                break;
        }

        if (Analytics is not null)
        {
            if (Analytics.Analytics is not null) throw new InvalidOperationException("The analytics database configuration cannot itself have an analytics database.");
            Analytics.Validate();
        }
    }

    /// <summary>True when both configurations point at the same database (same file, or same server and database name).</summary>
    public bool SameTargetAs(DatabaseConfiguration other, string basePath)
    {
        if (Provider != other.Provider) return false;
        if (Provider == DatabaseProvider.Sqlite)
            return Sqlite is not null && other.Sqlite is not null &&
                   string.Equals(Sqlite.ResolvePath(basePath), other.Sqlite.ResolvePath(basePath), StringComparison.OrdinalIgnoreCase);
        return SqlServer is not null && other.SqlServer is not null &&
               string.Equals(SqlServer.Server.Trim(), other.SqlServer.Server.Trim(), StringComparison.OrdinalIgnoreCase) &&
               SqlServer.Port == other.SqlServer.Port &&
               string.Equals(SqlServer.Database.Trim(), other.SqlServer.Database.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A copy of the primary settings with another analytics configuration (or none).</summary>
    public DatabaseConfiguration WithAnalytics(DatabaseConfiguration? analytics) => new()
    {
        Provider = Provider,
        Sqlite = Sqlite,
        SqlServer = SqlServer,
        Analytics = analytics is null ? null : new DatabaseConfiguration { Provider = analytics.Provider, Sqlite = analytics.Sqlite, SqlServer = analytics.SqlServer }
    };

    private static InvalidOperationException Missing(string section) =>
        new($"The database configuration selects provider '{section}' but has no '{section}' section.");
}

public sealed class SqliteSettings
{
    /// <summary>Database file. Relative paths resolve against the application root.</summary>
    public string DatabasePath { get; set; } = Path.Combine("App_Data", "dyncms.db");

    public string ResolvePath(string basePath) => Path.GetFullPath(Path.Combine(basePath, DatabasePath.Trim()));

    public string BuildConnectionString(string basePath) =>
        new SqliteConnectionStringBuilder { DataSource = ResolvePath(basePath) }.ToString();
}

public sealed class SqlServerSettings
{
    public string Server { get; set; } = "localhost";
    public int Port { get; set; } = 1433;
    public string Database { get; set; } = "DynCMS";
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool TrustServerCertificate { get; set; } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Server)) throw new InvalidOperationException("SQL Server: the server name is required.");
        if (Port is < 1 or > 65535) throw new InvalidOperationException("SQL Server: the port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(Database)) throw new InvalidOperationException("SQL Server: the database name is required.");
        if (!IsValidDatabaseName(Database.Trim())) throw new InvalidOperationException("SQL Server: the database name may only contain letters, digits, '_' and '-'.");
        if (string.IsNullOrWhiteSpace(UserName)) throw new InvalidOperationException("SQL Server: the user name is required.");
    }

    /// <summary>Conservative identifier rule so the name can be embedded in <c>CREATE DATABASE [..]</c> safely.</summary>
    public static bool IsValidDatabaseName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    public string BuildConnectionString(string? databaseOverride = null) => new SqlConnectionStringBuilder
    {
        DataSource = $"{Server.Trim()},{Port}",
        InitialCatalog = databaseOverride ?? Database.Trim(),
        UserID = UserName.Trim(),
        Password = Password,
        TrustServerCertificate = TrustServerCertificate,
        Encrypt = SqlConnectionEncryptOption.Mandatory,
        ConnectTimeout = 15,
        ApplicationName = "DynCMS",
        MultipleActiveResultSets = true
    }.ToString();

    /// <summary>Connection string to the <c>master</c> database, used to create a new database.</summary>
    public string BuildMasterConnectionString() => BuildConnectionString("master");
}

/// <summary>Reads and writes the database configuration file in the application root.</summary>
public sealed class DatabaseConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();
    private DatabaseConfiguration? _current;

    public DatabaseConfigurationStore(DynCmsPaths paths)
    {
        FilePath = paths.DatabaseConfigPath;
        BasePath = paths.BasePath;
    }

    /// <summary>Absolute path of <c>dyncms.database.json</c>.</summary>
    public string FilePath { get; }

    /// <summary>Directory that relative SQLite paths resolve against (the application root).</summary>
    public string BasePath { get; }

    public bool Exists => File.Exists(FilePath);

    /// <summary>The configuration currently in use, or <c>null</c> when the application still needs to be set up.</summary>
    public DatabaseConfiguration? Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>The database a role uses: the primary configuration, or the analytics one when configured (else the primary).</summary>
    public DatabaseConfiguration? Resolve(DatabaseRole role)
    {
        var current = Current;
        return role == DatabaseRole.Analytics ? current?.Analytics ?? current : current;
    }

    /// <summary>True when analytics has its own database rather than sharing the primary one.</summary>
    public bool IsAnalyticsSeparate => Current?.Analytics is not null;

    /// <summary>Loads the file if it exists and makes it current. Returns <c>null</c> when there is no file.</summary>
    public DatabaseConfiguration? Load()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath)) return _current = null;

            var config = JsonSerializer.Deserialize<DatabaseConfiguration>(File.ReadAllText(FilePath), JsonOptions)
                         ?? throw new InvalidOperationException($"'{FilePath}' does not contain a database configuration.");
            config.Validate();
            Normalize(config);
            return _current = config;
        }
    }

    /// <summary>An analytics entry that points at the primary database is the same as no entry.</summary>
    private void Normalize(DatabaseConfiguration config)
    {
        if (config.Analytics is not null && config.Analytics.SameTargetAs(config, BasePath)) config.Analytics = null;
    }

    /// <summary>Writes the file and makes the configuration current.</summary>
    public void Save(DatabaseConfiguration config)
    {
        config.Validate();
        Normalize(config);
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(config, JsonOptions));
            _current = config;
        }
    }

    /// <summary>Removes the file (used when initialisation fails right after setup so the setup page comes back).</summary>
    public void Delete()
    {
        lock (_lock)
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            _current = null;
        }
    }
}
