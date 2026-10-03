using DynCMS.Core.Analytics;
using DynCMS.Core.Data;
using DynCMS.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynCMS.Core.Services;

public interface IDynCmsInitializer
{
    /// <summary>Creates the content schema, initialises the identity framework and creates the media folder.</summary>
    Task InitializeAsync(CancellationToken ct = default);
}

public sealed class DynCmsInitializer(
    IDbContextFactory<DynCmsDbContext> factory,
    DatabaseConfigurationStore store,
    DynCmsPaths paths,
    ICmsIdentity identity,
    ITemplateService templates,
    ILanguageService languages,
    IDictionaryService dictionary,
    IAnalyticsSettingsService analyticsSettings,
    ISystemVersionService versions,
    IOptions<DynCmsOptions> options,
    ILogger<DynCmsInitializer> logger) : IDynCmsInitializer
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var config = store.Current ?? throw new DynCmsNotConfiguredException();
        Directory.CreateDirectory(paths.MediaRootPath);

        if (config.Provider == DatabaseProvider.Sqlite)
            Directory.CreateDirectory(Path.GetDirectoryName(config.Sqlite!.ResolvePath(store.BasePath))!);

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var created = await DatabaseSchema.EnsureTablesAsync(db, ct);
            logger.LogInformation("DynCMS content schema on {Database} {State}", config.Describe(store.BasePath), created ? "created" : "ready");
        }

        // Remember which schema version prepared this database, so the System page and plugins can tell.
        var version = await versions.EnsureRecordedAsync(ct);
        switch (version.State)
        {
            case DatabaseVersionState.Upgraded:
                logger.LogInformation("Database schema brought from version {From} to {To} (DynCMS {Application})", version.DatabaseSchemaVersion, version.ExpectedSchemaVersion, version.ApplicationVersion);
                break;
            case DatabaseVersionState.NewerThanApplication:
                logger.LogWarning("The database was prepared by a newer DynCMS (schema {Database}, this build expects {Expected}). Update DynCMS to {Application} or newer before editing content", version.DatabaseSchemaVersion, version.ExpectedSchemaVersion, version.DatabaseApplicationVersion);
                break;
        }

        // Analytics tables: in the primary database, or in the separately configured analytics database. A separate
        // analytics database that is down must not keep the site from starting; page views are dropped until it is back.
        var analyticsConfig = config.Analytics ?? config;
        try
        {
            await AnalyticsStoreMigrator.EnsureSchemaAsync(analyticsConfig, store.BasePath, ct);
            logger.LogInformation("DynCMS analytics schema on {Database} ready{Shared}", analyticsConfig.Describe(store.BasePath), config.Analytics is null ? " (shared with the content)" : string.Empty);
            if (config.Analytics is not null && await AnalyticsStoreMigrator.CountAsync(config, store.BasePath, ct) is { } leftover)
                logger.LogWarning("The primary database still holds {Views} old page views although analytics has its own database; Data → Analytics database offers to move or delete them", leftover.PageViews);
        }
        catch (Exception ex) when (config.Analytics is not null && ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The analytics database {Database} could not be initialised; page views are not recorded until it is reachable", analyticsConfig.Describe(store.BasePath));
        }

        // Every site has at least one language: the default one, served without a URL prefix.
        var language = await languages.EnsureDefaultAsync(options.Value.DefaultCulture, ct);
        logger.LogDebug("Default language: {Language} ({IsoCode})", language.Name, language.IsoCode);

        // Stored (Liquid) templates become selectable and renderable next to the component templates.
        await templates.RefreshRegistryAsync(ct);

        // The dictionary is answered from memory while rendering; load it once now.
        await dictionary.RefreshAsync(ct);

        // Analytics settings live in the database (Settings table) and are consulted on every page view.
        await analyticsSettings.ReloadAsync(ct);

        if (identity is CmsIdentity concrete) concrete.Initialize();
    }
}
