using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace DynCMS.Core.Data;

/// <summary>How much of a DbContext's schema a database already contains.</summary>
public sealed record SchemaState(IReadOnlyList<string> Expected, IReadOnlyList<string> Existing)
{
    public IReadOnlyList<string> Missing => Expected.Except(Existing, StringComparer.OrdinalIgnoreCase).ToList();
    public bool IsComplete => Missing.Count == 0;
    public bool IsEmpty => Existing.Count == 0;
    public bool IsPartial => !IsEmpty && !IsComplete;
}

/// <summary>A column the model maps but the database does not have yet.</summary>
public sealed record MissingColumn(string Table, string Column);

/// <summary>
/// Schema helpers that work when several DbContexts share one database. <c>EnsureCreated</c> gives up as soon
/// as the database contains any table, so each context creates its own tables based on what its model expects.
/// Tables added by newer DynCMS versions (for example <c>Templates</c>) are created on an existing database
/// without touching the tables that are already there, and columns added to existing tables (for example
/// <c>Templates.Role</c>) are appended with <c>ALTER TABLE</c>.
/// </summary>
public static class DatabaseSchema
{
    /// <summary>
    /// Creates the database (if missing), any table of <paramref name="db"/> that does not exist yet and any
    /// column a newer DynCMS version added to a table that does.
    /// </summary>
    /// <returns><c>true</c> when anything was created.</returns>
    public static bool EnsureTables(DbContext db)
    {
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!creator.Exists()) creator.Create();

        var state = Inspect(db);
        if (state.IsEmpty)
        {
            creator.CreateTables();
            return true;
        }

        var changed = false;

        if (!state.IsComplete)
        {
            Execute(db, CreateMissingTablesCommands(db, state.Missing));
            changed = true;
        }

        // Only tables that already existed can be missing columns; the ones just created are up to date.
        var columns = MissingColumns(db, state.Existing);
        if (columns.Count > 0)
        {
            Execute(db, AddMissingColumnsCommands(db, columns));
            changed = true;
        }

        return changed;
    }

    /// <inheritdoc cref="EnsureTables"/>
    public static async Task<bool> EnsureTablesAsync(DbContext db, CancellationToken ct = default)
    {
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(ct)) await creator.CreateAsync(ct);

        var state = await InspectAsync(db, ct);
        if (state.IsEmpty)
        {
            await creator.CreateTablesAsync(ct);
            return true;
        }

        var changed = false;

        if (!state.IsComplete)
        {
            await ExecuteAsync(db, CreateMissingTablesCommands(db, state.Missing), ct);
            changed = true;
        }

        var columns = await MissingColumnsAsync(db, state.Existing, ct);
        if (columns.Count > 0)
        {
            await ExecuteAsync(db, AddMissingColumnsCommands(db, columns), ct);
            changed = true;
        }

        return changed;
    }

    /// <summary>Compares the tables the model maps to with the tables present in the database.</summary>
    public static SchemaState Inspect(DbContext db)
    {
        var expected = ExpectedTables(db);
        var existing = expected.Where(t => TableExists(db, t)).ToList();
        return new SchemaState(expected, existing);
    }

    /// <inheritdoc cref="Inspect"/>
    public static async Task<SchemaState> InspectAsync(DbContext db, CancellationToken ct = default)
    {
        var expected = ExpectedTables(db);
        var existing = new List<string>();
        foreach (var table in expected)
            if (await TableExistsAsync(db, table, ct)) existing.Add(table);
        return new SchemaState(expected, existing);
    }

    public static bool TableExists(DbContext db, string table) =>
        db.Database.SqlQueryRaw<int>(TableExistsSql(db), table).AsEnumerable().FirstOrDefault() > 0;

    public static async Task<bool> TableExistsAsync(DbContext db, string table, CancellationToken ct = default) =>
        (await db.Database.SqlQueryRaw<int>(TableExistsSql(db), table).ToListAsync(ct)).FirstOrDefault() > 0;

    private static List<string> ExpectedTables(DbContext db) =>
        db.Model.GetEntityTypes().Select(e => e.GetTableName()).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void Execute(DbContext db, IReadOnlyList<MigrationCommand> commands) =>
        db.GetService<IMigrationCommandExecutor>().ExecuteNonQuery(commands, db.GetService<IRelationalConnection>());

    private static Task ExecuteAsync(DbContext db, IReadOnlyList<MigrationCommand> commands, CancellationToken ct) =>
        db.GetService<IMigrationCommandExecutor>().ExecuteNonQueryAsync(commands, db.GetService<IRelationalConnection>(), ct);

    // ---- missing tables -------------------------------------------------------------------------------

    /// <summary>
    /// The DDL for only the missing tables: the same operations <c>CreateTables</c> would run, filtered by table.
    /// Indexes and foreign keys that belong to those tables come along; existing tables are left alone.
    /// </summary>
