using DynCMS.Core.Analytics;
using DynCMS.Core.Data;
using DynCMS.Core.Plugins;
using DynCMS.Core.Security;
using DynCMS.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace DynCMS.Core;

public static class ApplicationBuilderExtensions
{
    /// <summary>Request path of the first-run database setup page.</summary>
    public const string SetupPath = "/setup";

    /// <summary>Route of the backup download endpoint mapped by <see cref="MapDynCmsBackups"/>; <c>{name}</c> is the backup file name.</summary>
    public const string BackupDownloadPattern = "/admin/data/backups/download/{name}";

    /// <summary>Route of the analytics CSV export mapped by <see cref="MapDynCmsAnalyticsExport"/> (query: <c>from</c>, <c>to</c> as ISO dates, <c>bots=true</c>).</summary>
    public const string AnalyticsExportPattern = "/admin/analytics/export";

    /// <summary>
    /// Initialises the database when <c>dyncms.database.json</c> exists in the application root: creates the
    /// schema, the identity framework data and the media folder, then runs the registered startup tasks.
    /// When the file is missing nothing happens and the application serves the setup page instead
    /// (see <see cref="UseDynCmsSetup"/>). Call once at startup before serving requests.
    /// </summary>
    /// <returns><c>true</c> when the database is ready, <c>false</c> when setup is still required.</returns>
    public static Task<bool> InitializeDynCmsAsync(this IHost host, CancellationToken ct = default) =>
        host.Services.GetRequiredService<IDynCmsRuntime>().TryInitializeAsync(ct);

