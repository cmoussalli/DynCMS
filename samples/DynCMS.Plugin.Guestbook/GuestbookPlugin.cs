using DynCMS.Plugin.Guestbook.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// The lowest DynCMS version this plugin works with. DynCMS refuses to attach the plugin to an older site and
// asks the administrator to update DynCMS first.
[assembly: DynCmsMinimumVersion("0.1.0")]

namespace DynCMS.Plugin.Guestbook;

/// <summary>
/// The plugin entry point: DynCMS finds this class in the uploaded assembly. Name, description, version and author
/// come from the project file (see <see cref="DynCmsPlugin"/>); the id defaults to the assembly name.
/// </summary>
public sealed class GuestbookPlugin : DynCmsPlugin
{
    public override string Icon => "message";

    /// <summary>A link in the back-office top bar; the page it points to is <c>Pages/GuestbookAdmin.razor</c>.</summary>
    public override IReadOnlyList<PluginMenuItem> MenuItems =>
    [
        new("Guestbook", "/admin/guestbook", Icon: "message")
    ];

    /// <summary>A property editor: document types can use it for any text property (Settings → Document types).</summary>
    public override IReadOnlyList<PropertyEditorDefinition> PropertyEditors =>
    [
        new PropertyEditorDefinition
        {
            Alias = "Guestbook.Stars",
            Name = "Star rating",
            Description = "Click to rate from 1 to the configured maximum (provided by the Guestbook plugin).",
            Icon = "star",
            ComponentType = typeof(StarRatingEditor),
            ConfigFields = [new PropertyEditorConfigField("max", "Maximum stars", "Between 1 and 10, default 5.", ConfigFieldType.Number)]
        }
    ];

    /// <summary>A component template: content can pick it as its template while the plugin runs.</summary>
    public override IReadOnlyList<TemplateDefinition> Templates =>
    [
        new TemplateDefinition("guestbook.page", "Guestbook page", typeof(GuestbookTemplate), Description: "A page that links to the guestbook (provided by the Guestbook plugin).")
    ];

    /// <summary>Components in the back-office dashboard and content editor, and in the head and at the end of every site page.</summary>
    public override IReadOnlyList<PluginUiExtension> UiExtensions =>
    [
        new(PluginSlots.AdminDashboard, typeof(GuestbookDashboardWidget)),
        new(PluginSlots.AdminContentEditor, typeof(ContentInfoPanel), Roles: CmsRoles.Admin),
        new(PluginSlots.SiteHead, typeof(SiteHeadTags)),
        new(PluginSlots.SiteBodyEnd, typeof(SiteFooterLink))
    ];

    /// <summary>
    /// The plugin's services. They can be injected into the plugin's components (<c>@inject GuestbookService</c>),
    /// endpoints and controllers, and may depend on anything the host registers (logging, options, CMS services).
    /// </summary>
    public override void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        // Plugins:DynCMS.Plugin.Guestbook in appsettings.json, or settings.json in the plugin folder.
        services.Configure<GuestbookOptions>(context.Configuration);

        // The database lives in the plugin's private data folder and survives updates of the plugin.
        services.AddDbContextFactory<GuestbookDbContext>(o => o.UseSqlite(context.SqliteConnectionString("guestbook.db")));
        services.AddScoped<GuestbookService>();

        // Reacts to CMS changes: every ICmsEventHandler a plugin registers is called while the plugin runs.
        services.AddSingleton<ActivityLog>();
        services.AddSingleton<ICmsEventHandler, GuestbookEvents>();

        // Keyed services work inside plugins: [Inject(Key = "short")] IEntryFormatter.
        services.AddKeyedSingleton<IEntryFormatter, ShortEntryFormatter>("short");
        services.AddKeyedSingleton<IEntryFormatter, LongEntryFormatter>("long");
    }

    /// <summary>Runs on every start, after the services are available: create the database when it is missing.</summary>
    public override async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var factory = context.Services.GetRequiredService<IDbContextFactory<GuestbookDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
        context.Logger.LogInformation("Guestbook database: {Path}", context.GetDataPath("guestbook.db"));
    }

    /// <summary>Minimal-API endpoints. They exist while the plugin runs; an API controller is in <c>GuestbookStatsController</c>.</summary>
    public override void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context)
    {
        var api = endpoints.MapGroup("/api/plugins/guestbook");

        api.MapGet("/entries", async (GuestbookService guestbook, CancellationToken ct) => Results.Ok(await guestbook.GetApprovedAsync(ct)))
            .AllowAnonymous();

        api.MapPost("/entries", async (SignRequest body, GuestbookService guestbook, CancellationToken ct) =>
            {
                try
                {
                    var entry = await guestbook.SignAsync(body.Name, body.Message, ct);
                    return Results.Created($"/api/plugins/guestbook/entries/{entry.Id}", entry);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            })
            .AllowAnonymous();

        var admin = new AuthorizeAttribute { Roles = CmsRoles.Admin };
        api.MapPost("/entries/{id:int}/approve", async (int id, GuestbookService guestbook, CancellationToken ct) =>
                await guestbook.SetApprovedAsync(id, true, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(admin);
        api.MapDelete("/entries/{id:int}", async (int id, GuestbookService guestbook, CancellationToken ct) =>
                await guestbook.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(admin);
    }
}

public sealed record SignRequest(string Name, string Message);

/// <summary>Bound from the plugin's configuration (<c>Plugins:DynCMS.Plugin.Guestbook</c> or <c>settings.json</c>).</summary>
public sealed class GuestbookOptions
{
    /// <summary>New entries wait for an administrator before they show on the site.</summary>
    public bool RequireApproval { get; set; } = true;

    public int MaxMessageLength { get; set; } = 500;

    /// <summary>Heading shown on the public page.</summary>
    public string Title { get; set; } = "Guestbook";
}