#pragma warning disable EF1001 // IMigrationsModelDiffer is what IRelationalDatabaseCreator.CreateTables uses internally.
    private static IReadOnlyList<MigrationCommand> CreateMissingTablesCommands(DbContext db, IReadOnlyList<string> missing)
    {
        var model = db.GetService<IDesignTimeModel>().Model;
        var differ = db.GetService<IMigrationsModelDiffer>();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        var wanted = new HashSet<string>(missing, StringComparer.OrdinalIgnoreCase);

        var operations = differ.GetDifferences(null, model.GetRelationalModel())
            .Where(op => op switch
            {
                CreateTableOperation t => wanted.Contains(t.Name),
                CreateIndexOperation i => wanted.Contains(i.Table),
                AddForeignKeyOperation fk => wanted.Contains(fk.Table),
                AddUniqueConstraintOperation u => wanted.Contains(u.Table),
                _ => false
            })
            .ToList();

        return generator.Generate(operations, model);
    }
#pragma warning restore EF1001

    // ---- missing columns ------------------------------------------------------------------------------

    /// <summary>Columns the model maps on <paramref name="tables"/> that the database does not have.</summary>
    public static IReadOnlyList<MissingColumn> MissingColumns(DbContext db, IEnumerable<string> tables)
    {
        var result = new List<MissingColumn>();
        foreach (var (table, expected) in ExpectedColumns(db, tables))
        {
            var existing = ExistingColumns(db, table);
            result.AddRange(expected.Except(existing, StringComparer.OrdinalIgnoreCase).Select(c => new MissingColumn(table, c)));
        }
        return result;
    }

    /// <inheritdoc cref="MissingColumns"/>
    public static async Task<IReadOnlyList<MissingColumn>> MissingColumnsAsync(DbContext db, IEnumerable<string> tables, CancellationToken ct = default)
    {
        var result = new List<MissingColumn>();
        foreach (var (table, expected) in ExpectedColumns(db, tables))
        {
            var existing = await ExistingColumnsAsync(db, table, ct);
            result.AddRange(expected.Except(existing, StringComparer.OrdinalIgnoreCase).Select(c => new MissingColumn(table, c)));
        }
        return result;
    }

    private static List<(string Table, List<string> Columns)> ExpectedColumns(DbContext db, IEnumerable<string> tables)
    {
        var wanted = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
        return db.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables
            .Where(t => wanted.Contains(t.Name))
            .Select(t => (t.Name, t.Columns.Select(c => c.Name).ToList()))
            .ToList();
    }

    private static HashSet<string> ExistingColumns(DbContext db, string table) =>
        new(db.Database.SqlQueryRaw<string>(ColumnsSql(db), table).AsEnumerable(), StringComparer.OrdinalIgnoreCase);

    private static async Task<HashSet<string>> ExistingColumnsAsync(DbContext db, string table, CancellationToken ct) =>
        new(await db.Database.SqlQueryRaw<string>(ColumnsSql(db), table).ToListAsync(ct), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>ALTER TABLE … ADD COLUMN</c> for each missing column. A column that is required gets the model's
    /// default (or a zero value) so the rows already in the table stay valid.
    /// </summary>
    private static IReadOnlyList<MigrationCommand> AddMissingColumnsCommands(DbContext db, IReadOnlyList<MissingColumn> missing)
    {
        var model = db.GetService<IDesignTimeModel>().Model;
        var generator = db.GetService<IMigrationsSqlGenerator>();
        var columns = model.GetRelationalModel().Tables
            .SelectMany(t => t.Columns.Select(c => (Table: t.Name, Column: c)))
            .ToDictionary(x => (x.Table, x.Column.Name), x => x.Column);

        var operations = new List<MigrationOperation>();
        foreach (var item in missing)
        {
            if (!columns.TryGetValue((item.Table, item.Column), out var column)) continue;

            var clrType = column.ProviderClrType;
            operations.Add(new AddColumnOperation
            {
                Table = item.Table,
                Name = column.Name,
                ClrType = clrType,
                ColumnType = column.StoreType,
                IsNullable = column.IsNullable,
                MaxLength = column.MaxLength,
                IsUnicode = column.IsUnicode,
                Precision = column.Precision,
                Scale = column.Scale,
                DefaultValue = column.IsNullable ? null : column.DefaultValue ?? ZeroValue(clrType),
                DefaultValueSql = column.IsNullable ? null : column.DefaultValueSql,
                Comment = column.Comment
            });
        }

        return generator.Generate(operations, model);
    }

    /// <summary>A value an existing row can take for a newly added required column.</summary>
    private static object? ZeroValue(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string)) return string.Empty;
        if (t == typeof(byte[])) return Array.Empty<byte>();
        if (t == typeof(Guid)) return Guid.Empty;
        if (t == typeof(DateTime)) return default(DateTime);
        if (t == typeof(DateTimeOffset)) return default(DateTimeOffset);
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }

    private static string TableExistsSql(DbContext db) => db.Database.IsSqlite()
        ? "SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = {0}"
        : "SELECT COUNT(*) AS [Value] FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME = {0}";

    private static string ColumnsSql(DbContext db) => db.Database.IsSqlite()
        ? "SELECT name AS \"Value\" FROM pragma_table_info({0})"
        : "SELECT COLUMN_NAME AS [Value] FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = {0}";
}
