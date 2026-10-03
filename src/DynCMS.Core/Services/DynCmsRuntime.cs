using DynCMS.Core.Analytics;
using DynCMS.Core.Data;
using DynCMS.Core.Plugins;
using DynCMS.Core.Security;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynCMS.Core.Services;

/// <summary>
/// Work that runs once the database is available: at startup when a configuration file exists, or right after
/// the setup page has saved one. Register with <c>IDynCmsBuilder.AddStartupTask&lt;T&gt;()</c>.
/// Tasks run in order of registration inside a service scope.
/// </summary>
public interface IDynCmsStartupTask
{
    Task RunAsync(CancellationToken ct = default);
}

/// <summary>Tracks whether DynCMS has a working database and drives initialisation.</summary>
public interface IDynCmsRuntime
{
    /// <summary>True once the database schema, identity framework and startup tasks have been initialised.</summary>
    bool IsReady { get; }

    /// <summary>True while no database configuration file exists (the setup page must be completed).</summary>
    bool SetupRequired { get; }

    /// <summary>The configuration file location (shown on the setup page and in log messages).</summary>
    string ConfigurationFilePath { get; }

    /// <summary>
    /// Loads the configuration file and initialises the database. Returns <c>false</c> (without throwing)
    /// when there is no configuration file yet. Throws when the file exists but the database cannot be initialised.
    /// </summary>
    Task<bool> TryInitializeAsync(CancellationToken ct = default);
}

