using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DynCMS.Core.Analytics;
using DynCMS.Core.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DynCMS.Core.Data;

/// <summary>A table in the configured database with its row count.</summary>
public sealed record DatabaseTableInfo(string Name, long? Rows);

/// <summary>Snapshot of the configured database shown on the Data tab of the back office.</summary>
public sealed record DatabaseInfo(
    bool IsReady,
    DatabaseProvider Provider,
    string Description,
    string ConfigurationFile,
    bool ConfigurationFileExists,
    string BackupFolder,
    string? SqliteFile,
    long? SqliteFileSize,
    DateTime? SqliteLastWriteUtc,
    string? Server,
    int? Port,
    string? Database,
    string? UserName,
    bool? TrustServerCertificate,
    IReadOnlyList<DatabaseTableInfo> Tables,
    string? TablesError,
    DatabaseRole Role = DatabaseRole.Primary,
    bool SharedWithPrimary = false);

/// <summary>A backup known to DynCMS: a SQLite copy in the backup folder, or a SQL Server .bak the server wrote.</summary>
public sealed record DatabaseBackupInfo(
    string FileName,
    string FullPath,
    long? Size,
    DateTime CreatedUtc,
    DatabaseProvider Provider,
    string? Database,
    string? Note,
    bool IsLocalFile,
    DatabaseRole Role = DatabaseRole.Primary)
{
    public string Extension => Path.GetExtension(FileName);
}

/// <summary>Database administration used by the back office Data tab.</summary>
public interface IDatabaseMaintenanceService
{
    /// <summary>Absolute path of the folder that stores backups and the backup manifest.</summary>
    string BackupFolder { get; }

    /// <summary>Describes the database behind <paramref name="role"/> and lists its tables.</summary>
    Task<DatabaseInfo> GetInfoAsync(DatabaseRole role = DatabaseRole.Primary, CancellationToken ct = default);

    /// <summary>Known backups of <paramref name="role"/>'s database, newest first.</summary>
    Task<IReadOnlyList<DatabaseBackupInfo>> ListBackupsAsync(DatabaseRole role = DatabaseRole.Primary, CancellationToken ct = default);

    /// <summary>Finds a backup of either role by file name (used by the download endpoint).</summary>
    DatabaseBackupInfo? FindBackup(string fileName);

    /// <summary>
    /// Backs up the database behind <paramref name="role"/>. SQLite: an online copy into the backup folder. SQL Server:
    /// <c>BACKUP DATABASE</c> into the server's default backup folder (the file lives on the server). Fails for
    /// <see cref="DatabaseRole.Analytics"/> while analytics shares the primary database (back that one up instead).
    /// </summary>
    Task<DatabaseBackupInfo> CreateBackupAsync(string? note = null, DatabaseRole role = DatabaseRole.Primary, CancellationToken ct = default);

    /// <summary>Replaces the content of <paramref name="role"/>'s database with the backup, which must be of that role and provider.</summary>
    Task RestoreBackupAsync(string fileName, bool backupFirst = true, DatabaseRole role = DatabaseRole.Primary, CancellationToken ct = default);

    /// <summary>Stores an uploaded backup file in the backup folder, filed under <paramref name="role"/>, so it can be restored later.</summary>
    Task<DatabaseBackupInfo> ImportBackupAsync(string fileName, Stream content, DatabaseRole role = DatabaseRole.Primary, CancellationToken ct = default);

    Task DeleteBackupAsync(string fileName, CancellationToken ct = default);

    /// <summary>
    /// Tests the new settings, writes them to <c>dyncms.database.json</c> and re-initialises the application on
    /// the new database without a restart. On failure the previous configuration is restored.
    /// </summary>
    Task<DatabaseTestResult> SwitchConfigurationAsync(DatabaseSetupRequest request, CancellationToken ct = default);

    /// <summary>
    /// Moves visitor analytics to its own database (<paramref name="request"/>) or back into the primary one
    /// (<c>null</c>), without a restart or a sign-out. With <paramref name="copyData"/> the recorded history is copied
    /// to the new store and removed from the old one.
    /// </summary>
    Task<DatabaseTestResult> SwitchAnalyticsConfigurationAsync(DatabaseSetupRequest? request, AnalyticsHistoryAction history, CancellationToken ct = default);

