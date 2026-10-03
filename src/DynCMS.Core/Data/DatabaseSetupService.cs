using DynCMS.Core.Analytics;
using DynCMS.Core.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DynCMS.Core.Data;

public enum DatabaseSetupMode
{
    /// <summary>Create a fresh database (or a fresh schema in an empty database).</summary>
    NewDatabase,

    /// <summary>Connect to a database that already exists; the DynCMS schema is created there if missing.</summary>
    ExistingDatabase
}

/// <summary>Everything the setup page collects. Converted to a <see cref="DatabaseConfiguration"/> when saved.</summary>
public sealed class DatabaseSetupRequest
{
    public DatabaseSetupMode Mode { get; set; } = DatabaseSetupMode.NewDatabase;
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    // SQLite
    public string SqlitePath { get; set; } = Path.Combine("App_Data", "dyncms.db");

    // SQL Server
    public string Server { get; set; } = "localhost";
    public int Port { get; set; } = 1433;
    public string Database { get; set; } = "DynCMS";
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool TrustServerCertificate { get; set; } = true;

    /// <summary>
    /// New database mode only: connect to <c>master</c> and run <c>CREATE DATABASE</c> when the database does not
    /// exist yet. Turn off when a DBA has already created an empty database for you.
    /// </summary>
    public bool CreateDatabase { get; set; } = true;

    /// <summary>
    /// What the database starts with when it has no content yet: just a home page, or the demo blog. Ignored for a
    /// database that already holds DynCMS content.
    /// </summary>
    public StarterContent StarterContent { get; set; } = StarterContent.EmptySite;

    public DatabaseConfiguration ToConfiguration() => Provider switch
    {
        DatabaseProvider.Sqlite => new DatabaseConfiguration
        {
            Provider = DatabaseProvider.Sqlite,
            Sqlite = new SqliteSettings { DatabasePath = SqlitePath.Trim() }
        },
        DatabaseProvider.SqlServer => new DatabaseConfiguration
        {
            Provider = DatabaseProvider.SqlServer,
            SqlServer = new SqlServerSettings
            {
                Server = Server.Trim(),
                Port = Port,
                Database = Database.Trim(),
                UserName = UserName.Trim(),
                Password = Password,
                TrustServerCertificate = TrustServerCertificate
            }
        },
        _ => throw new InvalidOperationException($"Unknown database provider '{Provider}'.")
    };
}

/// <summary>Outcome of a connection test or of completing the setup.</summary>
public sealed record DatabaseTestResult(bool Success, string Message, IReadOnlyList<string> Details)
{
    public static DatabaseTestResult Ok(string message, params string[] details) => new(true, message, details);
    public static DatabaseTestResult Fail(string message, params string[] details) => new(false, message, details);
}

/// <summary>Connection testing and first-run setup used by the <c>/setup</c> page.</summary>
public interface IDatabaseSetupService
{
    /// <summary>Checks the settings against the server without changing anything. <paramref name="role"/> decides which schema is expected there.</summary>
    Task<DatabaseTestResult> TestConnectionAsync(DatabaseSetupRequest request, DatabaseRole role = DatabaseRole.Primary, CancellationToken ct = default);

    /// <summary>
    /// Tests the connection(s), creates the database(s) when requested, writes <c>dyncms.database.json</c>
    /// and initialises the schema, identity framework and startup tasks. <paramref name="analytics"/>, when given,
    /// is a separate database for visitor analytics.
    /// </summary>
    Task<DatabaseTestResult> CompleteSetupAsync(DatabaseSetupRequest request, DatabaseSetupRequest? analytics = null, AnalyticsHistoryAction history = AnalyticsHistoryAction.Move, CancellationToken ct = default);