internal sealed class DynCmsRuntime(
    DatabaseConfigurationStore store,
    IServiceScopeFactory scopeFactory,
    ICmsIdentity identity,
    AnalyticsQueue analyticsQueue,
    PluginManager plugins,
    ILogger<DynCmsRuntime> logger) : IDynCmsRuntime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _ready;

    public bool IsReady => _ready;
    public bool SetupRequired => !_ready && store.Current is null && !store.Exists;
    public string ConfigurationFilePath => store.FilePath;

    public async Task<bool> TryInitializeAsync(CancellationToken ct = default)
    {
        if (_ready) return true;

        await _gate.WaitAsync(ct);
        try
        {
            if (_ready) return true;

            var config = store.Current ?? store.Load();
            if (config is null)
            {
                logger.LogWarning("No database configuration found at {Path}. Open /setup in the browser to configure the database.", store.FilePath);
                return false;
            }

            logger.LogInformation("Initialising DynCMS on {Database}", config.Describe(store.BasePath));
            await InitializeCoreAsync(ct);
            _ready = true;
        }
        finally
        {
            _gate.Release();
        }

        // Plugins start once the CMS is ready (outside the gate: a plugin may use CMS services while starting).
        await plugins.EnsureStartedAsync(ct);
        return true;
    }

    /// <summary>
    /// Initialises the database for a configuration the setup page has just saved. On failure the configuration
    /// file is removed again so the setup page stays reachable, and the exception is rethrown.
    /// </summary>
    internal async Task InitializeAfterSetupAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_ready) return;
            try
            {
                await InitializeCoreAsync(ct);
                _ready = true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Database initialisation failed after setup; removing {Path}", store.FilePath);
                store.Delete();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }

        await plugins.EnsureStartedAsync(ct);
    }

    /// <summary>
    /// Saves a new database configuration and initialises the application on it without a restart (schema,
    /// identity framework, startup tasks). When that fails the previous configuration is put back and
    /// re-initialised, and the exception is rethrown.
    /// </summary>
    internal async Task SwitchAsync(DatabaseConfiguration config, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var previous = store.Current;
            logger.LogInformation("Switching DynCMS to {Database}", config.Describe(store.BasePath));

            _ready = false;
            store.Save(config);
            ReleaseConnections();
            ResetIdentity();
            try
            {
                await InitializeCoreAsync(ct);
                _ready = true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Initialisation on the new database failed; restoring the previous configuration");
                if (previous is null)
                {
                    store.Delete();
                    throw;
                }

                store.Save(previous);
                ReleaseConnections();
                ResetIdentity();
                try
                {
                    await InitializeCoreAsync(CancellationToken.None);
                    _ready = true;
                }
                catch (Exception rollback)
                {
                    logger.LogCritical(rollback, "Re-initialising the previous database failed as well; a restart is required");
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Points the analytics store at another database (or back at the primary one when <paramref name="analytics"/>
    /// is null) without a restart or a sign-out: creates the tables there, optionally copies the recorded history
    /// across (and removes it from the old store), then saves the configuration. The worker is paused meanwhile so no
    /// page view is written to the old store while it is being copied. Returns null when nothing changed.
    /// </summary>
    internal async Task<AnalyticsMigrationResult?> SwitchAnalyticsStoreAsync(DatabaseConfiguration? analytics, AnalyticsHistoryAction history, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = store.Current ?? throw new DynCmsNotConfiguredException();
            if (analytics is not null && analytics.SameTargetAs(current, store.BasePath)) analytics = null;

            var oldTarget = current.Analytics ?? current;
            var newTarget = analytics ?? current;
            var next = current.WithAnalytics(analytics);
            if (oldTarget.SameTargetAs(newTarget, store.BasePath))
            {
                store.Save(next);   // credentials or the like may have changed
                return null;
            }

            logger.LogInformation("Moving the analytics store from {Old} to {New}", oldTarget.Describe(store.BasePath), newTarget.Describe(store.BasePath));
            analyticsQueue.Paused = true;
            try
            {
                await AnalyticsStoreMigrator.EnsureSchemaAsync(newTarget, store.BasePath, ct);
                // Either way the old store ends up without analytics rows: leaving the primary database drops the two
                // tables so content backups stay lean; leaving a dedicated analytics database empties them.
                var dropTables = current.Analytics is null;
                AnalyticsMigrationResult result;
                if (history == AnalyticsHistoryAction.Move)
                {
                    result = await AnalyticsStoreMigrator.CopyAsync(oldTarget, newTarget, store.BasePath, clearSource: true, dropSourceTables: dropTables, logger, ct);
                }
                else
                {
                    await AnalyticsStoreMigrator.ClearAsync(oldTarget, store.BasePath, dropTables, ct);
                    result = new AnalyticsMigrationResult(0, 0, true);
                }
                store.Save(next);
                ReleaseConnections();
                analyticsQueue.NotifyStoreChanged();
                return result;
            }
            finally
            {
                analyticsQueue.Paused = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Old history left in the primary database while analytics runs in its own: moves it into the analytics store or
    /// deletes it, and drops the tables from the primary database either way. Returns null when there was none.
    /// </summary>
    internal async Task<AnalyticsMigrationResult?> ResolveLeftoverHistoryAsync(AnalyticsHistoryAction history, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = store.Current ?? throw new DynCmsNotConfiguredException();
            if (current.Analytics is null) return null;
            var leftover = await AnalyticsStoreMigrator.CountAsync(current, store.BasePath, ct);
            if (leftover is null) return null;

            analyticsQueue.Paused = true;
            try
            {
                if (history == AnalyticsHistoryAction.Move)
                {
                    await AnalyticsStoreMigrator.EnsureSchemaAsync(current.Analytics, store.BasePath, ct);
                    var result = await AnalyticsStoreMigrator.CopyAsync(current, current.Analytics, store.BasePath, clearSource: true, dropSourceTables: true, logger, ct);
                    analyticsQueue.NotifyStoreChanged();
                    return result;
                }
                await AnalyticsStoreMigrator.ClearAsync(current, store.BasePath, dropTables: true, ct);
                logger.LogInformation("Deleted {Views} old page views left in the primary database", leftover.PageViews);
                return new AnalyticsMigrationResult(0, 0, true);
            }
            finally
            {
                analyticsQueue.Paused = false;
                ReleaseConnections();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Tells the analytics worker that the store's content was replaced (restore) so it drops its caches.</summary>
    internal void NotifyAnalyticsStoreChanged()
    {
        ReleaseConnections();
        analyticsQueue.NotifyStoreChanged();
    }

    /// <summary>
    /// Deletes the configuration file and marks the runtime as not ready, so <c>UseDynCmsSetup</c> sends every
    /// request to the setup page again. The database itself is left untouched.
    /// </summary>
    internal async Task ResetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            logger.LogWarning("Database configuration {Path} removed from the back office; setup is required again", store.FilePath);
            _ready = false;
            store.Delete();
            ReleaseConnections();
            ResetIdentity();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-runs the initialiser after the database content was replaced (restore from backup): adds tables the
    /// backup may lack, reloads stored templates and the identity framework caches. Startup tasks do not run.
    /// </summary>
    internal async Task RefreshAfterRestoreAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDynCmsInitializer>().InitializeAsync(ct);
            if (identity is CmsIdentity concrete) concrete.RefreshCache();
            analyticsQueue.NotifyStoreChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops pooled connections so the previous database (for SQLite: its file) is released.</summary>
    private static void ReleaseConnections()
    {
        SqliteConnection.ClearAllPools();
        SqlConnection.ClearAllPools();
    }

    private void ResetIdentity()
    {
        if (identity is CmsIdentity concrete) concrete.Reset();
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDynCmsInitializer>().InitializeAsync(ct);

        foreach (var task in scope.ServiceProvider.GetServices<IDynCmsStartupTask>())
        {
            logger.LogDebug("Running startup task {Task}", task.GetType().Name);
            await task.RunAsync(ct);
        }
    }
}