    /// <summary>
    /// Old analytics rows still sitting in the primary database while analytics runs in its own (for example after a
    /// setup that pointed a separate analytics database at an existing site). Null when there are none.
    /// </summary>
    Task<AnalyticsHistoryInfo?> GetLeftoverAnalyticsAsync(CancellationToken ct = default);

    /// <summary>Moves the leftover history into the analytics database or deletes it; the tables leave the primary database either way.</summary>
    Task<AnalyticsMigrationResult?> ResolveLeftoverAnalyticsAsync(AnalyticsHistoryAction history, CancellationToken ct = default);

    /// <summary>
    /// Deletes <c>dyncms.database.json</c> and puts the application back into setup mode: every page redirects
    /// to <c>/setup</c> until a database is configured again. The database itself is not touched.
    /// </summary>
    Task ResetConfigurationAsync(CancellationToken ct = default);
}

internal sealed partial class DatabaseMaintenanceService(
    DatabaseConfigurationStore store,
    DynCmsPaths paths,
    DynCmsRuntime runtime,
    IDatabaseSetupService setup,
    StartupContext startup,
    ILogger<DatabaseMaintenanceService> logger) : IDatabaseMaintenanceService
{
    private const string ManifestFileName = "backups.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _backupGate = new(1, 1);

    public string BackupFolder => paths.BackupRootPath;

    // ------------------------------------------------------------------ info

    private DatabaseConfiguration Target(DatabaseRole role) => store.Resolve(role) ?? throw new DynCmsNotConfiguredException();

    /// <summary>True while the analytics tables live in the primary database.</summary>
    private bool IsShared(DatabaseRole role) => role == DatabaseRole.Analytics && !store.IsAnalyticsSeparate;

    private static string RoleName(DatabaseRole role) => role == DatabaseRole.Analytics ? "analytics" : "primary";

    /// <summary>Backups of the analytics database are told apart from the primary ones by this file name prefix.</summary>
    private const string AnalyticsPrefix = "analytics-";

    public async Task<DatabaseInfo> GetInfoAsync(DatabaseRole role = DatabaseRole.Primary, CancellationToken ct = default)
    {
        var config = Target(role);

        string? sqliteFile = null;
        long? sqliteSize = null;
        DateTime? sqliteWrite = null;
        if (config.Provider == DatabaseProvider.Sqlite)
        {
            sqliteFile = config.Sqlite!.ResolvePath(store.BasePath);
            var file = new FileInfo(sqliteFile);
            if (file.Exists)
            {
                // Include the write-ahead log so the size reflects what is really on disk.
                sqliteSize = file.Length + new[] { "-wal", "-shm" }.Sum(suffix => new FileInfo(sqliteFile + suffix) is { Exists: true } f ? f.Length : 0);
                sqliteWrite = file.LastWriteTimeUtc;
            }
        }

        IReadOnlyList<DatabaseTableInfo> tables = [];
        string? tablesError = null;
        try
        {
            tables = await ListTablesAsync(config, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list database tables");
            tablesError = DatabaseSetupService.Describe(ex);
        }

        var sql = config.SqlServer;
        return new DatabaseInfo(
            runtime.IsReady,
            config.Provider,
            config.Describe(store.BasePath),
            store.FilePath,
            store.Exists,
            BackupFolder,
            sqliteFile, sqliteSize, sqliteWrite,
            sql?.Server, sql?.Port, sql?.Database, sql?.UserName, sql?.TrustServerCertificate,
            tables,
            tablesError,
            role,
            IsShared(role));
    }

    private async Task<IReadOnlyList<DatabaseTableInfo>> ListTablesAsync(DatabaseConfiguration config, CancellationToken ct)
    {
        // Only raw catalog queries run here, so any context on the target will do.
        var builder = new DbContextOptionsBuilder<DynCmsDbContext>();
        DynCmsDbContextFactory.Apply(builder, config, store.BasePath);
        await using var db = new DynCmsDbContext(builder.Options);
        var listSql = db.Database.IsSqlite()
            ? "SELECT name AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name"
            : "SELECT TABLE_NAME AS \"Value\" FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME";
        var names = await db.Database.SqlQueryRaw<string>(listSql).ToListAsync(ct);

        var result = new List<DatabaseTableInfo>(names.Count);
        foreach (var name in names)
        {
            long? rows;
            try
            {
                // The name comes from the catalog and is quoted as an identifier; no user input is involved.
                var quoted = "\"" + name.Replace("\"", "\"\"") + "\"";
#pragma warning disable EF1002, EF1003
                rows = (await db.Database.SqlQueryRaw<long>("SELECT CAST(COUNT(*) AS BIGINT) AS \"Value\" FROM " + quoted).ToListAsync(ct)).FirstOrDefault();
#pragma warning restore EF1002, EF1003
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not count rows of {Table}", name);
                rows = null;
            }
            result.Add(new DatabaseTableInfo(name, rows));
        }
        return result;
    }

    // --------------------------------------------------------------- backups

    public async Task<IReadOnlyList<DatabaseBackupInfo>> ListBackupsAsync(DatabaseRole role = DatabaseRole.Primary, CancellationToken ct = default)
    {
        await _backupGate.WaitAsync(ct);
        try
        {
            return ListBackupsCore(role);
        }
        finally
        {
            _backupGate.Release();
        }
    }

    public DatabaseBackupInfo? FindBackup(string fileName)
    {
        if (!IsSafeFileName(fileName)) return null;
        _backupGate.Wait();
        try
        {
            return ListBackupsCore(null).FirstOrDefault(b => string.Equals(b.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _backupGate.Release();
        }
    }

    public async Task<DatabaseBackupInfo> CreateBackupAsync(string? note = null, DatabaseRole role = DatabaseRole.Primary, CancellationToken ct = default)
    {
        if (IsShared(role))
            throw new InvalidOperationException("Analytics data lives in the primary database; back up the primary database instead.");

        var config = Target(role);
        await _backupGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(BackupFolder);
            var prefix = role == DatabaseRole.Analytics ? AnalyticsPrefix : string.Empty;
            var entry = config.Provider == DatabaseProvider.Sqlite
                ? await BackupSqliteAsync(config.Sqlite!, prefix, note, ct)
                : await BackupSqlServerAsync(config.SqlServer!, prefix, note, ct);
            entry.Role = role;

            var manifest = LoadManifest();
            manifest.RemoveAll(e => string.Equals(e.FileName, entry.FileName, StringComparison.OrdinalIgnoreCase));
            manifest.Add(entry);
            SaveManifest(manifest);

            logger.LogInformation("{Role} database backup written to {Path}", RoleName(role), entry.Path);
            return ToInfo(entry);
        }
        finally
        {
            _backupGate.Release();
        }
    }

    public async Task RestoreBackupAsync(string fileName, bool backupFirst = true, DatabaseRole role = DatabaseRole.Primary, CancellationToken ct = default)
    {
        if (IsShared(role))
            throw new InvalidOperationException("Analytics data lives in the primary database; restore a primary backup instead.");

        var config = Target(role);
        var backup = FindBackup(fileName) ?? throw new FileNotFoundException($"Backup '{fileName}' was not found.");

        if (backup.Role != role)
            throw new InvalidOperationException($"'{backup.FileName}' is a backup of the {RoleName(backup.Role)} database and cannot be restored into the {RoleName(role)} one.");
        if (backup.Provider != config.Provider)
            throw new InvalidOperationException($"'{backup.FileName}' is a {ProviderName(backup.Provider)} backup, but the {RoleName(role)} database runs on {ProviderName(config.Provider)}.");

        if (backupFirst)
            await CreateBackupAsync($"Automatic backup before restoring {backup.FileName}", role, ct);

        await _backupGate.WaitAsync(ct);
        try
        {
            logger.LogWarning("Restoring database from {Backup}", backup.FullPath);
            if (config.Provider == DatabaseProvider.Sqlite)
                await RestoreSqliteAsync(config.Sqlite!, backup, ct);
            else
                await RestoreSqlServerAsync(config.SqlServer!, backup, ct);
        }
        finally
        {
            _backupGate.Release();
        }

        if (role == DatabaseRole.Primary)
        {
            await runtime.RefreshAfterRestoreAsync(ct);
        }
        else
        {
            // A backup from before the analytics schema grew a column is brought up to date, then the worker forgets what it cached.
            await AnalyticsStoreMigrator.EnsureSchemaAsync(config, store.BasePath, ct);
            runtime.NotifyAnalyticsStoreChanged();
        }
        logger.LogInformation("{Role} database restored from {Backup}", RoleName(role), backup.FileName);
    }

    public async Task<DatabaseBackupInfo> ImportBackupAsync(string fileName, Stream content, DatabaseRole role = DatabaseRole.Primary, CancellationToken ct = default)
    {
        var config = Target(role);
        var name = SanitizeFileName(fileName);
        if (role == DatabaseRole.Analytics && !name.StartsWith(AnalyticsPrefix, StringComparison.OrdinalIgnoreCase)) name = AnalyticsPrefix + name;
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var provider = extension switch
        {
            ".db" or ".sqlite" or ".sqlite3" => DatabaseProvider.Sqlite,
            ".bak" => DatabaseProvider.SqlServer,
            _ => throw new InvalidOperationException("Only SQLite files (.db, .sqlite, .sqlite3) and SQL Server backups (.bak) can be uploaded.")
        };

        await _backupGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(BackupFolder);
            var path = UniquePath(Path.Combine(BackupFolder, name));
            name = Path.GetFileName(path);

            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(file, ct);

            if (provider == DatabaseProvider.Sqlite)
            {
                try
                {
                    await VerifySqliteFileAsync(path, ct);
                }
                catch (Exception ex)
                {
                    File.Delete(path);
                    throw new InvalidOperationException("The uploaded file is not a readable SQLite database.", ex);
                }
            }

            var entry = new ManifestEntry
            {
                FileName = name,
                Path = path,
                CreatedUtc = DateTime.UtcNow,
                Provider = provider,
                Database = provider == DatabaseProvider.SqlServer ? config.SqlServer?.Database : System.IO.Path.GetFileName(config.Sqlite?.DatabasePath),
                Note = "Uploaded" + (provider == DatabaseProvider.SqlServer ? " — copy this file to the SQL Server's backup folder before restoring" : string.Empty),
                Size = new FileInfo(path).Length,
                Role = role
            };
            var manifest = LoadManifest();
            manifest.Add(entry);
            SaveManifest(manifest);
            return ToInfo(entry);
        }
        finally
        {
            _backupGate.Release();
        }
    }

    public async Task DeleteBackupAsync(string fileName, CancellationToken ct = default)
    {
        var backup = FindBackup(fileName) ?? throw new FileNotFoundException($"Backup '{fileName}' was not found.");
        await _backupGate.WaitAsync(ct);
        try
        {
            if (backup.IsLocalFile) File.Delete(backup.FullPath);
            var manifest = LoadManifest();
            manifest.RemoveAll(e => string.Equals(e.FileName, backup.FileName, StringComparison.OrdinalIgnoreCase));
            SaveManifest(manifest);
            logger.LogInformation("Database backup {Backup} deleted", backup.FileName);
        }
        finally
        {
            _backupGate.Release();
        }
    }

    // ---------------------------------------------------------------- SQLite

    private async Task<ManifestEntry> BackupSqliteAsync(SqliteSettings settings, string prefix, string? note, CancellationToken ct)
    {
        var source = settings.ResolvePath(store.BasePath);
        if (!File.Exists(source)) throw new FileNotFoundException("The SQLite database file does not exist.", source);

        var stem = Path.GetFileNameWithoutExtension(source);
        var path = UniquePath(Path.Combine(BackupFolder, $"{prefix}{stem}-{DateTime.Now:yyyyMMdd-HHmmss}.db"));

        await using var live = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source }.ToString());
        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await live.OpenAsync(ct);
        await target.OpenAsync(ct);
        // Online, page-by-page copy that is consistent even while the application keeps writing (WAL friendly).
        live.BackupDatabase(target);
        // Keep the copy a single self-contained file: no -wal/-shm companions when it is opened or downloaded later.
        await using (var journal = target.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=DELETE";
            await journal.ExecuteScalarAsync(ct);
        }
        await target.CloseAsync();
        await live.CloseAsync();
        SqliteConnection.ClearPool(target);
        RemoveEmptySideFiles(path);

        return new ManifestEntry
        {
            FileName = Path.GetFileName(path),
            Path = path,
            CreatedUtc = DateTime.UtcNow,
            Provider = DatabaseProvider.Sqlite,
            Database = Path.GetFileName(source),
            Note = note,
            Size = new FileInfo(path).Length
        };
    }

    private async Task RestoreSqliteAsync(SqliteSettings settings, DatabaseBackupInfo backup, CancellationToken ct)
    {
        if (!backup.IsLocalFile) throw new FileNotFoundException("The backup file is missing from the backup folder.", backup.FullPath);
        await VerifySqliteFileAsync(backup.FullPath, ct);

        var target = settings.ResolvePath(store.BasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // Drop pooled connections so no stale schema cache survives, then copy the backup over the live file
        // through SQLite's backup API: it takes the write lock and leaves the WAL in a consistent state.
        SqliteConnection.ClearAllPools();

        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup.FullPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await using var live = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target }.ToString());
        await source.OpenAsync(ct);
        await live.OpenAsync(ct);
        source.BackupDatabase(live);

        await using (var checkpoint = live.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await checkpoint.ExecuteNonQueryAsync(ct);
        }
        await live.CloseAsync();
        await source.CloseAsync();
        SqliteConnection.ClearAllPools();
        RemoveEmptySideFiles(backup.FullPath);
    }

    /// <summary>Deletes the empty -wal/-shm files SQLite leaves behind after a read-only open of a WAL-mode file.</summary>
    private static void RemoveEmptySideFiles(string databasePath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var side = new FileInfo(databasePath + suffix);
            try
            {
                if (side.Exists && side.Length == 0) side.Delete();
            }
            catch (IOException) { /* still in use; harmless */ }
        }
    }

    private static async Task VerifySqliteFileAsync(string path, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA schema_version";
        await cmd.ExecuteScalarAsync(ct);
        await connection.CloseAsync();
        SqliteConnection.ClearPool(connection);
        RemoveEmptySideFiles(path);
    }

    // ------------------------------------------------------------ SQL Server

    private async Task<ManifestEntry> BackupSqlServerAsync(SqlServerSettings settings, string prefix, string? note, CancellationToken ct)
    {
        await using var connection = new SqlConnection(settings.BuildConnectionString());
        await connection.OpenAsync(ct);

        string? folder;
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))";
            folder = (await query.ExecuteScalarAsync(ct)) as string;
        }
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("SQL Server did not report a default backup folder (InstanceDefaultBackupPath). Back up the database with SQL Server tools instead.");

        var fileName = $"{prefix}{settings.Database}-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        var path = folder.TrimEnd('\\', '/') + (folder.Contains('/') && !folder.Contains('\\') ? "/" : "\\") + fileName;

        await using (var backup = connection.CreateCommand())
        {
            backup.CommandTimeout = 0;
            backup.CommandText = $"BACKUP DATABASE [{Bracket(settings.Database)}] TO DISK = @path WITH INIT, FORMAT, COPY_ONLY, CHECKSUM, NAME = @name";
            backup.Parameters.AddWithValue("@path", path);
            backup.Parameters.AddWithValue("@name", $"DynCMS backup of {settings.Database}");
            await backup.ExecuteNonQueryAsync(ct);
        }

        var local = File.Exists(path) ? new FileInfo(path).Length : (long?)null;
        return new ManifestEntry
        {
            FileName = fileName,
            Path = path,
            CreatedUtc = DateTime.UtcNow,
            Provider = DatabaseProvider.SqlServer,
            Database = settings.Database,
            Note = note ?? "Written by SQL Server into its default backup folder",
            Size = local
        };
    }

    private static async Task RestoreSqlServerAsync(SqlServerSettings settings, DatabaseBackupInfo backup, CancellationToken ct)
    {
        SqlConnection.ClearAllPools();
        var db = Bracket(settings.Database);

        await using var master = new SqlConnection(settings.BuildMasterConnectionString());
        await master.OpenAsync(ct);

        await Exec(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE", ct);
        try
        {
            await using var restore = master.CreateCommand();
            restore.CommandTimeout = 0;
            restore.CommandText = $"RESTORE DATABASE [{db}] FROM DISK = @path WITH REPLACE";
            restore.Parameters.AddWithValue("@path", backup.FullPath);
            await restore.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            try { await Exec(master, $"ALTER DATABASE [{db}] SET MULTI_USER", CancellationToken.None); }
            catch { /* the database may still be restoring; leave the error from the restore itself to surface */ }
        }
        SqlConnection.ClearAllPools();
    }

    private static async Task Exec(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // SqlServerSettings.Validate restricts the name to letters, digits, '_' and '-'; ']' is escaped regardless.
    private static string Bracket(string identifier) => identifier.Replace("]", "]]");

    // --------------------------------------------------------- configuration

    public async Task<DatabaseTestResult> SwitchConfigurationAsync(DatabaseSetupRequest request, CancellationToken ct = default)
    {
        var test = await setup.TestConnectionAsync(request, DatabaseRole.Primary, ct);
        if (!test.Success) return test;

        // The analytics database stays where it is when the primary one moves.
        var config = request.ToConfiguration().WithAnalytics(store.Current?.Analytics);
        try
        {
            if (config.Provider == DatabaseProvider.SqlServer && request.Mode == DatabaseSetupMode.NewDatabase && request.CreateDatabase)
                await DatabaseSetupService.CreateSqlServerDatabaseAsync(config.SqlServer!, ct);

            startup.RequestedStarterContent = request.StarterContent;   // read by the seeders when the new database is empty
            await runtime.SwitchAsync(config, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Switching the database configuration failed");
            return DatabaseTestResult.Fail("Switching the database failed. The previous configuration is back in use.", DatabaseSetupService.Describe(ex));
        }
        finally
        {
            startup.RequestedStarterContent = null;
        }

        return DatabaseTestResult.Ok("The application now runs on the new database.",
            $"Configuration saved to {store.FilePath}.",
            "Sign in again: sessions belong to the database they were created in.");
    }

    public async Task<AnalyticsHistoryInfo?> GetLeftoverAnalyticsAsync(CancellationToken ct = default)
    {
        var current = store.Current ?? throw new DynCmsNotConfiguredException();
        if (current.Analytics is null) return null;
        return await AnalyticsStoreMigrator.CountAsync(current, store.BasePath, ct);
    }

    public Task<AnalyticsMigrationResult?> ResolveLeftoverAnalyticsAsync(AnalyticsHistoryAction history, CancellationToken ct = default) =>
        runtime.ResolveLeftoverHistoryAsync(history, ct);

    public async Task<DatabaseTestResult> SwitchAnalyticsConfigurationAsync(DatabaseSetupRequest? request, AnalyticsHistoryAction history, CancellationToken ct = default)
    {
        var current = store.Current ?? throw new DynCmsNotConfiguredException();
        DatabaseConfiguration? config = null;

        if (request is not null)
        {
            config = request.ToConfiguration();
            if (!config.SameTargetAs(current, store.BasePath))
            {
                var test = await setup.TestConnectionAsync(request, DatabaseRole.Analytics, ct);
                if (!test.Success) return test;
            }
        }

        AnalyticsMigrationResult? result;
        try
        {
            if (config is { Provider: DatabaseProvider.SqlServer } && request!.Mode == DatabaseSetupMode.NewDatabase && request.CreateDatabase)
                await DatabaseSetupService.CreateSqlServerDatabaseAsync(config.SqlServer!, ct);

            result = await runtime.SwitchAnalyticsStoreAsync(config, history, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Switching the analytics database failed");
            return DatabaseTestResult.Fail("Switching the analytics database failed. The previous configuration is still in use.", DatabaseSetupService.Describe(ex));
        }

        var target = store.Resolve(DatabaseRole.Analytics)!;
        var where = store.IsAnalyticsSeparate ? $"Analytics is now stored in {target.Describe(store.BasePath)}." : "Analytics is now stored in the primary database again.";
        if (result is null) return DatabaseTestResult.Ok("The analytics database is unchanged.", $"Configuration saved to {store.FilePath}.");
        return DatabaseTestResult.Ok(where,
            $"Configuration saved to {store.FilePath}.",
            history == AnalyticsHistoryAction.Move ? $"{result.PageViewsCopied:N0} page views and {result.GeoEntriesCopied:N0} cached locations were moved across." : "The old history was deleted; the reports start over.",
            "Nobody was signed out: sessions live in the primary database.");
    }

    public Task ResetConfigurationAsync(CancellationToken ct = default) => runtime.ResetAsync(ct);

    // -------------------------------------------------------------- manifest

    private sealed class ManifestEntry
    {
        public string FileName { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public DatabaseProvider Provider { get; set; }
        public string? Database { get; set; }
        public string? Note { get; set; }
        public long? Size { get; set; }
        public DatabaseRole Role { get; set; } = DatabaseRole.Primary;
    }

    private string ManifestPath => Path.Combine(BackupFolder, ManifestFileName);

    private List<ManifestEntry> LoadManifest()
    {
        if (!File.Exists(ManifestPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ManifestEntry>>(File.ReadAllText(ManifestPath), JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The backup manifest {Path} could not be read; starting a new one", ManifestPath);
            return [];
        }
    }

    private void SaveManifest(List<ManifestEntry> manifest)
    {
        Directory.CreateDirectory(BackupFolder);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest.OrderByDescending(e => e.CreatedUtc).ToList(), JsonOptions));
    }

    /// <summary>Manifest entries plus any backup files dropped into the folder by hand, filtered by role (null: every role).</summary>
    private List<DatabaseBackupInfo> ListBackupsCore(DatabaseRole? role)
    {
        var manifest = LoadManifest();
        var known = new HashSet<string>(manifest.Select(e => e.FileName), StringComparer.OrdinalIgnoreCase);
        var result = manifest.Select(ToInfo).ToList();

        if (Directory.Exists(BackupFolder))
        {
            foreach (var file in new DirectoryInfo(BackupFolder).EnumerateFiles())
            {
                var provider = file.Extension.ToLowerInvariant() switch
                {
                    ".db" or ".sqlite" or ".sqlite3" => DatabaseProvider.Sqlite,
                    ".bak" => DatabaseProvider.SqlServer,
                    _ => (DatabaseProvider?)null
                };
                if (provider is null || known.Contains(file.Name)) continue;
                var fileRole = file.Name.StartsWith(AnalyticsPrefix, StringComparison.OrdinalIgnoreCase) ? DatabaseRole.Analytics : DatabaseRole.Primary;
                result.Add(new DatabaseBackupInfo(file.Name, file.FullName, file.Length, file.CreationTimeUtc, provider.Value, null, "Found in the backup folder", true, fileRole));
            }
        }

        if (role is { } wanted) result.RemoveAll(b => b.Role != wanted);
        return result.OrderByDescending(b => b.CreatedUtc).ToList();
    }

    private static DatabaseBackupInfo ToInfo(ManifestEntry e)
    {
        var exists = File.Exists(e.Path);
        var size = exists ? new FileInfo(e.Path).Length : e.Size;
        return new DatabaseBackupInfo(e.FileName, e.Path, size, e.CreatedUtc, e.Provider, e.Database, e.Note, exists, e.Role);
    }

    // ----------------------------------------------------------------- names

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 _.\-()]{0,200}$")]
    private static partial Regex SafeFileName();

    private static bool IsSafeFileName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name) && !name.Contains("..") && SafeFileName().IsMatch(name);

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '.' or '-' or '(' or ')' ? c : '_').ToArray()).TrimStart('.', ' ', '_');
        if (cleaned.Length > 200) cleaned = cleaned[^200..];
        if (!IsSafeFileName(cleaned)) throw new InvalidOperationException("The file name is not valid.");
        return cleaned;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string ProviderName(DatabaseProvider provider) => provider == DatabaseProvider.SqlServer ? "SQL Server" : "SQLite";
}