    /// <summary>
    /// Analytics rows an existing database already holds (so the setup page can ask what to do with them when a
    /// separate analytics database is chosen). Null when the database cannot be opened or has no analytics tables.
    /// </summary>
    Task<AnalyticsHistoryInfo?> CountExistingAnalyticsAsync(DatabaseSetupRequest request, CancellationToken ct = default);
}

internal sealed class DatabaseSetupService(
    DatabaseConfigurationStore store,
    DynCmsRuntime runtime,
    StartupContext startup,
    ILogger<DatabaseSetupService> logger) : IDatabaseSetupService
{
    public async Task<DatabaseTestResult> TestConnectionAsync(DatabaseSetupRequest request, DatabaseRole role = DatabaseRole.Primary, CancellationToken ct = default)
    {
        DatabaseConfiguration config;
        try
        {
            config = request.ToConfiguration();
            config.Validate();
        }
        catch (InvalidOperationException ex)
        {
            return DatabaseTestResult.Fail(ex.Message);
        }

        try
        {
            return config.Provider == DatabaseProvider.Sqlite
                ? await TestSqliteAsync(request, config.Sqlite!, role, ct)
                : await TestSqlServerAsync(request, config.SqlServer!, role, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Database connection test failed");
            return DatabaseTestResult.Fail("Connection failed.", Describe(ex));
        }
    }

    public async Task<AnalyticsHistoryInfo?> CountExistingAnalyticsAsync(DatabaseSetupRequest request, CancellationToken ct = default)
    {
        try
        {
            var config = request.ToConfiguration();
            config.Validate();
            if (config.Provider == DatabaseProvider.Sqlite && !File.Exists(config.Sqlite!.ResolvePath(store.BasePath))) return null;
            return await AnalyticsStoreMigrator.CountAsync(config, store.BasePath, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<DatabaseTestResult> CompleteSetupAsync(DatabaseSetupRequest request, DatabaseSetupRequest? analytics = null, AnalyticsHistoryAction history = AnalyticsHistoryAction.Move, CancellationToken ct = default)
    {
        if (runtime.IsReady || store.Current is not null)
            return DatabaseTestResult.Fail("The database has already been configured.", $"Delete {store.FilePath} and restart the application to run the setup again.");

        var test = await TestConnectionAsync(request, DatabaseRole.Primary, ct);
        if (!test.Success) return test;

        var config = request.ToConfiguration();
        var analyticsConfig = analytics?.ToConfiguration();
        if (analyticsConfig is not null && !analyticsConfig.SameTargetAs(config, store.BasePath))
        {
            var analyticsTest = await TestConnectionAsync(analytics!, DatabaseRole.Analytics, ct);
            if (!analyticsTest.Success) return analyticsTest with { Message = "Analytics database: " + analyticsTest.Message };
            config.Analytics = analyticsConfig;
        }

        try
        {
            if (config.Provider == DatabaseProvider.SqlServer && request.Mode == DatabaseSetupMode.NewDatabase && request.CreateDatabase)
                await CreateSqlServerDatabaseAsync(config.SqlServer!, ct);
            if (config.Analytics is { Provider: DatabaseProvider.SqlServer } && analytics!.Mode == DatabaseSetupMode.NewDatabase && analytics.CreateDatabase)
                await CreateSqlServerDatabaseAsync(config.Analytics.SqlServer!, ct);

            store.Save(config);
            logger.LogInformation("Database configuration written to {Path}", store.FilePath);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Setup failed while preparing the database");
            return DatabaseTestResult.Fail("Setup failed while preparing the database.", Describe(ex));
        }

        // The seeders run as startup tasks inside InitializeAfterSetupAsync; this is how they learn what was picked.
        startup.RequestedStarterContent = request.StarterContent;
        try
        {
            await runtime.InitializeAfterSetupAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return DatabaseTestResult.Fail("The configuration was saved but initialising the database failed. The configuration file has been removed so you can try again.", Describe(ex));
        }
        finally
        {
            startup.RequestedStarterContent = null;
        }

        // An existing site that already recorded page views into its content database, now with a separate analytics
        // database: the old rows are moved across or deleted as the person chose, so nothing is left behind unseen.
        var details = new List<string> { $"Configuration saved to {store.FilePath}." };
        if (config.Analytics is not null)
        {
            try
            {
                var leftover = await runtime.ResolveLeftoverHistoryAsync(history, ct);
                if (leftover is not null)
                    details.Add(history == AnalyticsHistoryAction.Move
                        ? $"{leftover.PageViewsCopied:N0} page views were moved into the analytics database."
                        : "The page views already in the content database were deleted.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Old analytics rows in the primary database could not be handled after setup");
                details.Add("The page views already in the content database could not be moved; the Data tab offers to move or delete them.");
            }
        }

        return DatabaseTestResult.Ok("Setup complete.", [.. details]);
    }

    // ---------------------------------------------------------------- SQLite

    private async Task<DatabaseTestResult> TestSqliteAsync(DatabaseSetupRequest request, SqliteSettings settings, DatabaseRole role, CancellationToken ct)
    {
        var path = settings.ResolvePath(store.BasePath);
        var directory = Path.GetDirectoryName(path)!;

        if (request.Mode == DatabaseSetupMode.NewDatabase)
        {
            if (File.Exists(path))
                return DatabaseTestResult.Fail("A file already exists at that location.", path, "Choose another file name, or pick \"Use an existing database\" to keep it.");

            try
            {
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, $".dyncms-write-test-{Guid.NewGuid():N}");
                await File.WriteAllTextAsync(probe, "ok", ct);
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return DatabaseTestResult.Fail("The folder is not writable.", directory, ex.Message);
            }

            return DatabaseTestResult.Ok("Ready to create the SQLite database.", $"File: {path}", "The folder is writable; the database file and schema will be created.");
        }

        if (!File.Exists(path))
            return DatabaseTestResult.Fail("The database file does not exist.", path, "Check the path, or pick \"Create a new database\".");

        // Make sure it is a SQLite database we can open before inspecting the schema.
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            await connection.OpenAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA schema_version";
            await cmd.ExecuteScalarAsync(ct);
        }

        var schema = await InspectSchemaAsync(request.ToConfiguration(), role, ct);
        return schema.IsPartial
            ? PartialSchema(schema, $"File: {path}")
            : DatabaseTestResult.Ok("Connected to the SQLite database.", $"File: {path}", SchemaNote(schema, role));
    }

    // ------------------------------------------------------------ shared

    /// <summary>Opens the context for <paramref name="role"/> on the candidate database and reports which of its tables exist.</summary>
    private async Task<SchemaState> InspectSchemaAsync(DatabaseConfiguration config, DatabaseRole role, CancellationToken ct)
    {
        if (role == DatabaseRole.Analytics)
        {
            await using var analytics = new AnalyticsDbContext(AnalyticsDbContext.OptionsFor(config, store.BasePath));
            return await DatabaseSchema.InspectAsync(analytics, ct);
        }

        var builder = new DbContextOptionsBuilder<DynCmsDbContext>();
        DynCmsDbContextFactory.Apply(builder, config, store.BasePath);
        await using var db = new DynCmsDbContext(builder.Options);
        return await DatabaseSchema.InspectAsync(db, ct);
    }

    private static DatabaseTestResult PartialSchema(SchemaState schema, string target) => DatabaseTestResult.Fail(
        "The database contains only part of the DynCMS schema.", target,
        $"Found: {string.Join(", ", schema.Existing)}. Missing: {string.Join(", ", schema.Missing)}.",
        "Remove the leftover tables or choose another database.");

    private static string SchemaNote(SchemaState schema, DatabaseRole role) => (schema.IsComplete, role) switch
    {
        (true, DatabaseRole.Analytics) => "Existing analytics tables were found; they will be used as is.",
        (true, _) => "An existing DynCMS schema was found; it will be used as is.",
        (false, DatabaseRole.Analytics) => "No analytics tables found; they will be created.",
        (false, _) => "No DynCMS tables found; the schema will be created."
    };

    // ------------------------------------------------------------ SQL Server

    private async Task<DatabaseTestResult> TestSqlServerAsync(DatabaseSetupRequest request, SqlServerSettings settings, DatabaseRole role, CancellationToken ct)
    {
        var createViaMaster = request.Mode == DatabaseSetupMode.NewDatabase && request.CreateDatabase;

        if (createViaMaster)
        {
            await using var master = new SqlConnection(settings.BuildMasterConnectionString());
            await master.OpenAsync(ct);
            var version = await ServerVersionAsync(master, ct);

            await using var check = master.CreateCommand();
            check.CommandText = "SELECT DB_ID(@name)";
            check.Parameters.AddWithValue("@name", settings.Database);
            var exists = await check.ExecuteScalarAsync(ct) is not (null or DBNull);

            if (exists)
                return DatabaseTestResult.Fail($"The database '{settings.Database}' already exists on this server.",
                    version, "Pick another name, or choose \"Use an existing database\" to use it.");

            return DatabaseTestResult.Ok("Connected to the server.", version,
                $"Database '{settings.Database}' does not exist yet and will be created through master.");
        }

        await using var connection = new SqlConnection(settings.BuildConnectionString());
        try
        {
            await connection.OpenAsync(ct);
        }
        catch (SqlException ex) when (ex.Number == 4060) // cannot open database requested by the login
        {
            return DatabaseTestResult.Fail($"The database '{settings.Database}' does not exist or the user may not open it.",
                request.Mode == DatabaseSetupMode.NewDatabase
                    ? "Turn on \"Create the database if it does not exist\" to let DynCMS create it through master."
                    : "Check the database name and the user's permissions.",
                ex.Message);
        }

        var serverVersion = await ServerVersionAsync(connection, ct);
        await connection.CloseAsync();

        var schema = await InspectSchemaAsync(request.ToConfiguration(), role, ct);
        if (schema.IsPartial) return PartialSchema(schema, serverVersion);

        if (request.Mode == DatabaseSetupMode.NewDatabase && !schema.IsEmpty)
            return DatabaseTestResult.Fail($"The database '{settings.Database}' already contains DynCMS tables.",
                serverVersion, "Choose \"Use an existing database\" to keep that content.");

        return DatabaseTestResult.Ok($"Connected to database '{settings.Database}'.", serverVersion, SchemaNote(schema, role));
    }

    internal static async Task CreateSqlServerDatabaseAsync(SqlServerSettings settings, CancellationToken ct)
    {
        await using var master = new SqlConnection(settings.BuildMasterConnectionString());
        await master.OpenAsync(ct);

        await using var check = master.CreateCommand();
        check.CommandText = "SELECT DB_ID(@name)";
        check.Parameters.AddWithValue("@name", settings.Database);
        if (await check.ExecuteScalarAsync(ct) is not (null or DBNull)) return;

        // The name is restricted to letters, digits, '_' and '-' by SqlServerSettings.Validate, so it is
        // safe to quote; ']' is escaped anyway for good measure.
        await using var create = master.CreateCommand();
        create.CommandText = $"CREATE DATABASE [{settings.Database.Replace("]", "]]")}]";
        create.CommandTimeout = 120;
        await create.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> ServerVersionAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT @@VERSION";
        var version = (await cmd.ExecuteScalarAsync(ct))?.ToString() ?? connection.ServerVersion;
        var firstLine = version.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? version;
        var dash = firstLine.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? firstLine[..dash] : firstLine;
    }

    internal static string Describe(Exception ex)
    {
        var root = ex;
        while (root.InnerException is not null && root is not SqlException and not SqliteException) root = root.InnerException;
        return root.Message;
    }
}