    /// <summary>
    /// While the database has not been configured, sends every page request to <c>/setup</c> and answers
    /// non-page requests (APIs, anything that does not accept HTML) with <c>503 Service Unavailable</c>;
    /// once it has, keeps <c>/setup</c> from being opened again. Add it early in the pipeline, before
    /// authentication. Framework, static and Blazor circuit requests are always let through.
    /// </summary>
    public static IApplicationBuilder UseDynCmsSetup(this IApplicationBuilder app)
    {
        var runtime = app.ApplicationServices.GetRequiredService<IDynCmsRuntime>();

        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var isSetup = path.StartsWithSegments(SetupPath);

            if (runtime.IsReady)
            {
                if (isSetup)
                {
                    context.Response.Redirect("/");
                    return;
                }
                await next(context);
                return;
            }

            if (!isSetup && !IsInfrastructure(path))
            {
                await SendToSetupAsync(context);
                return;
            }

            try
            {
                await next(context);
            }
            catch (DynCmsNotConfiguredException) when (!context.Response.HasStarted)
            {
                // A request that looked like a static file (e.g. /favicon.ico) fell through to a page that
                // needs the database. Send it to the setup page instead of failing with a 500.
                context.Response.Clear();
                await SendToSetupAsync(context);
            }
        });
    }

    /// <summary>A browser navigation is redirected to the setup page; anything else (an API call) gets a 503.</summary>
    private static Task SendToSetupAsync(HttpContext context)
    {
        if (IsNavigation(context.Request))
        {
            context.Response.Redirect(SetupPath);
            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync($"DynCMS is not set up yet. Open {SetupPath} in a browser to configure the database.");
    }

    private static bool IsNavigation(HttpRequest request) =>
        request.Headers["Sec-Fetch-Mode"] == "navigate" ||
        request.Headers.Accept.Any(a => a?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsInfrastructure(PathString path) =>
        path.StartsWithSegments("/_framework") ||
        path.StartsWithSegments("/_content") ||
        path.StartsWithSegments("/_blazor") ||
        path.StartsWithSegments("/_vs") ||
        Path.HasExtension(path.Value);

    /// <summary>
    /// Serves uploaded media files from the configured media folder as an endpoint
    /// (for example <c>/media/{**path}</c>). Being an endpoint, it takes precedence over a
    /// Blazor catch-all page route, which plain static file middleware would lose to.
    /// </summary>
    public static IEndpointConventionBuilder MapDynCmsMedia(this IEndpointRouteBuilder endpoints)
    {
        var paths = endpoints.ServiceProvider.GetRequiredService<DynCmsPaths>();
        Directory.CreateDirectory(paths.MediaRootPath);

        var files = new PhysicalFileProvider(paths.MediaRootPath);
        var contentTypes = new FileExtensionContentTypeProvider();
        var pattern = paths.MediaRequestPath.TrimEnd('/') + "/{**path}";

        return endpoints.MapGet(pattern, (string path, HttpContext http) =>
        {
            var file = files.GetFileInfo(path);
            if (!file.Exists || file.IsDirectory || file.PhysicalPath is null)
                return Results.NotFound();

            if (!contentTypes.TryGetContentType(file.Name, out var contentType))
                contentType = "application/octet-stream";

            http.Response.Headers.CacheControl = "public,max-age=86400";
            return Results.File(file.PhysicalPath, contentType, lastModified: file.LastModified, enableRangeProcessing: true);
        }).AllowAnonymous();
    }

    /// <summary>
    /// Maps the MCP server (Streamable HTTP, default <c>/mcp</c>) that lets AI agents manage the site with the same
    /// tools the REST API offers. Requires authentication: agents send their API key as <c>Authorization: Bearer</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapDynCmsMcp(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DynCmsOptions>>().Value.Api;
        if (!options.McpEnabled) return endpoints;
        endpoints.MapMcp("/" + options.McpPath.Trim('/')).RequireAuthorization();
        return endpoints;
    }

    /// <summary>
    /// Plugs the plugin system into routing: the endpoints plugins map (they come and go with the plugins) and the
    /// static files of running plugins at <c>/_content/{plugin id}/…</c>. Plugin pages need no mapping: the
    /// <c>/admin/{**rest}</c> page of the back office and the site's catch-all page render them.
    /// </summary>
    public static IEndpointRouteBuilder MapDynCmsPlugins(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DynCmsOptions>>().Value.Plugins;
        if (!options.Enabled) return endpoints;

        var manager = endpoints.ServiceProvider.GetRequiredService<PluginManager>();
        manager.AttachEndpoints(endpoints);
        endpoints.DataSources.Add(endpoints.ServiceProvider.GetRequiredService<PluginEndpointDataSource>());

        var pattern = "/" + options.StaticAssetsRequestPath.Trim('/') + "/{plugin}/{**path}";
        endpoints.MapGet(pattern, (string plugin, string path, HttpContext http) => manager.ServeStaticFile(plugin, path, http))
            .AllowAnonymous()
            .ExcludeFromDescription();
        return endpoints;
    }

    /// <summary>
    /// Lets administrators download database backups listed on the back office Data tab. Only files that exist
    /// in the backup folder can be downloaded (SQL Server backups are written on the server, which may be another machine).
    /// </summary>
    public static IEndpointConventionBuilder MapDynCmsBackups(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet(BackupDownloadPattern, (string name, IDatabaseMaintenanceService maintenance) =>
        {
            var backup = maintenance.FindBackup(name);
            if (backup is null || !backup.IsLocalFile) return Results.NotFound();
            return Results.File(backup.FullPath, "application/octet-stream", backup.FileName, enableRangeProcessing: true);
        }).RequireAuthorization(new AuthorizeAttribute { Roles = CmsRoles.Admin });
    }

    /// <summary>
    /// Lets signed-in back-office users download the page views of a date range as CSV from the Analytics section.
    /// <c>from</c> and <c>to</c> are local dates (<c>yyyy-MM-dd</c>, inclusive); without them the last 30 days are exported.
    /// </summary>
    public static IEndpointConventionBuilder MapDynCmsAnalyticsExport(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet(AnalyticsExportPattern, async (string? from, string? to, bool? bots, IAnalyticsService analytics, HttpContext http, CancellationToken ct) =>
        {
            var range = DateTime.TryParse(from, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var first) &&
                        DateTime.TryParse(to, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var last)
                ? AnalyticsRange.Days(first, last)
                : AnalyticsRange.LastDays(30);

            var fileName = $"pageviews-{range.From:yyyyMMdd}-{range.To.AddDays(-1):yyyyMMdd}.csv";
            http.Response.ContentType = "text/csv; charset=utf-8";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            await using var writer = new StreamWriter(http.Response.Body, new System.Text.UTF8Encoding(false), leaveOpen: true);
            await analytics.ExportCsvAsync(range, bots == true, writer, ct);
            await writer.FlushAsync(ct);
        }).RequireAuthorization();
    }
}
