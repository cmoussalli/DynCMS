using DynCMS.Plugin.Forms.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[assembly: DynCmsMinimumVersion("0.1.0")]

namespace DynCMS.Plugin.Forms;

/// <summary>
/// Forms for the website. Administrators build forms under <c>/admin/forms</c>; each form is written to a stored
/// Liquid <em>partial template</em> called <c>form-{alias}</c>. A page template shows it with
/// <c>{% render 'form-contact' %}</c>. A script served by the plugin (added to every page's head through the
/// <c>site.head</c> slot) submits the form without a page reload; without JavaScript it is an ordinary POST.
/// </summary>
public sealed class FormsPlugin : DynCmsPlugin
{
    public override string Icon => "list";

    public override IReadOnlyList<PluginMenuItem> MenuItems =>
    [
        new("Forms", "/admin/forms", Icon: "list")
    ];

    /// <summary>Stylesheet and script for the rendered forms, on every site page.</summary>
    public override IReadOnlyList<PluginUiExtension> UiExtensions =>
    [
        new(PluginSlots.SiteHead, typeof(FormsSiteHead))
    ];

    public override void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.Configure<FormsOptions>(context.Configuration);
        services.AddDbContextFactory<FormsDbContext>(o => o.UseSqlite(context.SqliteConnectionString("forms.db")));
        services.AddScoped<FormsService>();
        services.AddSingleton<SubmissionThrottle>();
    }

    public override async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var factory = context.Services.GetRequiredService<IDbContextFactory<FormsDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Database.EnsureCreatedAsync(ct);
            // EnsureCreated leaves an existing database alone: add the Theme column to forms saved before themes existed.
            var hasTheme = false;
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Forms') WHERE name = 'Theme'";
                hasTheme = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
            }
            if (!hasTheme)
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Forms\" ADD COLUMN \"Theme\" TEXT NOT NULL DEFAULT 'default'", ct);
            await using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Forms') WHERE name = 'CustomCss'";
                if (Convert.ToInt32(await cmd2.ExecuteScalarAsync(ct)) == 0)
                    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Forms\" ADD COLUMN \"CustomCss\" TEXT NULL", ct);
            }
        }

        // Forms keep their partial views while the plugin is stopped (emptied); bring them back to life.
        using var scope = context.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FormsService>().SyncPartialsAsync(live: true, ct);
    }

    public override async Task StopAsync(IPluginContext context, CancellationToken ct)
    {
        try
        {
            using var scope = context.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<FormsService>().SyncPartialsAsync(live: false, ct);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "Could not empty the form partials on stop");
        }
    }

    public override void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context)
    {
        var api = endpoints.MapGroup(FormHtml.SubmitPath);

        // Public: what the rendered form posts to. Form-encoded; answers JSON to the script, a small page otherwise.
        api.MapPost("/{alias}/submit", SubmitAsync).AllowAnonymous();

        // Back office: the export of a form's submissions.
        api.MapGet("/{id:guid}/export.csv", async (Guid id, FormsService forms, CancellationToken ct) =>
            {
                var csv = await forms.ExportCsvAsync(id, ct);
                if (csv is null) return Results.NotFound();
                var form = await forms.GetAsync(id, ct);
                return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"{form!.Alias}-submissions.csv");
            })
            .RequireAuthorization(new AuthorizeAttribute { Roles = string.Join(',', CmsRoles.All) });
    }

    private static async Task<IResult> SubmitAsync(string alias, HttpContext http, FormsService forms, SubmissionThrottle throttle, IOptions<FormsOptions> options, CancellationToken ct)
    {
        var request = http.Request;
        var wantsJson = request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase);
        var referer = request.Headers.Referer.ToString();

        IResult Answer(SubmitResult r, string? redirect = null)
        {
            if (wantsJson) return Results.Json(new { ok = r.Ok, message = r.Message, errors = r.Errors, redirect }, statusCode: r.StatusCode);
            return Results.Content(ResultPage(r, redirect, referer), "text/html; charset=utf-8", System.Text.Encoding.UTF8, r.StatusCode);
        }

        if (!request.HasFormContentType || request.ContentLength is > 64 * 1024)
            return Answer(SubmitResult.Refused("The submission was not understood.", StatusCodes.Status400BadRequest));

        var client = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!throttle.TryAcquire(client, options.Value.MaxSubmissionsPerMinute))
            return Answer(SubmitResult.Refused("Too many submissions. Please wait a minute and try again.", StatusCodes.Status429TooManyRequests));

        var form = await request.ReadFormAsync(ct);
        var values = form.ToDictionary(kv => kv.Key, kv => kv.Value.Where(v => v is not null).Select(v => v!).ToArray(), StringComparer.Ordinal);

        var result = await forms.SubmitAsync(alias, values, referer.Length > 0 ? referer : null, ct);
        string? redirect = null;
        if (result.Ok && await forms.GetByAliasAsync(alias, ct) is { RedirectUrl: { } target }) redirect = target;
        return Answer(result, redirect);
    }

    // The answer when the browser posted the form itself (no JavaScript).
    private static string ResultPage(SubmitResult r, string? redirect, string referer)
    {
        var back = Uri.TryCreate(referer, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.ToString() : "/";
        var message = r.Ok ? "Thank you! Your message has been sent." : r.Message ?? "The form could not be sent.";
        var errors = r.Errors is { Count: > 0 } ? "<ul>" + string.Join("", r.Errors.Select(e => $"<li><strong>{FormHtml.Enc(e.Key)}</strong>: {FormHtml.Enc(e.Value)}</li>")) + "</ul>" : string.Empty;
        var refresh = r.Ok && redirect is not null ? $"<meta http-equiv=\"refresh\" content=\"0;url={FormHtml.Enc(redirect)}\">" : string.Empty;
        return $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">{refresh}<title>{(r.Ok ? "Sent" : "Not sent")}</title>" +
               "<style>body{font:16px/1.5 system-ui,sans-serif;max-width:560px;margin:15vh auto;padding:0 20px}</style></head>" +
               $"<body><h1>{(r.Ok ? "Sent" : "Not sent")}</h1><p>{FormHtml.Enc(message)}</p>{errors}<p><a href=\"{FormHtml.Enc(back)}\">Go back</a></p></body></html>";
    }
}
