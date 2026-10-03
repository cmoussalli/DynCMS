# DynCMS Developer Guide

How DynCMS is put together, where the seams are, and how to extend it without forking it.

The [README](README.md) describes what DynCMS does from an editor's point of view. This guide is the
counterpart for developers: architecture, lifecycle, every public service and option, the extension points,
conventions, and the constraints and known issues you should know before you build on it.

Everything in this guide was checked against the source on 2026-09-25 (version 0.1.0, .NET SDK 10.0.302).
Where the code and an older description disagreed, the code won. Where something is a known bug rather than a
design decision, it is listed in §23 rather than silently documented as behaviour.

---

## Table of contents

1. [Before you start](#1-before-you-start)
2. [Solution map](#2-solution-map)
3. [Boot sequence](#3-boot-sequence)
4. [Setup mode and the database configuration file](#4-setup-mode-and-the-database-configuration-file)
5. [Persistence](#5-persistence)
6. [The content model](#6-the-content-model)
7. [Service reference](#7-service-reference)
8. [Request to page: the rendering pipeline](#8-request-to-page-the-rendering-pipeline)
9. [Templates](#9-templates)
10. [Property editors](#10-property-editors)
11. [Media](#11-media)
12. [Security](#12-security)
13. [Management API and MCP server](#13-management-api-and-mcp-server)
14. [Backups, restore and database maintenance](#14-backups-restore-and-database-maintenance)
15. [Analytics](#15-analytics)
16. [Configuration reference](#16-configuration-reference)
17. [Extension points](#17-extension-points)
18. [Hosting DynCMS in your own application](#18-hosting-dyncms-in-your-own-application)
19. [The back office: routes and building blocks](#19-the-back-office-routes-and-building-blocks)
20. [The starter site and the archived demo](#20-the-starter-site-and-the-archived-demo)
21. [Conventions](#21-conventions)
22. [Constraints and gotchas](#22-constraints-and-gotchas)
23. [Known issues](#23-known-issues)
24. [Recipes](#24-recipes)
25. [Plugins](#25-plugins)

---

## 1. Before you start

**Prerequisites**

| | |
|---|---|
| SDK | .NET 10 (`dotnet --version` → `10.0.x`; the code was last built with 10.0.302) |
| Database | Nothing to install for SQLite; SQL Server 2016+ (or Azure SQL) for the other provider |
| Editor | Anything with C#/Razor support; the solution uses the `.slnx` format, so use a recent VS / Rider / `dotnet` CLI |

**Everyday commands**

```bash
dotnet build DynCMS.slnx              # whole solution — builds warning-clean; keep it that way
dotnet run --project src/DynCMS.Web   # the minimal host project (dnrun.config.json also names it as the startup project)
dotnet pack src/DynCMS.Core -o ./nupkg && dotnet pack src/DynCMS.UI -o ./nupkg && dotnet pack src/DynCMS.Host -o ./nupkg
```

The Web project listens on `http://localhost:5237` / `https://localhost:7117` (see
`src/DynCMS.Web/Properties/launchSettings.json`). The site is at `/`, the back office at `/admin`.

| Account | Username | Password | Role | Comes from |
|---|---|---|---|---|
| Administrator | `admin` | `Admin123!` | `Administrators` | `DynCms:Identity:AdminUserName` / `AdminPassword` in `appsettings.json` |

**What there is not.** There is no test project, no CI, and the working folder is not a git repository. The
`.gitignore` at the root covers `bin/`, `obj/`, `.vs/`, `.idea/`, `*.user`, `*.suo`, `App_Data/`, `*.db`,
`*.db-shm`, `*.db-wal`. It does **not** cover `dyncms.database.json` (see §4) or `mcp.md` (see §23).

**Starting over.** Delete `src/DynCMS.Web/dyncms.database.json` and `src/DynCMS.Web/App_Data/`. The next
start lands on `/setup`, and the new database gets the starter site (`options.SeedStarterSite = false` skips it).

---

## 2. Solution map

Besides the four site projects, `src/DynCMS.Plugins.Sdk` is the small package plugin authors reference (§25), and `samples/DynCMS.Plugin.Guestbook` is a complete plugin built on it; it is in the solution but nothing references it.

```
DynCMS.slnx
└── src
    ├── DynCMS.Plugins.Sdk (Razor class library)      ← the plugin contract + shared UI bits; no other DynCMS reference
    ├── DynCMS.Core   (class library, net10.0)        ← references Plugins.Sdk; no UI-framework dependency
    ├── DynCMS.UI     (Razor class library, net10.0)  ← references Core + Plugins.Sdk
    ├── DynCMS.Host   (Razor class library, net10.0)  ← references Core + UI + Plugins.Sdk; the NuGet package a site installs
    └── DynCMS.Web    (Blazor Web App, net10.0)       ← references Host; the minimal host project (the demo is archived, §20)
```

Dependencies point one way only: **Web → Host → UI → Core**. Core never references Blazor types, which is why a
property editor is described in Core as a `PropertyEditorDefinition` carrying an opaque `Type`, while the
actual component lives in UI.

`Directory.Build.props` sets the shared NuGet metadata (`Version` 0.1.0, `Authors` DynCMS, MIT, tags
`cms;blazor;aspnetcore;dyncms`). `DynCMS.Web` sets `IsPackable=false`. All projects are `net10.0`,
nullable and implicit usings on; the three libraries also set `LangVersion latest`.

### DynCMS.Core

| Folder | What lives there |
|---|---|
| `Models/` | `ContentType`, `PropertyType`, `ContentNode`, `ContentCulture`, `Language`, `DictionaryItem`, `MediaItem`, `Template`, `PublishedContent` |
| `Data/` | `DynCmsDbContext`, the provider-agnostic context factory, `DatabaseConfiguration*`, `DatabaseSchema`, setup and maintenance services |
| `Services/` | Content, document-type, language, dictionary, media and template services; the published read side; `ICultureContext`; the runtime and initializer |
| `Templates/` | Template registry, the Fluid/Liquid engine, preview, IntelliSense data, reference scanning, starter markup |
| `PropertyEditors/` | Editor registry and definitions (no components) |
| `Security/` | CMouss.IdentityFramework adapter, role/permission constants, the ASP.NET Core auth handler |
| `Api/` | The management REST API, the MCP tools, API keys and scopes, and `CmsManagement`, the application layer behind both (§13) |
| `Extensions/` | `AddDynCms(...)` plus the `IApplicationBuilder` / `IEndpointRouteBuilder` helpers |
| `Helpers/` | `Slug` (aliases and URL segments), `ContentValueConverter` (string ⇄ typed), `Paging` |

Packages: `Fluid.Core` 2.40.0 (Liquid), `Microsoft.EntityFrameworkCore.Sqlite` + `.SqlServer` 10.0.12,
`Microsoft.AspNetCore.OpenApi` 10.0.12, `CMouss.IdentityFramework` 1.5.2, `ModelContextProtocol.AspNetCore` 2.2.0.

### DynCMS.UI

Razor class library with `StaticWebAssetBasePath = _content/DynCMS.UI`. Package: `CMouss.IdentityFramework.BlazorUI`
1.5.2 (the cookie service and the identity management screens embedded in `/admin/users`).

| Folder | What lives there |
|---|---|
| `Admin/Pages/` | The routable back-office sections: `Dashboard`, `ContentSection`, `MediaSection`, `SettingsSection`, `UsersSection`, `DataSection`; their `_Imports.razor` applies `AdminLayout` and `[Authorize]` to all of them |
| `Admin/Components/` | `ContentEditor`, `ContentTree`, `ContentTreeNode`, `CreateContentDialog`, `PublishDialog`, `DocumentTypeEditor`, `PropertyDialog`, `TemplateEditor`, `ComponentTemplateView`, `MediaBrowser`, `LanguagesPanel`, `DictionaryPanel`, `DictionaryItemEditor`, `ApiKeysPanel`, `Modal`, `ConfirmDialog`, `ToastHost`, `Icon`, `RedirectToLogin`, `Data/` (`DataOverview`, `DataConfiguration`, `DataAnalyticsConfiguration`, `DatabaseConnectionFields`, `DataBackups`, `DataReset`, `DataFormat.cs`) and `Analytics/` (`AnalyticsOverview`, `AnalyticsPages`, `AnalyticsVisitors`, `AnalyticsMap`, `AnalyticsSources`, `AnalyticsTechnology`, `AnalyticsSettingsPanel`, `AnalyticsRangePicker`, `AnalyticsChart`, `AnalyticsBarList`, `Stat`, `AnalyticsFormat.cs`) |
| `Admin/PropertyEditors/` | The eleven built-in editor components plus `PropertyEditorBase` |
| `Admin/Layout/` | `AdminLayout` (top bar, nav, user card, toasts) and `BlankLayout` (login and setup) |
| `Admin/Auth/` | `Login`, `Logout` |
| `Rendering/` | `CmsContentRenderer`, `CmsLiquidTemplate`, `CmsDefaultTemplate`, `CmsImage`, `CmsPager`, `CmsTemplateBase`, `PublishedContentExtensions` |
| `Setup/` | The `/setup` page |
| `Services/` | `ToastService` |
| `wwwroot/` | `dyncms-admin.css` (73 KB, the whole back office), `dyncms-admin.js` (rich text, clipboard), `dyncms-liquid-editor.js` (the code editor) |

`DynCmsUi` (static): `Assembly`, `AdminStylesheet = "_content/DynCMS.UI/dyncms-admin.css"`, `AdminBasePath = "/admin"`,
`LoginPath = "/admin/login"`.

### DynCMS.Host

Razor class library with `StaticWebAssetBasePath = _content/DynCMS.Host`. It is the package a site installs:
everything that used to be host boilerplate lives here, and `DynCmsHost.RunAsync(args)` is a complete program.

| File | What lives there |
|---|---|
| `DynCmsHost.cs` | `RunAsync(args, configureOptions?, configureCms?)`, `Assembly`, `ShellStylesheet`, `SiteStylesheet` |
| `DynCmsHostExtensions.cs` | `builder.AddDynCmsHost(...)` (services) and `app.UseDynCmsHost()` / `UseDynCmsHost<TRoot>()` (pipeline) |
| `DynCmsHostOptions.cs` | Layout, language, stylesheets/scripts, router assemblies, starter site and pipeline switches; `GetRouterAssemblies()` (§16) |
| `DynCmsInitializationService.cs` | `IHostedService` that runs `IDynCmsRuntime.TryInitializeAsync` before the server listens |
| `StarterSiteSeeder.cs` | Startup task (internal): two document types, five Liquid templates and a published home page; with the *Demo blog* choice it hands over to `DemoBlogSeeder`; only when the database has no document types (§20) |
| `DemoBlogSeeder.cs`, `DemoBlogTemplates.cs` | The demo blog: `blog` and `article` document types, three more templates, media, dictionary labels, fourteen published articles and a draft (§20) |
| `Components/App.razor`, `Routes.razor` | The root component and the router; both read `DynCmsHostOptions` |
| `Components/Pages/` | `CmsPage` (`/` and `/{*Path}`), `Error` (`/Error`), `NotFound` (`/not-found`) |
| `Components/Layout/` | `SiteLayout` (default public layout) and `ReconnectModal` (with its collocated JS) |
| `wwwroot/` | `dyncms-shell.css` (reconnect dialog, error bar) and `dyncms-site.css` (the default theme, §21) |
| `build/DynCMS.Host.props` | Sets `BlazorDisableThrowNavigationException=true` and `RequiresAspNetWebAssets=true` in the consuming project; packed into `build/` and `buildTransitive/`. `DynCMS.Web` imports it by hand because a `ProjectReference` does not apply it |

It has no scoped CSS on purpose. The consuming app's `{ApplicationName}.styles.css` bundle is still linked, because
the identity framework's UI ships scoped styles, but its name is computed from `IHostEnvironment.ApplicationName`,
because a package cannot know the app's assembly name.

### DynCMS.Web

The minimal host project: the shape a new site has after `dotnet new blazor --empty` plus a reference to
`DynCMS.Host`. Six files: `DynCMS.Web.csproj` (a `ProjectReference` to Host plus an `Import` of its
`build/DynCMS.Host.props`, which the NuGet package would apply by itself), `Program.cs` (`AddDynCmsHost()` and
`UseDynCmsHost()`), `Components/_Imports.razor` (the usings for the site's future components), `appsettings.json`
(logging and `DynCms:Identity`), `appsettings.Development.json`, `Properties/launchSettings.json`. No pages, layout or
`wwwroot/`: the host supplies `App`, `Routes`, the layout, the pages and the CSS, and the first run seeds the starter site. A `Components/Templates/` folder is created the day the site gets its first component
template (§24). Until 2026-09-26 this project was the demo site; its templates, seeder, stylesheet and database were
moved unchanged to `_archive/DynCMS.Web-demo/` (§20).

---
## 3. Boot sequence

A site's `Program.cs` is two calls into `DynCMS.Host` (`src/DynCMS.Web/Program.cs` is exactly that:
`builder.AddDynCmsHost()`, `app.UseDynCmsHost()`). A site with its own stylesheet, templates and seeder chains them
in between, as the archived demo did:

```csharp
builder.AddDynCmsHost(options =>
    {
        options.SeedStarterSite = false;
        options.Stylesheets.Add("demo.css");
    })
    .AddTemplate<HomeTemplate>("home", "Home page")
    .AddTemplate<TextPageTemplate>("textPage", "Text page")
    .AddTemplate<ArticleListTemplate>("articleList", "Article list")
    .AddTemplate<ArticleTemplate>("article", "Article")
    .AddStartupTask<DemoSeeder>();

var app = builder.Build();
app.UseDynCmsHost();
app.Run();
```

### `AddDynCmsHost` (services)

`src/DynCMS.Host/DynCmsHostExtensions.cs`, in order:

1. `options.Validate()` (throws `InvalidOperationException` if `Layout` is not an `IComponent`), then
   `services.AddSingleton(options)` (`DynCmsHostOptions`).
2. `services.AddRazorComponents().AddInteractiveServerComponents()`.
3. `services.AddDynCms(o => configuration.GetSection(options.ConfigurationSection).Bind(o))` (Core, below).
4. `.AddDynCmsUI()` (UI, below).
5. `cms.AddStartupTask<StarterSiteSeeder>()` when `options.SeedStarterSite` (the default). Because it is registered
   *first*, it runs before any startup task you add.
6. `services.AddHostedService<DynCmsInitializationService>()`.

It returns the `IDynCmsBuilder`, so `.AddTemplate<T>(alias, name)`, `.AddPropertyEditor(...)` and
`.AddStartupTask<T>()` chain on.

**`AddDynCms`** (`src/DynCMS.Core/Extensions/ServiceCollectionExtensions.cs`) registers everything in Core. The exact
registrations, in order:

| Lifetime | Service → implementation | Registered with |
|---|---|---|
| Options | `DynCmsOptions` (+ your configure delegate) | `AddOptions` |
| Singleton (instances) | `ITemplateRegistry`, `IPropertyEditorRegistry` | `TryAddSingleton(instance)` |
| Singleton | `DynCmsPaths`, `DatabaseConfigurationStore`, `IDbContextFactory<DynCmsDbContext>` → `DynCmsDbContextFactory`, `DynCmsRuntime`, `IDynCmsRuntime` → the same `DynCmsRuntime`, `IDatabaseSetupService` → `DatabaseSetupService`, `IDatabaseMaintenanceService` → `DatabaseMaintenanceService`, `IMediaStorage` → `FileSystemMediaStorage`, `DictionaryCache`, `ILiquidTemplateEngine` → `FluidTemplateEngine`, `ICmsIdentity` → `CmsIdentity`, `IApiKeyService` → `ApiKeyService` | `TryAddSingleton` |
| Scoped | `ILanguageService`, `IDictionaryService`, `ICultureContext` → `CultureContext`, `IContentTypeService`, `IContentService`, `IPublishedContentQuery`, `IMediaService`, `IDynCmsInitializer`, `ITemplateService`, `IStoredTemplateRenderer`, `ITemplatePreviewService`, `ICmsAccess` → `CmsAccess`, `CmsManagement` | `TryAddScoped` |
| Plain adds | `AddAuthentication("DynCmsIdentity").AddScheme<AuthenticationSchemeOptions, DynCmsAuthenticationHandler>`, `AddAuthorization()`, `AddHttpContextAccessor()`, `ConfigureHttpJsonOptions` (ignore nulls, camel-case enums), `AddOpenApi()` + `AddOptions<OpenApiOptions>("v1")`, `AddMcpServer(...)` (stateless HTTP transport, `DynCmsMcpTools`) | not `TryAdd` |

Two consequences of that table:

- Registering your own implementation of a `TryAdd*` service *before* `AddDynCms` wins. That is how you replace
  media storage or any Core service (§17). The exceptions are the two registries: `AddDynCms` keeps its own
  `TemplateRegistry` / `PropertyEditorRegistry` instances inside the returned builder, so a pre-registered registry
  would be resolved by the app while `AddTemplate` / `AddPropertyEditor` write into the orphaned one. Do not
  replace the registries.
- `AddDynCms` has global side effects on the host: the JSON options apply to *every* minimal API in the app, an
  OpenAPI document named `v1` is registered, and the MCP server services are registered even when
  `Api.McpEnabled` is false (only the endpoint mapping is skipped). `AddStartupTask<T>` is a plain `AddScoped`, so
  registering the same task twice runs it twice.

**`AddDynCmsUI`** (`src/DynCMS.UI/Extensions/ServiceCollectionExtensions.cs`): `TryAddScoped<ToastService>`,
`AddCascadingAuthenticationState()`, `AddIdentityFrameworkBlazorUI()`, then it sets the identity framework's
process-wide statics (`IDFBlazorUIConfig.HomeURL = "/"`, `AuthHomeURL = "/admin"`, `LoginRedirectURL` and
`AfterLogoutRedirectURL = "/admin/login"`, `IDFBlazorUIAdminConfig.AdminRoleIds = ["Administrators"]`,
`AllowDeleteOperations = true`) and registers the eleven built-in property editors (§10).

### `UseDynCmsHost<TRoot>` (pipeline)

`app.UseDynCmsHost()` is `app.UseDynCmsHost<App>()`. The order, with the `DynCmsHostOptions` switch that controls
each line:

```csharp
if (!env.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);        // options.UseExceptionHandler
    app.UseHsts();                                                         // options.UseHsts
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();                                                 // options.UseHttpsRedirection
app.UseStaticFiles(); app.UseRouting();                                    // options.ServeWwwrootAtRuntime
app.UseDynCmsSetup();                                                      // the setup gate (§4) — before authentication
app.UseAuthentication(); app.UseAuthorization(); app.UseAntiforgery();

app.MapStaticAssets();
app.MapDynCmsMedia();      // /media/{**path}: an endpoint, so it beats the catch-all page route
app.MapDynCmsBackups();    // /admin/data/backups/download/{name}, Administrators only
app.MapDynCmsAnalyticsExport(); // /admin/analytics/export?from=&to=&bots= CSV (§15), signed-in users
app.MapDynCmsApi();        // /api/v1 + openapi.json (§13), unless Api.Enabled = false
app.MapDynCmsMcp();        // /mcp (§13), unless Api.McpEnabled = false
app.MapRazorComponents<TRoot>()
   .AddInteractiveServerRenderMode()
   .AddAdditionalAssemblies(options.GetRouterAssemblies(typeof(TRoot).Assembly));
```

Notes:

- When `ServeWwwrootAtRuntime` is off, neither `UseStaticFiles()` nor `UseRouting()` is called explicitly; routing
  then relies on `WebApplication`'s implicit insertion.
- `UseDynCmsSetup()` must come before authentication, because an unconfigured app has no user store to authenticate
  against.
- `MapDynCmsMedia()` is deliberately an *endpoint*, not static-file middleware: the site's catch-all page route
  (`/{*Path}`) would otherwise swallow media URLs. It is `AllowAnonymous`, sends `Cache-Control: public,max-age=86400`,
  supports range requests and serves unknown types as `application/octet-stream`.
- **Router assemblies.** `DynCmsHostOptions.GetRouterAssemblies(appAssembly)` returns `DynCMS.Host`, `DynCMS.UI`,
  the entry assembly (when `IncludeEntryAssembly`, default true) and `AdditionalAssemblies`, deduplicated and without
  `appAssembly`. The endpoint above and the `Router` in `Routes.razor` both call it; the two lists must match, or
  `/admin/...` 404s on one of the two routing paths (first load vs. enhanced navigation).

### Initialisation

`DynCmsInitializationService` is an `IHostedService`; hosted services start before Kestrel listens, so the database
is ready (or setup mode is known) before the first request. It awaits `IDynCmsRuntime.TryInitializeAsync`, which
returns `false` without throwing when there is **no** configuration file. If the file exists but the database cannot
be opened, or the file fails validation, the exception propagates and **the host fails to start**. Only a missing
file yields setup mode.

`DynCmsRuntime.TryInitializeAsync` → `DynCmsInitializer.InitializeAsync`, in this order:

1. Create the media folder (and the SQLite folder) if missing.
2. `DatabaseSchema.EnsureTablesAsync(db)`: create the database, then any missing table or column (§5).
3. `ILanguageService.EnsureDefaultAsync(options.DefaultCulture)`: make sure one language exists and is the default
   (`"en"` unless configured; see §6).
4. `ITemplateService.RefreshRegistryAsync()`: load stored Liquid templates into `ITemplateRegistry`.
5. `IDictionaryService.RefreshAsync()`: load the dictionary snapshot into `DictionaryCache`.
6. `CmsIdentity.Initialize()`: configure `IDFManager`, create the identity tables and master data, ensure the
   `Editors` role, the CMS entities/actions and the editor permissions (§12).

`DynCmsRuntime` then runs every registered `IDynCmsStartupTask` inside one service scope, in registration order.
The whole sequence is guarded by a `SemaphoreSlim`, and `IsReady` flips only on success.

```csharp
public interface IDynCmsRuntime
{
    bool IsReady { get; }
    bool SetupRequired { get; }              // not ready, no configuration loaded and no file on disk
    string ConfigurationFilePath { get; }
    Task<bool> TryInitializeAsync(CancellationToken ct = default);
}

public interface IDynCmsStartupTask { Task RunAsync(CancellationToken ct = default); }
public interface IDynCmsInitializer  { Task InitializeAsync(CancellationToken ct = default); }

public interface IDynCmsBuilder
{
    IServiceCollection Services { get; }
    ITemplateRegistry Templates { get; }
    IPropertyEditorRegistry PropertyEditors { get; }
    IDynCmsBuilder AddTemplate<TComponent>(string alias, string? name = null);   // name defaults to typeof(T).Name
    IDynCmsBuilder AddPropertyEditor(PropertyEditorDefinition definition);
    IDynCmsBuilder AddStartupTask<TTask>() where TTask : class, IDynCmsStartupTask;
}
```

`DynCmsRuntime` is `internal sealed`; the runtime database operations the back office offers are `internal` on it
and reachable from host code only through `IDatabaseSetupService` and `IDatabaseMaintenanceService` (§14):

| Method | Used by | Effect |
|---|---|---|
| `InitializeAfterSetupAsync` | `/setup` | First initialisation right after the configuration file is written; runs startup tasks |
| `SwitchAsync` | Data → Configuration | Save a new config, clear all SQLite and SQL Server connection pools, reset identity, re-initialise (startup tasks run against the new database). On failure it rolls back to the previous config, or deletes the file when there was none, and rethrows; if the rollback itself fails it logs *Critical* and a restart is required |
| `ResetAsync` | Data → Reset | Delete `dyncms.database.json`, clear pools, reset identity, return to setup mode. The database itself is left alone |
| `RefreshAfterRestoreAsync` | Data → Backups | Re-run the initializer and refresh identity caches after a restore. Startup tasks do **not** re-run; `IsReady` never drops |

---

## 4. Setup mode and the database configuration file

DynCMS has no connection string in `appsettings.json`. The database is chosen at runtime and persisted to
`dyncms.database.json` in the content root (`DynCmsOptions.BasePath` / `DatabaseConfigFile` move it):

```json
{ "Provider": "Sqlite",    "Sqlite":    { "DatabasePath": "App_Data/dyncms.db" } }
```

```json
{ "Provider": "SqlServer", "SqlServer": { "Server": "localhost", "Port": 1433, "Database": "DynCMS",
                                          "UserName": "sa", "Password": "…", "TrustServerCertificate": true } }
```

An optional `Analytics` entry (same shape, either provider) keeps visitor analytics in a database of its own; without
it the analytics tables live in the primary database (§15):

```json
{ "Provider": "Sqlite", "Sqlite": { "DatabasePath": "App_Data/dyncms.db" },
  "Analytics": { "Provider": "Sqlite", "Sqlite": { "DatabasePath": "App_Data/dyncms-analytics.db" } } }
```

`DatabaseRole` (`Primary`, `Analytics`) names the two; `DatabaseConfigurationStore.Resolve(role)` returns the
configuration a role uses (the analytics entry falls back to the primary one, and an entry that points at the primary
database is normalised away). The setup page offers the split as its last step; the Data tab changes it later.

**The file holds the SQL Server password in clear text. Protect it exactly like `appsettings.json` and keep
it out of source control.** `.gitignore` does not cover it. `src/DynCMS.Web` ships without one, so a fresh
checkout starts on `/setup`; the old demo's copy is in `_archive/DynCMS.Web-demo/`.

`DatabaseConfiguration` (`src/DynCMS.Core/Data/DatabaseConfiguration.cs`): `Provider` (`DatabaseProvider.Sqlite` |
`SqlServer`), `Sqlite?` (`DatabasePath`, default `App_Data\dyncms.db`, relative to the base path), `SqlServer?`
(`Server`, `Port` 1433, `Database`, `UserName`, `Password`, `TrustServerCertificate`), `Validate()`,
`BuildConnectionString(basePath)`, `Describe(basePath)` (never includes the password). The SQL Server connection string
is fixed to `DataSource="{Server},{Port}"`, `Encrypt=Mandatory`, `ConnectTimeout=15`, `ApplicationName=DynCMS`,
`MultipleActiveResultSets=true`; named instances without a port are not supported. Database names must be ≤ 128
characters, start with a letter or `_` and contain only `[A-Za-z0-9_-]`.

`DatabaseConfigurationStore` (singleton) is the single source of truth in the process: `FilePath`, `BasePath`,
`Exists`, `Current`, `Load()`, `Save(config)`, `Delete()`. `DynCmsDbContextFactory` rebuilds its `DbContextOptions`
whenever `Current` changes by reference, which is what lets the back office switch databases without a restart.
Any code that asks the factory for a context while `Current` is null gets a `DynCmsNotConfiguredException`
(a `public sealed` subclass of `InvalidOperationException`; message: "DynCMS has no database configuration yet. Open
/setup to configure the database.").

### The setup gate

`UseDynCmsSetup()` (`src/DynCMS.Core/Extensions/ApplicationBuilderExtensions.cs`), per request:

- runtime ready → let it through, but bounce `/setup` back to `/`;
- not ready, and the path is `/setup` or infrastructure (`/_framework`, `/_content`, `/_blazor`, `/_vs`, anything
  with a file extension) → let it through;
- not ready and the request is a navigation (`Sec-Fetch-Mode: navigate`, or `Accept` contains `text/html`) →
  `302` to `/setup`;
- not ready and anything else (API calls, fetches) → `503 text/plain` "DynCMS is not set up yet. Open /setup in a
  browser to configure the database.";
- a request that slipped through and hit the database anyway throws `DynCmsNotConfiguredException`, which the
  middleware converts into the same redirect/503 when the response has not started.

`ApplicationBuilderExtensions.SetupPath` (`"/setup"`) and `BackupDownloadPattern`
(`"/admin/data/backups/download/{name}"`) are public constants.

### The setup service

The `/setup` page (`src/DynCMS.UI/Setup/DatabaseSetup.razor`, `BlankLayout`, anonymous) collects a
`DatabaseSetupRequest` and calls `IDatabaseSetupService`:

```csharp
public enum DatabaseSetupMode { NewDatabase, ExistingDatabase }

public sealed class DatabaseSetupRequest
{
    public DatabaseSetupMode Mode = NewDatabase;  public DatabaseProvider Provider = Sqlite;
    public StarterContent StarterContent = EmptySite;   // EmptySite | DemoBlog — what the seeder puts in an empty database (§20)
    public string SqlitePath = @"App_Data\dyncms.db";
    public string Server = "localhost"; public int Port = 1433; public string Database = "DynCMS";
    public string UserName = ""; public string Password = ""; public bool TrustServerCertificate = true;
    public bool CreateDatabase = true;            // SQL Server, new-database mode: CREATE DATABASE through master
    public DatabaseConfiguration ToConfiguration();
}

public sealed record DatabaseTestResult(bool Success, string Message, IReadOnlyList<string> Details);

public interface IDatabaseSetupService
{
    Task<DatabaseTestResult> TestConnectionAsync(DatabaseSetupRequest request, CancellationToken ct = default);
    Task<DatabaseTestResult> CompleteSetupAsync(DatabaseSetupRequest request, CancellationToken ct = default);
}
```

`TestConnectionAsync` semantics per mode:

| Provider / mode | What it checks |
|---|---|
| SQLite, new | The file must **not** exist. It creates the target directory and writes and deletes a probe file (`.dyncms-write-test-{guid}`) to prove the folder is writable — the one place "test" touches the disk |
| SQLite, existing | The file must exist; it is opened read-only, `PRAGMA schema_version` is read and the schema inspected |
| SQL Server, new, create | Connects to `master`, `SELECT DB_ID(@name)`; fails if the database exists |
| SQL Server, otherwise | Connects to the database (error 4060 gets a friendly message), reads `@@VERSION`, inspects the schema |

Schema inspection uses `DatabaseSchema.Inspect`: an **empty** schema is fine for a new database, a **complete**
DynCMS schema is fine for an existing one, and a **partial** one (some DynCMS tables present) always fails with
"Remove the leftover tables or choose another database." New-database mode on a non-empty schema also fails.

`CompleteSetupAsync` refuses when the runtime is already ready or a configuration is already loaded, re-runs the
connection test, optionally runs `CREATE DATABASE` (command timeout 120 s), writes the configuration file, then
calls `DynCmsRuntime.InitializeAfterSetupAsync`. If initialisation throws, the file is deleted again so the setup
page stays reachable. On success the page force-loads `/admin/login`.

---

## 5. Persistence

### Tables

`DynCmsDbContext` (`src/DynCMS.Core/Data/DynCmsDbContext.cs`) configures:

| Entity → table | Indexes | Max lengths | JSON columns | Relations / delete | Computed (ignored) |
|---|---|---|---|---|---|
| `ContentType` → `ContentTypes` | `Alias` unique | Alias 100, Name 200, Icon 50 | `AllowedChildTypeAliases`, `AllowedTemplateAliases` (`List<string>`) | `Properties` 1:n via `PropertyType.ContentTypeId`, **cascade** | `Groups` |
| `PropertyType` → `PropertyTypes` | `(ContentTypeId, Alias)` unique | Alias 100, Name 200, EditorAlias 100, GroupName 100 | `Config` (`Dictionary<string,string>`) | | |
| `ContentNode` → `ContentNodes` | `ParentId`; `Path`; `(ParentId, UrlSegment)` | Name 255, UrlSegment 255 | `DraftValues`, `PublishedValues` (`Dictionary<string,string?>`), `Cultures` (`Dictionary<string,ContentCulture>`, keys re-wrapped case-insensitively on load) | `ContentType` n:1, **restrict** | `AncestorIds`, `HasPendingChanges`, `VariesByCulture`, `ExistingCultures`, `PublishedCultures` |
| `Language` → `Languages` | `IsoCode` unique | IsoCode 20, Name 200, FallbackIsoCode 20 | | | `UrlPrefix`, `Culture` |
| `DictionaryItem` → `DictionaryItems` | `Key` unique; `ParentId` | Key 200 | `Translations` (`Dictionary<string,string?>`) | | `Level`, `TranslatedCultures` |
| `MediaItem` → `MediaItems` | `ParentId` | Name 255 | | | `IsImage` |
| `Template` → `Templates` | `Alias` unique | Alias 100, Name 200, Description 1000, Role 20 | (`Role` enum stored as text, DB default `Page`) | | `IsPartial` |
| `ApiKey` → `ApiKeys` | `Hash` unique; `UserId` | Name 200, Prefix 32, Hash 64, UserId 100, UserName 200 | `Scopes` (`List<string>`) | | `IsRevoked`, `IsExpired`, `IsActive` |

There is no foreign key on `ContentNode.ParentId`, `MediaItem.ParentId` or `DictionaryItem.ParentId`; they are plain
indexed columns and the services maintain the trees.

Collection and dictionary properties round-trip through `System.Text.Json` with matching `ValueComparer`s
(`DynCmsDbContext.JsonConversion<T>`). They are opaque to SQL: you cannot filter on a property value in the
database, only in memory.

The identity framework's tables (`IDFDBContext`) live in the **same** database.

### Schema management: `DatabaseSchema`

There are no EF migrations. Because two `DbContext`s share one database, `EnsureCreated` is useless: it stops as
soon as *any* table exists. `DatabaseSchema` fills that gap:

```csharp
await DatabaseSchema.EnsureTablesAsync(db);   // true when anything was created
var state = await DatabaseSchema.InspectAsync(db);   // SchemaState: Expected, Existing, Missing, IsComplete, IsEmpty, IsPartial
```

- database missing → `IRelationalDatabaseCreator.Create()`;
- none of this model's tables present → `CreateTables()`;
- some tables missing → generate just the `CreateTable` / `CreateIndex` / `AddForeignKey` / `AddUniqueConstraint`
  operations for those tables and run them;
- columns missing on tables that already exist → `ALTER TABLE … ADD COLUMN`, giving required columns the model
  default (or a zero value) so existing rows stay valid. This is how `Templates.Role`, `ContentNodes.Cultures` and
  the language flags reached older databases.

It uses `IMigrationsModelDiffer` behind a `#pragma warning disable EF1001`, the same internal API `CreateTables`
uses. On SQL Server the `INFORMATION_SCHEMA` lookups are by table name only (`dbo` assumed).

**It is strictly additive.** Renames, drops, type changes and data migrations are not handled. Move to EF Core
migrations before you evolve the content model in production.

### DbContext lifetime

Every service takes `IDbContextFactory<DynCmsDbContext>` and does:

```csharp
await using var db = await factory.CreateDbContextAsync(ct);
```

One context per operation, disposed immediately. No `DbContext` is ever held across an await in a Blazor circuit,
which sidesteps the classic "a second operation was started on this context" bug. Reads use `AsNoTracking()`.
Follow this pattern in anything you add.

---
## 6. The content model

### Document types (`ContentType`)

The schema of a page: an alias, an icon, a list of `PropertyType`s, and the structural decisions `AllowedAsRoot`,
`AllowedChildTypeAliases`, `AllowedTemplateAliases` + `DefaultTemplateAlias`, and `VariesByCulture`.

| `ContentType` member | Meaning |
|---|---|
| `Id`, `Alias`, `Name`, `Description`, `Icon` (default `"document"`, an `Icon.razor` name) | identity and display |
| `AllowedAsRoot` | may be created at the root of the tree |
| `AllowedChildTypeAliases` | which types may be created under it |
| `AllowedTemplateAliases`, `DefaultTemplateAlias` | which page templates content of this type may use (empty list = any) |
| `VariesByCulture` | content of this type has a name, URL segment and publish state per language (§6, *Languages*) |
| `SortOrder`, `CreatedAt`, `UpdatedAt` | |
| `Properties` | the `PropertyType` list |
| `Groups` (computed) | distinct `GroupName`s in `SortOrder`, blank → `"Content"`; the content editor renders them as tabs |

| `PropertyType` member | Meaning |
|---|---|
| `Id`, `ContentTypeId`, `Alias`, `Name`, `Description` | |
| `EditorAlias` | a registered property editor alias, e.g. `DynCms.RichText` |
| `GroupName` (default `"Content"`) | the tab |
| `Mandatory` | checked on publish |
| `VariesByCulture` | this property's *value* is per language (only meaningful when the type varies) |
| `SortOrder` | |
| `Config` | `Dictionary<string,string>` (case-insensitive) whose keys come from the editor's `ConfigFields`; `GetConfig(key)` |

`ContentTypeService.SaveAsync` derives missing aliases with `Slug.ToAlias`, rejects duplicate property aliases
(case-insensitive), and reconciles properties by `Id`: removed ones are deleted, new ones added explicitly
(`db.PropertyTypes.Add`, because a navigation-discovered entity with a preset Guid would be tracked as *Modified*),
`SortOrder` rewritten from list order. Type alias uniqueness is checked with a SQL `==`, so it is case-sensitive on
SQLite. `SaveAsync` does not validate that `DefaultTemplateAlias` is in `AllowedTemplateAliases` or that the
templates exist; the API layer does (§13). `DeleteAsync` refuses while content still uses the type, and scrubs the
alias from other types' `AllowedChildTypeAliases`. **Known issue:** the update branch does not copy
`VariesByCulture` on the type or its properties, so those flags can only be set when a type is created (§23).

### Content nodes (`ContentNode`)

One row carries **both states**:

| Draft | Published snapshot |
|---|---|
| `Name`, `UrlSegment`, `TemplateAlias`, `DraftValues` | `PublishedName`, `PublishedUrlSegment`, `PublishedTemplateAlias`, `PublishedValues`, `IsPublished`, `PublishedAt` |

- **Save** writes the draft columns only.
- **Save & publish** runs `Validate` (name plus mandatory properties) and, if clean, copies draft → published.
- **Unpublish** flips `IsPublished`; the snapshot stays.
- `HasPendingChanges` compares the two, ignoring empty values and key order.

Tree shape:

- `Path`: comma-separated ancestor ids **ending with the node's own id**. `AncestorIds` parses it (root first,
  without the node itself).
- `Level`: **0 at the root**, +1 per level.
- `SortOrder`: position among siblings; `MoveAsync(id, ±1)` renumbers the whole sibling set.
- Descendant queries are `Path.StartsWith(path + ",")`: one indexed query, no recursion.

Other members: `ParentId`, `ContentTypeId`, `ContentType` (navigation, **null unless the query loaded it**),
`Cultures` (below), `CreatedAt`, `UpdatedAt`, and the value accessors `GetValue(alias)`, `SetValue(alias, value)`
(throws `InvalidOperationException` for a property that varies by culture), `PropertyVaries(alias)`.

`CreateAsync` enforces the structure rules (allowed at root / allowed under this parent), seeds every property
alias with `null` (varying properties in the culture's dictionary, shared ones on the node), and picks
`DefaultTemplateAlias`, falling back to the first allowed template. `UrlSegment` is slugged from the name and made
unique among siblings by appending `-2`, `-3`, …. An empty slug (a name in a non-Latin script, see `Slug` in §7)
becomes `page`.

`SaveAsync` re-slugs the segment from the name **only when `UrlSegment` is blank**. A node always has a segment after
`CreateAsync`, so renaming a page keeps its URL unless you clear the segment first (the content editor's Info tab
lets an editor change it explicitly).

`DeleteAsync` deletes the node **and every descendant**, with no recycle bin and no confirmation beyond the UI
dialog. Nothing checks for content-picker references or cleans up media.

**Which queries load the document type.** Only `GetAsync`, `CreateAsync`, `SaveAsync`, `PublishAsync` and
`UnpublishAsync` return nodes with `ContentType.Properties` populated. Nodes from the list queries (`GetRootsAsync`,
`GetChildrenAsync`, `GetAncestorsAsync`, `GetDescendantsAsync`, `GetTreeAsync`, `GetRecentAsync`, `SearchAsync`)
have an empty property list, so on those `PropertyVaries()` is always false and the culture-aware value accessors
fall back to the shared dictionary. Load a node with `GetAsync` before you edit its values.

### Published content (`PublishedContent`)

The immutable read-side projection the site renders (all properties are `init`-only):

| Member | Meaning |
|---|---|
| `Id`, `ParentId`, `Name`, `UrlSegment`, `Url` | identity and the resolved URL (with the language prefix) |
| `ContentTypeAlias`, `ContentTypeName`, `TemplateAlias` | the type and the template to render (`PublishedTemplateAlias ?? TemplateAlias`, draft alias in preview) |
| `Level`, `SortOrder`, `CreatedAt`, `UpdatedAt`, `PublishedAt` | `UpdatedAt` is the later of the culture's and the node's; `PublishedAt` the culture's when varying |
| `IsPreview` | draft values are being shown |
| `Culture`, `VariesByCulture`, `Cultures` | the language rendered, whether the type varies, and the languages the item is available in (every language for invariant content) |
| `Values` | published values, or draft values in preview (case-insensitive keys) |

```csharp
content["bodyText"]                 // raw string?
content.Value<int>("pageSize", 6)   // typed, with fallback
content.HasValue("summary")         // false for null or whitespace
content.Is("article")               // content type alias, case-insensitive
content.IsAvailableIn("de")
```

`ContentValueConverter.Convert<T>(raw, fallback)` handles `string`, `bool` (`true`/`1`/`on`/`yes`, anything else is
`false`, never the fallback), `int`, `long`, `double`, `decimal`, `float` (invariant), `Guid`, `DateTime`
(round-trip), `DateOnly` (first ten characters), `DateTimeOffset`, enums (ignore case), unwraps `Nullable<T>`, and
falls back to `System.Text.Json` deserialisation for everything else. It never throws: a bad value yields the
fallback. `ToStorage<T>(value)` is the inverse (`bool` → `"true"`/`"false"`, `DateTime` → round-trip, `IFormattable`
→ invariant, else JSON).

### URL rules

Implemented in `PublishedContentQuery`:

- The first path segment is tested as a **language** first (`Match`: exact ISO code, else a unique primary-subtag
  match, so `/de/...` finds `de-DE` when it is the only German). A hit decides the language of the whole request and
  the segment is consumed. Consequence: a page whose segment equals a language code (or its primary subtag) is
  unreachable, and `/en/about` resolves for the default language although its canonical URL is `/about`.
- The **primary root**, the first root ordered by `SortOrder`, then `Name`, is `/`. Its children are `/segment`, and
  so on down.
- Any **other root** keeps its own segment as the first path element: `/second-site/page`.
- Resolution tries the primary root's subtree first, then the other roots. Each level loads all candidate children
  (with their types) and picks the first visible child whose segment matches, case-insensitively.
- In preview the draft segments are used; otherwise the published ones (`Published* ?? draft`).
- The **default language** has no prefix; every other language is served under `/{iso-code}/…` (lower case:
  `/de-de/ueber-uns`).
- URL *building* (`GetUrlAsync`, `PublishedContent.Url`) walks the ancestor chain, skips the primary root, joins the
  segments and prepends the prefix. Ancestors that are not published are silently dropped from the chain rather than
  making the child unresolvable, and the ancestors are not filtered per culture; such a URL then does not route.

### Languages and culture variants

Modelled on Umbraco's *Allow varying by culture*.

**Languages** (`Language`, table `Languages`, `ILanguageService`, Settings → Languages):

| Member | Meaning |
|---|---|
| `Id`, `IsoCode`, `Name`, `SortOrder`, `CreatedAt` | `IsoCode` is a .NET culture name, validated and canonicalised (`en-us` → `en-US`); `Name` defaults to the culture's English name |
| `IsDefault` | exactly one; the initialiser creates it from `DynCmsOptions.DefaultCulture` (`"en"`) on first start |
| `IsMandatory` | must be published before any other language of a node goes live |
| `FallbackIsoCode` | where an empty value is looked up next; must be an existing language other than itself; chains and even cycles are tolerated (`FallbackChain` stops on repeat) |
| `UrlPrefix` (computed) | lower-cased ISO code |
| `Culture` (computed) | the `CultureInfo`, invariant when unknown |

`LanguageExtensions`: `Match(code)`, `Default()` (the flagged one, else the lowest `SortOrder`), `FallbackChain(iso)`
(self first). `SaveAsync` keeps one default (you cannot clear `IsDefault` by saving `false`; use `SetDefaultAsync` on
another language). Deleting a language strips its per-language content from every node and its texts from every
dictionary item, and nulls other languages' fallback to it; the default language cannot be deleted.

**What varies.** `ContentType.VariesByCulture` turns variation on for a document type; `PropertyType.VariesByCulture`
marks the properties whose *value* is per language. A varying type always has a per-language **name, URL segment
and publish state**; properties that are not flagged are *shared* by every language, as is the template.

**Storage.** `ContentNode.Cultures` is a JSON column: `Dictionary<string, ContentCulture>` keyed by ISO code
(case-insensitive), each entry a `ContentCulture` with its own draft (`Name`, `UrlSegment`, `DraftValues`,
`UpdatedAt`) and published snapshot (`IsPublished`, `PublishedName`, `PublishedUrlSegment`, `PublishedValues`,
`PublishedAt`), plus the computed `Exists` (has a name) and `HasPendingChanges`. The node-level columns keep their
meaning for invariant content and, for varying content, hold the **shared** values; `Name` and `UrlSegment` mirror
the default language (or the first existing culture) so the tree, search and the API stay readable, and
`IsPublished` is true when *any* language is. Rows that predate the feature have an empty map and behave exactly as
before as long as their type stays invariant; a type switched to varying leaves its existing nodes invisible in every
language until each is saved with a per-language name.

**Working with a node.** Never read the raw dictionaries when a culture is in play; use the culture-aware members:

```csharp
node.VariesByCulture                     // from the document type (ContentType must be loaded)
node.PropertyVaries("bodyText")          // type varies AND the property is flagged
node.GetName("de")  / node.SetName("de", "Über uns")
node.GetUrlSegment("de") / node.SetUrlSegment("de", "ueber-uns")
node.GetValue("bodyText", "de") / node.SetValue("bodyText", html, "de")   // routes to the right dictionary
node.SetValue("image", id)               // shared value; throws for a varying property
node.SetValue("bodyText", html, null)    // 3-arg overload with null culture writes the SHARED dictionary, silently
node.GetCulture("de") / node.GetOrAddCulture("de")
node.IsPublishedIn("de"), node.HasPendingChangesIn("de"), node.ExistingCultures, node.PublishedCultures
```

`IContentService` (full signatures in §7):

- `CreateAsync(typeId, parentId, name, culture)`: for a varying type the name lands in `culture` (the default
  language when null). Other languages are *created* by saving a name for them.
- `SaveAsync(node)`: saves every language the node carries; slugs and de-duplicates segments **per language**
  among siblings. The taken set is every sibling's node-level segment plus each sibling's segment in that language.
  A culture with a blank name is removed from the map unless it is published (then the save throws). "Content needs
  a name in at least one language." when nothing is left.
- `Validate(node, culture)`: name and mandatory properties of that language, plus the shared ones.
  `ContentValidationError.Culture` is the language for the name and varying properties, `null` for shared ones.
- `PublishAsync(id, cultures)`: publishes the listed languages (all named ones when null or empty). Every language
  is validated first; **mandatory languages** must be live afterwards, or the call fails with one error per problem.
  The shared values go live with every publish. An unknown id or culture *throws*; validation failures come back as
  `PublishResult.Failed`.
- `UnpublishAsync(id, culture)`: one language, or all when null; `IsPublished` becomes "any culture published".

**Reading.** `IPublishedContentQuery` takes an optional `culture` on every call. When it is null the *current*
language is used: explicit argument → `ICultureContext.Culture` if a host set it → the language prefix of the current
path (`NavigationManager` in components, `HttpContext` in API calls) → the default language → a synthetic `en`. An
unknown explicit culture silently falls through this chain (the write side throws instead). So a layout or a
template component that just calls `Query.GetChildrenAsync(id, preview)` automatically stays in the language of the
page.

A varying node is *visible* in a language when it is published there (or merely exists, in preview). A varying
property that is empty in the language follows the **fallback chain** (`de-AT → de-DE → en`, if configured that way)
and stops there: unlike the dictionary, content does **not** fall back to the default language unless it is in the
chain. `GetCultureLinksAsync(id)` returns `CultureLink(IsoCode, Name, Url, IsCurrent, IsDefault)` for every language
the node is visible in, for a switcher; the default `SiteLayout` renders one when there is more than one language.

**Back office.** The content editor shows a language selector for varying types; the name, URL segment and the
flagged properties belong to the selected language (badged *per language*), the rest are badged *Shared*. Edits
in several languages are kept on the node and saved together. *Save & publish* opens a dialog to pick the
languages (mandatory ones that are not live yet are pre-selected and locked); *Unpublish* takes the selected
language offline. `/admin/content/{id}?culture=de` opens the editor in a language.

**API and MCP.** See §13. Languages are read with `content:read` and written with `document-types:write` (the
service maps them to the `DocumentType` entity), so an editor's key cannot add languages.

### The dictionary

Modelled on Umbraco's dictionary: the texts a template prints that are not content, with one translation per language.

**Model.** `DictionaryItem` (table `DictionaryItems`, `IDictionaryService`, Settings → Dictionary): `Key` (unique
across the dictionary, case-insensitive, ≤ `DictionaryItem.MaxKeyLength` = 200, e.g. `blog.readMore`), optional
`ParentId` (a tree for organisation only; the key alone identifies an item), `SortOrder`, `Translations` (JSON map
of ISO code → text), `CreatedAt`, `UpdatedAt`, and the unstored `Level`. Methods: `Get(iso)`, `Set(iso, text)` (blank
removes), `HasTranslation(iso)`, `TranslatedCultures`, `Is(key)`, `Clone()`, and `Resolve(iso, languages)`, which
applies the fallback: the language's own text, else the first along its **fallback chain**, else the **default
language's**, else `null`.

**Service.** `GetAllAsync` returns every item in tree order (parents first, siblings by `SortOrder` then `Key`,
orphans surfaced at the root) with `Level` set; note it hands out the *cached* instances, whereas `GetAsync` /
`GetByKeyAsync` return clones. `SaveAsync` validates the key (required, unique, ≤ 200 chars), the parent (must exist,
not the item or a descendant), trims texts, resolves language codes with `Match` (so `"de"` lands on `de-DE` when
unique) and drops translations for languages the site does not have. `DeleteAsync` removes an item with everything
under it. `GetValueAsync(key, culture)` / `GetValuesAsync(culture)` are the read side, with the same culture
resolution as `IPublishedContentQuery`. `GetValue` / `GetValues` are synchronous twins answered from memory:
`DictionaryCache` (singleton) holds a `DictionarySnapshot` of the items and the languages, loaded by
`DynCmsInitializer` at startup and reloaded after every dictionary or language change, so a Blazor template can
print labels without awaiting anything. Before the first load the synchronous calls return `null` / an empty map and
kick off a background load.

**Rendering.** `CmsTemplateBase.T(key, fallback?)` for component templates and the `dictionary` filter (alias
`translate`) plus `site.dictionary` for Liquid (§9). When no text exists in any language the key itself is printed
(or the fallback argument, when given) so a missing translation is visible rather than silent.

**Back office.** Settings → Dictionary lists every item with its text per language (fallback texts in italics,
*Missing* where nothing applies), a per-language "translated n / total" summary and a filter on keys and texts. An
item opens in an editor with the key, the parent, the Liquid to paste, and a textarea per language; `Ctrl+S` saves.
Editors hold every action on the `Dictionary` permission entity; API keys need `dictionary:read` / `dictionary:write`.

---

## 7. Service reference

Every method takes a trailing `CancellationToken ct = default`, omitted below for brevity. Every service throws
`InvalidOperationException` with a message written for an editor to read; `PublishAsync` is the only one that reports
validation as a result object.

### `IContentService`

```csharp
Task<IReadOnlyList<ContentNode>> GetRootsAsync();
Task<IReadOnlyList<ContentNode>> GetChildrenAsync(Guid parentId);
Task<bool>                       HasChildrenAsync(Guid id);
Task<ContentNode?>               GetAsync(Guid id);                       // with ContentType.Properties
Task<IReadOnlyList<ContentNode>> GetAncestorsAsync(Guid id);              // root first
Task<IReadOnlyList<ContentNode>> GetDescendantsAsync(Guid id);            // by Level, then SortOrder (breadth order)
Task<IReadOnlyList<ContentNode>> GetTreeAsync();                          // every node, depth-first; orphans dropped
Task<IReadOnlyList<ContentNode>> GetRecentAsync(int take = 10);           // UpdatedAt desc
Task<IReadOnlyList<ContentNode>> SearchAsync(string term, int take = 25); // LIKE on name/segment, then per-language names in memory
Task<int>                        CountAsync(bool? published = null);      // node-level IsPublished
Task<IReadOnlyList<ContentType>> GetAllowedChildTypesAsync(Guid? parentId);
Task<ContentNode>                CreateAsync(Guid contentTypeId, Guid? parentId, string name, string? culture = null);
Task<ContentNode>                SaveAsync(ContentNode node);             // never touches ParentId, SortOrder, Level, Path
IReadOnlyList<ContentValidationError> Validate(ContentNode node, string? culture = null);
Task<PublishResult>              PublishAsync(Guid id, IReadOnlyList<string>? cultures = null);
Task<ContentNode?>               UnpublishAsync(Guid id, string? culture = null);
Task                             DeleteAsync(Guid id);                    // subtree, permanent
Task                             MoveAsync(Guid id, int direction);       // only Math.Sign(direction) matters; clamps at the edges

public sealed record ContentValidationError(string PropertyAlias, string Message, string? Culture = null); // "name" for the name
public sealed record PublishResult(bool Success, IReadOnlyList<ContentValidationError> Errors, ContentNode? Node);
```

`SaveAsync` copies `TemplateAlias` and `DraftValues` wholesale without validating aliases or templates; unknown
aliases persist. The API layer validates before it calls the service.

### `IContentTypeService`

```csharp
Task<IReadOnlyList<ContentType>> GetAllAsync();                 // SortOrder, Name; properties ordered
Task<ContentType?>  GetAsync(Guid id);
Task<ContentType?>  GetByAliasAsync(string alias);              // SQL ==, case-sensitive on SQLite
Task<bool>          AliasExistsAsync(string alias, Guid? excludeId = null);
Task<ContentType>   SaveAsync(ContentType contentType);
Task<int>           CountContentAsync(Guid contentTypeId);
Task                DeleteAsync(Guid id);                       // throws while content uses it
```

### `ILanguageService`

```csharp
Task<IReadOnlyList<Language>> GetAllAsync();                    // IsDefault desc, SortOrder, Name
Task<Language>   GetDefaultAsync();                             // throws when none
Task<Language?>  GetAsync(string isoCode);                      // case-insensitive
Task<Language>   SaveAsync(Language language);
Task<Language>   SetDefaultAsync(string isoCode);
Task             DeleteAsync(string isoCode);                   // no-op when missing; throws for the default
Task<Language>   EnsureDefaultAsync(string isoCode);            // used by the initializer
string?          DisplayNameOf(string? isoCode);                // CultureInfo.EnglishName
```

### `IDictionaryService`

```csharp
Task<IReadOnlyList<DictionaryItem>> GetAllAsync();              // tree order, Level set, cached instances
Task<DictionaryItem?> GetAsync(Guid id);                        // clone
Task<DictionaryItem?> GetByKeyAsync(string key);                // clone
Task<int>             CountAsync();
Task<DictionaryItem>  SaveAsync(DictionaryItem item);
Task                  DeleteAsync(Guid id);                     // with descendants
Task<string?>         GetValueAsync(string key, string? culture = null);
Task<IReadOnlyDictionary<string,string>> GetValuesAsync(string? culture = null);
string?               GetValue(string key, string? culture = null);          // from DictionaryCache
IReadOnlyDictionary<string,string> GetValues(string? culture = null);       // from DictionaryCache
Task                  RefreshAsync();                           // reload the cache
```

### `IMediaService` and `IMediaStorage`

```csharp
Task<IReadOnlyList<MediaItem>> GetChildrenAsync(Guid? folderId);   // null = root; folders first, SortOrder, Name
Task<MediaItem?>               GetAsync(Guid id);
Task<IReadOnlyList<MediaItem>> GetAncestorsAsync(Guid id);         // root first; one query per level
Task<IReadOnlyList<MediaItem>> GetRecentAsync(int take = 12);      // files only, CreatedAt desc
Task<int>                      CountAsync();                       // files only
Task<MediaItem>                CreateFolderAsync(Guid? parentId, string name);
Task<MediaItem>                UploadAsync(Guid? folderId, string fileName, string? contentType, Stream content);
Task<MediaItem?>               RenameAsync(Guid id, string name);
Task                           DeleteAsync(Guid id);               // subtree; files deleted before the rows
string?                        GetUrl(MediaItem? item);            // null for folders

public interface IMediaStorage
{
    Task   SaveAsync(string storedPath, Stream content, CancellationToken ct = default);
    Task   DeleteAsync(string storedPath, CancellationToken ct = default);
    string GetUrl(string storedPath);
}
```

`MediaItem`: `Id`, `ParentId`, `Name` (file name without extension, not slugged), `IsFolder`, `FileName`, `Extension`
(lower case, with the dot), `MimeType`, `StoredPath` (forward slashes), `SizeBytes`, `SortOrder`, `CreatedAt`,
`UpdatedAt`, `IsImage` (`MimeType` starts with `image/`).

### `ITemplateService`, `ITemplateRegistry`

See §9.

### `IPublishedContentQuery` (read side)

`IPublishedContentQuery` is the only thing a template should need:

```csharp
Task<PublishedContent?>               GetByRouteAsync(string path, bool preview = false, string? culture = null);
Task<PublishedContent?>               GetByIdAsync(Guid id, bool preview = false, string? culture = null);
Task<PublishedContent?>               GetRootAsync(bool preview = false, string? culture = null);
Task<IReadOnlyList<PublishedContent>> GetChildrenAsync(Guid parentId, bool preview = false, string? culture = null);
Task<IReadOnlyList<PublishedContent>> GetAncestorsAsync(Guid id, bool preview = false, string? culture = null);
Task<IReadOnlyList<PublishedContent>> GetByContentTypeAsync(string alias, bool preview = false, string? culture = null); // PublishedAt ?? UpdatedAt desc
Task<string?>                         GetUrlAsync(Guid id, string? culture = null);      // draft segments for unpublished nodes
Task<IReadOnlyList<CultureLink>>      GetCultureLinksAsync(Guid id, bool preview = false, string? culture = null);
Task<string>                          ResolveCultureAsync(string? culture = null);

public sealed record CultureLink(string IsoCode, string Name, string Url, bool IsCurrent, bool IsDefault);
```

Everything filters on the publish state unless `preview: true`. **Always pass `Content.IsPreview` through**, or
preview mode will silently show published children under a draft page. `culture` may stay null inside a page: the
language of the current path is used.

### `ICultureContext`

```csharp
public interface ICultureContext { string? Culture { get; set; } string? RequestPath { get; } }
```

Scoped. Set `Culture` from your own code (a middleware, a layout) to pin the language for the rest of the scope.
`RequestPath` comes from `NavigationManager` in a circuit, else `IHttpContextAccessor`; in a hosted scope with neither
it is null and the default language wins. `CultureContextExtensions.ResolveLanguage(context, languages, culture)` is
the resolution chain both the content query and the dictionary use.

### Helpers

**`Slug`** (`DynCMS.Core.Helpers`): `ToUrlSegment(text)` transliterates `ä ö ü ß æ ø å œ þ ð` (both cases),
strips diacritics (NFD, non-spacing marks removed), lower-cases, replaces every run of `[^a-z0-9]` with `-`,
collapses and trims dashes. Non-Latin scripts vanish entirely and produce `""` (callers substitute `page` or `file`).
`ToAlias(text)` camel-cases the segment parts and prefixes a leading digit with `p`.

**`Paging`** (`DynCMS.Core.Helpers`): `PageParameter = "page"`; `Page<T>(source, pageSize, page?)` returns
`PagedList<T>` (enumerates the whole source; `pageSize` clamped ≥ 1, `page` clamped into range); `ParsePage(string?)`,
`ParsePage(Uri, parameter)`, `PageUrl(Uri, page, parameter)`, `PageUrl(path, query?, page, parameter)` (page 1 drops
the parameter), `PageNumbers(page, pageCount, window = 2)` (every page when `pageCount ≤ 2·window + 5`, else
`1, gap, window, gap, last` with `null` as the gap). `IPagedList`: `Page`, `PageSize`, `PageCount` (≥ 1), `TotalCount`,
`First`, `Last`, `HasPrevious`, `HasNext`, `PreviousPage`, `NextPage`; `PagedList<T>` adds `Items`.

---
## 8. Request to page: the rendering pipeline

```
GET /blog/my-post
      │
      ├─ UseDynCmsSetup            ready? → continue
      ├─ authentication            IDF_AuthToken cookie or bearer → ClaimsPrincipal
      ├─ endpoints                 /media/**, /admin/data/backups/**, /api/v1/**, /mcp, static assets
      └─ MapRazorComponents        Router (Host + UI + entry assembly + AdditionalAssemblies)
             │
             ├─ literal routes win → /admin/…, /setup, /Error, /not-found, your own @page components
             └─ CmsPage  @page "/" and "/{*Path}"   [SupplyParameterFromQuery] bool Preview
                    │
                    └─ <CmsContentRenderer Path="blog/my-post" Preview="@Preview" />
                           │
                           ├─ Query.GetByRouteAsync(path, preview)   → PublishedContent?
                           │        null → Nav.NotFound() (default) / the NotFound fragment / a built-in message
                           ├─ Templates.Get(content.TemplateAlias)   → TemplateDefinition?
                           └─ render:
                                stored    → <CmsLiquidTemplate Alias=… Content=… />
                                component → <DynamicComponent Type=… Parameters={ Content } />
                                neither   → <CmsDefaultTemplate />   (a <dl> of every value)
```

`CmsContentRenderer` parameters: `Path`, `Preview`, `NotFound` (`RenderFragment?`), `UseNotFoundPage` (default
`true`). Behaviour:

- It re-resolves only when the key `"{preview}|{Path}"` changes, not on every parameter set.
- **Preview** (`?preview=true`) is honoured only when the cascaded `AuthenticationState` says the caller is
  authenticated. It renders a preview bar with *Exit preview* (→ `content.Url`) and *Edit* (→ `/admin/content/{id}`).
- It sets `<PageTitle>` to the content name and cascades the `PublishedContent` (`IsFixed="false"`), so nested
  components can pick it up with `[CascadingParameter]`.
- Not found: with `UseNotFoundPage` and no `NotFound` fragment it calls `NavigationManager.NotFound()`, which the
  host's `Router NotFoundPage="typeof(Pages.NotFound)"` plus `UseStatusCodePagesWithReExecute("/not-found")` turn
  into a real 404. With `UseNotFoundPage="false"` it renders the fragment, or a built-in `<div class="dc-notfound">`.
- Admin routes win over the catch-all because literal segments beat a catch-all parameter.

The host shell (`src/DynCMS.Host/Components/App.razor`) picks the render mode per request with
`HttpContext.AcceptsInteractiveRouting() ? InteractiveServer : null` for both `<HeadOutlet>` and `<Routes>`, sets
`<html lang>` to the language of the first path segment once the runtime is ready (`DynCmsHostOptions.Language`
before that), and links, in order: `ResourcePreloader`, `dyncms-shell.css`, `dyncms-site.css` (when
`IncludeSiteStylesheet`), `{ApplicationName}.styles.css`, `dyncms-admin.css` (not fingerprinted), your
`Options.Stylesheets`, `ImportMap`; then `<Routes>`, `<ReconnectModal>`, `blazor.web.js` and your `Options.Scripts`.
`Routes.razor` wraps everything in `AuthorizeRouteView` with `DefaultLayout = Options.Layout`, `<NotAuthorized>`
→ `<RedirectToLogin />`, and `FocusOnNavigate Selector="h1"`.

`SiteLayout` (the default `Options.Layout`) resolves the culture, loads the root and its children for the navigation,
renders the language switcher when there is more than one language (with `hreflang` and `aria-current`), a
"Back office" link, and a footer; it reloads its data on `LocationChanged` only when the culture changed.

---

## 9. Templates

A template is an alias plus something that renders it. `ITemplateRegistry` holds two sets:

```csharp
public enum TemplateRole { Page = 0, Partial = 1 }
public enum TemplateKind { Component, Stored }

public sealed record TemplateDefinition(
    string Alias, string Name,
    Type? ComponentType = null,     // component template
    Guid? StoredId = null,          // stored Liquid template
    TemplateRole Role = TemplateRole.Page,
    string? Description = null)
{ TemplateKind Kind; bool IsStored; bool IsPartial; }

public interface ITemplateRegistry
{
    IReadOnlyList<TemplateDefinition> All { get; }        // stored + components not shadowed by a stored alias, by Name
    IReadOnlyList<TemplateDefinition> Pages { get; }      // All minus partials
    IReadOnlyList<TemplateDefinition> Partials { get; }
    IReadOnlyList<TemplateDefinition> Components { get; }
    IReadOnlyList<TemplateDefinition> Stored { get; }
    IReadOnlyList<Template> StoredTemplates { get; }
    TemplateDefinition? Get(string? alias);               // stored first, then component
    TemplateDefinition? GetComponent(string? alias);
    Template? GetStored(string? alias);
    void Register(TemplateDefinition definition);         // overwrites the same alias silently
    void SetStored(IEnumerable<Template> templates);      // replaces the stored set wholesale; raises Changed
    event Action? Changed;
}
```

`Template` (table `Templates`): `Id`, `Alias`, `Name`, `Description`, `Role`, `Content` (Liquid source), `CreatedAt`,
`UpdatedAt`, `IsPartial`.

### 9.1 Component templates

A Blazor component that inherits `CmsTemplateBase` and is registered at startup:

```razor
@* Components/Templates/ArticleTemplate.razor *@
@inherits CmsTemplateBase

<h1>@Content.Name</h1>
<div class="prose">@Content.Html("bodyText")</div>
```

```csharp
builder.AddDynCmsHost(...)
    .AddTemplate<ArticleTemplate>("article", "Article");
```

`AddTemplate<T>` has no generic constraint; a non-component type compiles and fails inside `DynamicComponent` at
render time. `CmsTemplateBase` supplies:

- `[Parameter] PublishedContent Content`;
- `[Inject] protected IDictionaryService Dictionary`;
- `protected string T(string key, string? fallback = null)`: a dictionary text in the language of the page
  (`Dictionary.GetValue(key, Content.Culture) ?? fallback ?? key`). Synchronous, served from memory.

The extension methods in `PublishedContentExtensions` cover the common conversions:

| Call | Returns |
|---|---|
| `Content.Html("bodyText")` | `MarkupString` (rich text, unescaped) |
| `Content.Tags("tags")` | `IReadOnlyList<string>` (empty when unset) |
| `Content.MediaId("image")` / `Content.ContentId("ctaLink")` | `Guid?` |
| `Content.Date("publishDate")` | `DateTime?` |
| `Content.Flag("featured")` | `bool` |
| `Content.Value<T>("alias", fallback)` | anything `ContentValueConverter` handles |

Render a media picker with `<CmsImage MediaId="@Content["image"]" Class="…" Alt="…"><Fallback>…</Fallback></CmsImage>`.
`CmsImage` parameters: `MediaId`, `Alt` (defaults to the item's name), `Class`, `Lazy` (default true), `Fallback`,
plus any unmatched attributes; it re-queries only when `MediaId` changes.

**Paging.** `Paging.Page(items, pageSize, page)` returns a `PagedList<T>` (§7). `<CmsPager Paging="_paging" />`
renders previous / numbers / next as plain `<a href="?page=n">` links (a window around the current page with gaps,
the first page without a parameter, the rest of the query string kept), so it works without interactivity and every
page has a stable address. Parameters: `Paging` (required, `IPagedList`), `UrlFor` (`Func<int,string>`),
`PreviousText`, `NextText`, `AriaLabel`, `Window` (default 2). It renders nothing when there is one page. Read the
number with `[SupplyParameterFromQuery(Name = "page")] string? Page` and `Paging.ParsePage(Page)`. The archived demo's
`ArticleListTemplate.razor` (`_archive/DynCMS.Web-demo/Components/Templates/`) is the worked example, with the labels from the dictionary.

Component templates are compiled in, so they cannot be edited in the back office. They are the right choice when
the template needs real code: injected services, interactive state, anything beyond what Liquid expresses.

### 9.2 Stored (Liquid) templates

Rows in `Templates`, written in Liquid and rendered by [Fluid](https://github.com/sebastienros/fluid) 2.40.
Created and edited at `Settings → Templates`; saving makes the change live immediately
(`SaveAsync` → `RefreshRegistryAsync` → `ITemplateRegistry.SetStored` → `Changed`).

```csharp
public interface ITemplateService
{
    Task<IReadOnlyList<Template>> GetAllAsync();
    Task<Template?>  GetAsync(Guid id);
    Task<Template?>  GetByAliasAsync(string alias);
    Task<Template>   SaveAsync(Template template);
    Task<int>        CountContentAsync(string alias);                          // TemplateAlias or PublishedTemplateAlias
    Task<TemplateUsage> GetUsageAsync(string alias, Guid? ignoreTemplateId = null);
    Task             DeleteAsync(Guid id);
    Task<Template>   DuplicateAsync(Guid id);                                  // "{Name} copy", alias via GetAvailableAliasAsync
    Task<Template>   CreateOverrideForComponentAsync(string alias);
    Task<string>     GetAvailableAliasAsync(string preferred);                 // card, card2, card3, …
    Task             RefreshRegistryAsync();
}
public sealed record TemplateUsage(int ContentCount, IReadOnlyList<ContentType> DocumentTypes,
                                   IReadOnlyList<Template> UsedBy, IReadOnlyList<string> Uses) { bool IsUsed; }
```

`SaveAsync` enforces, in order: a name; an alias (slugged from the name when blank); valid Liquid syntax
(`ILiquidTemplateEngine.Validate`); an alias no other template uses (a SQL `==`, so case-sensitive on SQLite, see
§23). Failures surface as `InvalidOperationException`.

`DeleteAsync` refuses when another template still `{% render %}`s it, or when content uses the alias *and* no
component template exists to take over. When a stored template is deleted and no component shares its alias, the
alias is scrubbed from every document type's allowed and default templates.

Changing a page template's `Role` to `Partial` detaches it from any content and document type pointing at it.
Changing a template's *alias* does **not** update content or document types that point at the old alias.

### 9.3 Resolution and override rules

**A stored template shadows a component template with the same alias.** `TemplateRegistry.Get` looks in the
stored dictionary first. That is the whole override mechanism:

- open a component template in the back office → **Create an editable version** →
  `CreateOverrideForComponentAsync` writes a stored template with the same alias, scaffolded from the document type
  that uses it (`TemplateSamples.Scaffold`);
- from then on, content with that alias renders the Liquid;
- delete the stored template and the compiled component takes over again.

No redeploy either way.

### 9.4 Liquid API reference

The engine is `FluidTemplateEngine` (`src/DynCMS.Core/Templates/LiquidTemplateEngine.cs`). Top-level variable names
are case-insensitive; member names on `content` and `site` are matched with underscores stripped and case ignored,
so `content.bodyText`, `content.bodytext` and `content.body_text` all reach the same property.

**Variables**

| | |
|---|---|
| `content` | the page being rendered; inside a partial, whatever the caller passed (or the caller's page, see 9.5) |
| `site.root` (`site.home`) | the primary root page in the page's language |
| `site.culture` | the ISO code of the language being served |
| `site.preview` | same as `preview` |
| `site.languages` | the home page in every language: hashes of `iso_code`, `name`, `url`, `is_current`, `is_default` |
| `site.dictionary` | every dictionary key with its text in the page's language: `{{ site.dictionary['blog.readMore'] }}` |
| `preview` | `true` while a draft is shown |
| `request.path`, `request.url`, `request.query`, `request.query_string` | the request being answered: `/blog`, `/blog?page=2`, a hash of the query string (`request.query.page`, string values), `?page=2` or `""`. In the editor's preview it is the page's own URL (`/sample` for a synthetic sample) with no query string |

**`content` members**

`id`, `name`, `url`, `url_segment`, `content_type` (or `content_type_alias`), `content_type_name`, `template` (or
`template_alias`), `level`, `sort_order`, `created_at`, `updated_at`, `published_at`, `is_preview`, `parent_id`,
`values` (the raw string map, no conversion), `culture`, `varies_by_culture`, `available_cultures` (ISO codes),
`cultures` (this page in every language, same hashes as `site.languages`), plus the navigation members `children`,
`parent` (nil at the root), `ancestors`, `siblings` (nil at the root, otherwise the parent's children minus this page),
each a live query in the page's language and preview scope.

Anything else is a **property lookup by alias**. Built-in names win, so a property aliased `name`, `level`,
`template`, `parent` and so on can only be reached through `content.values['alias']`.

Stored values are converted on the way out (`ConvertValue`): empty → `nil`; exactly `true`/`false` → boolean; text
that starts and ends with `[]` or `{}` → a Liquid array or hash (parsed with `System.Text.Json`, numbers become
`long`/`decimal`); **everything else stays a string, numbers included** (`content.pageSize` is `"6"`). That is why a
tags property can be iterated directly.

**DynCMS filters** (no custom tags are registered; the tag set is Fluid's)

| Filter | Input | Output |
|---|---|---|
| `media_url` | media picker value | URL string, or nil |
| `media` | media picker value | hash: `id`, `name`, `url`, `file_name`, `extension`, `mime_type`, `size_bytes`, `is_image`, `is_folder`; nil when not found |
| `content_by_id` | content picker value | a page, or nil |
| `content_url` | content picker value or a page | URL string, or nil |
| `content_of_type` | a document type alias | every published page of that type (newest first) |
| `children_of` | a page or an id | its children |
| `tags` / `json` | JSON text | array or hash (`ConvertValue`; non-string input is returned unchanged) |
| `dictionary` (alias `translate`) | a dictionary key | its text in the page's language with fallback; `'key' \| dictionary: 'Fallback text'` when there is none, else the key itself |
| `paginate: 6` | a list | one page of it, six to a page (default 10), chosen by `?page=` (`\| paginate: 6, 2` asks for page 2 as a *number*; out-of-range numbers are clamped): a hash of `items`, `page`, `page_size`, `page_count`, `total`, `first`, `last`, `has_previous`, `has_next`, `previous_page`, `next_page`, `previous_url`, `next_url` and `pages` (hashes of `number`, `url`, `is_current`). URLs keep the rest of the query string; page 1 has no parameter |
| `page_url` | a page number | the URL of that page on the current path: `{{ 3 \| page_url }}` (clamped to ≥ 1, not to the page count) |

Plus the standard Liquid/Fluid filter set (`date`, `default`, `truncate`, `where`, `map`, `join`, `sort`, …).

Paging in Liquid, as the *Listing page* starting point (and the archived demo's *Paging page* template) does it:

```liquid
{%- assign paged = content.children | paginate: 12 %}
{%- for item in paged.items %}{% render 'card', content: item %}{%- endfor %}
{%- if paged.page_count > 1 %}
<nav class="pager">
  {%- if paged.has_previous %}<a href="{{ paged.previous_url }}" rel="prev">‹</a>{%- endif %}
  {%- for p in paged.pages %}<a href="{{ p.url }}"{% if p.is_current %} class="active"{% endif %}>{{ p.number }}</a>{%- endfor %}
  {%- if paged.has_next %}<a href="{{ paged.next_url }}" rel="next">›</a>{%- endif %}
</nav>
{%- endif %}
```

`CmsLiquidTemplate` hands the current URL to the renderer (`LiquidRequest.FromUri`) and renders again when the
query string changes, which the router does not do on its own for a query-only navigation; the render key is
`alias | content id | preview | updated at | path and query`. When you render through `IStoredTemplateRenderer`
yourself without a `LiquidRequest`, `paginate` always yields page 1 unless you pass the page as the second argument.

**Output is HTML-encoded.** Rich text needs `| raw`:

```liquid
{{ content.bodyText | raw }}
```

**Engine configuration**: `FluidParser` with `AllowParentheses`; `MaxSteps = 250_000`, `MaxRecursion = 64`; the
`TemplateContext` culture set to the page's language (so `date` formats "3. März 2026" on `/de/`; unknown ISO →
invariant); member access restricted to `PublishedContent` and the `site` marker, so a template cannot reach
arbitrary .NET objects; the `LiquidRenderScope` travels in `AmbientValues`. Parsed templates are cached per template
`Id`, with the source compared on lookup so a saved change re-parses. `RenderSourceAsync` (the editor's preview path)
never caches.

**Types**

```csharp
public sealed record LiquidRenderScope(IPublishedContentQuery Query, IMediaService Media,
                                       IDictionaryService Dictionary, bool Preview, LiquidRequest? Request = null);
public sealed record LiquidRequest(string Path, IReadOnlyDictionary<string,string> Query)
{ static LiquidRequest FromUri(Uri uri); static LiquidRequest ForPath(string path); string Url; int? Page; string PageUrl(int page); }
public sealed record TemplateValidationResult(bool IsValid, string? Error) { static TemplateValidationResult Valid; }
public sealed class TemplateRenderException(string alias, string message, Exception? inner = null) : Exception { string Alias; }

public interface ILiquidTemplateEngine
{
    TemplateValidationResult Validate(string source);
    Task<string> RenderAsync(Template template, PublishedContent content, LiquidRenderScope scope, CancellationToken ct = default);
    Task<string> RenderSourceAsync(string alias, string source, PublishedContent content, LiquidRenderScope scope, CancellationToken ct = default);
}
public interface IStoredTemplateRenderer   // scoped; builds the scope from the DI services and content.IsPreview
{
    Task<string> RenderAsync(string alias, PublishedContent content, LiquidRequest? request = null, CancellationToken ct = default);
}
```

**Errors.** The engine wraps every failure (missing alias, syntax error, render error) in `TemplateRenderException`.
`CmsLiquidTemplate` catches any exception: in preview it shows a red box naming the alias and the error with a link
to the template editor; on the public site it emits an HTML comment and nothing else.

### 9.5 Partials

A template whose `Role` is `Partial`. It is never offered as a page template and is rendered from another template:

```liquid
{% render 'navigation' %}                {# content = the caller's page (Fluid's render sees the root-level variables) #}
{% render 'card', content: item %}       {# content = item #}
{% include 'card' %}                     {# shares the caller's scope #}
```

`StoredTemplateFileProvider` exposes every stored template to Fluid as `{alias}.liquid`, which is what makes
`render` and `include` resolve. `TemplateReferences.Find(source)` scans the source with a regex (string literals only,
`{% comment %}` and `{% raw %}` blocks ignored, `.liquid` suffix stripped, first-appearance order) to build the usage
graph the back office shows and the delete guard uses.

### 9.6 The template editor

`Admin/Components/TemplateEditor.razor` (~930 lines) plus `wwwroot/dyncms-liquid-editor.js` (~540 lines: a
hand-written editor over a `<textarea>`, no external editor dependency). The JS module exports `init(root, dotnet,
{value, completions})`, `setValue`, `getValue`, `insert` (`$0` marks the caret), `setCompletions`, `focus`, `dispose`
and `writePreview(frame, html)` (writes a `srcdoc` that links the host page's stylesheets and sets
`<base href="/" target="_blank">`). It calls back `OnSourceChanged` (250 ms debounce) and `OnSaveShortcut`. Keys:
`Ctrl+S` save, `Ctrl+Space` completions, `Ctrl+/` (or `Ctrl+7`) comment toggle, `Tab`/`Shift+Tab` indent by two
spaces, auto-pairing of `{{ }}`, `{% %}` and quotes.

Two Core services feed it:

- **`TemplateIntellisense`**: static lists of `Variables`, `Filters`, `Tags`, `Snippets` and `Keywords`, plus
  `PropertiesOf(types, registry)`, which turns document types' properties into completions with the right filter
  already attached, and `Build(properties, partials)`. `Expression(property)`: rich text → `| raw`, media picker →
  `| media_url`, content picker → `| content_url`, tags → `| tags | join: ", "`, date → `| date: "%d %B %Y"`.
  The completion popup is built from *all* document types; the Insert menu and Help chips prefer the types that
  allow the template.
- **`ITemplatePreviewService`**: `GetTargetsAsync(alias)` lists real content (nodes already using this template
  first) plus one synthetic "Sample *Type*" per document type (`TemplatePreviewTarget(Id, Name, Description,
  IsSample)`); `RenderAsync(alias, source, targetId)` renders the *unsaved* source against the chosen target through
  `RenderSourceAsync`, always in preview scope, and returns `TemplatePreviewResult(Success, Html, Error, Target)`.
  Samples fabricate plausible values per editor type (the first image in the library, the site root as a link) so a
  brand-new template shows something before any content exists.

New templates are pre-filled with `TemplateSamples.For(role)` and can start from a `TemplateStarter` (`Key`, `Name`,
`Description`, `Icon`, `Role`, `Source`): `page` "Page template", `list` "Listing page", `blank` "Blank page",
`card` "Card partial", `navigation` "Navigation partial", `partial` "Blank partial"; or from `Scaffold(type, role)`,
which emits markup for every property of a document type (rich text with `| raw`, images with `media_url`, tags as a
loop, dates formatted, toggles as `if`, colours as inline style).

---

## 10. Property editors

An editor is a **definition** in Core and a **component** in UI.

```csharp
public sealed class PropertyEditorDefinition
{
    public required string Alias { get; init; }          // "MyCompany.Markdown"
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string Icon { get; init; } = "edit";          // an Icon.razor name
    public Type? ComponentType { get; set; }
    public IReadOnlyList<PropertyEditorConfigField> ConfigFields { get; init; } = [];
}
public sealed record PropertyEditorConfigField(string Key, string Label, string? Description = null, ConfigFieldType Type = ConfigFieldType.Text);
public enum ConfigFieldType { Text, MultilineText, Number, Boolean }

public interface IPropertyEditorRegistry
{
    IReadOnlyList<PropertyEditorDefinition> All { get; }   // by Name
    PropertyEditorDefinition? Get(string alias);           // case-insensitive
    void Register(PropertyEditorDefinition definition);    // overwrites the same alias silently
}
```

`ConfigFields` become the fields shown for the property in the document-type editor's property dialog (`Boolean` →
checkbox stored as `"true"`/`"false"`, `MultilineText` → textarea, `Number` → number input, `Text` → text input; an
empty value removes the key). The values land in `PropertyType.Config`.

The component inherits `PropertyEditorBase`:

```csharp
public abstract class PropertyEditorBase : ComponentBase
{
    [Parameter] public string? Value { get; set; }                 // the stored string, null when empty
    [Parameter] public EventCallback<string?> ValueChanged { get; set; }
    [Parameter] public PropertyType Property { get; set; }         // name, description, mandatory flag, Config
    [Parameter] public bool Invalid { get; set; }                  // the last publish attempt failed on this property
    protected string InputId    => $"dc-prop-{Property.Alias}";    // the ContentEditor's <label for> points here
    protected string InputClass => Invalid ? "dc-input dc-invalid" : "dc-input";
    protected string?  Config(string key);        // Property.GetConfig
    protected int?     ConfigInt(string key);
    protected double?  ConfigDouble(string key);
    protected bool     ConfigBool(string key);    // "true", "1", "on", "yes"
    protected Task     SetValueAsync(string? value);   // no-op when unchanged, then ValueChanged
}
```

`ContentEditor` hosts it with `<DynamicComponent Type="editor.ComponentType" Parameters="…">` and shows a warning
box when an alias has no registered editor: an unregistered editor never loses data, it just cannot be edited.

**Everything is stored as a string.** The built-ins (aliases in `PropertyEditorAliases`):

| Alias | Component | Config keys | Stored format |
|---|---|---|---|
| `DynCms.TextBox` | `TextBoxEditor` | `placeholder`, `maxLength` | the text as typed |
| `DynCms.TextArea` | `TextAreaEditor` | `rows` (default 5), `maxLength` (`placeholder` is read but not declared, so the dialog does not offer it) | the text |
| `DynCms.RichText` | `RichTextEditor` | none | HTML; `null` when effectively empty |
| `DynCms.Numeric` | `NumericEditor` | `min`, `max`, `step` (default `any`) | invariant-culture number (`"3"`, `"3.5"`); unparsable input kept as typed |
| `DynCms.Toggle` | `ToggleEditor` | `label` | `"true"` / `"false"` |
| `DynCms.DatePicker` | `DatePickerEditor` | `includeTime` (Boolean) | `"yyyy-MM-dd"`, or `"yyyy-MM-ddTHH:mm"` with `includeTime` |
| `DynCms.Dropdown` | `DropdownEditor` | `items` (MultilineText, one per line) | the selected option's text; a value no longer in the list is shown as "(no longer an option)" |
| `DynCms.MediaPicker` | `MediaPickerEditor` | `imagesOnly` (Boolean) | media `Guid` as a string |
| `DynCms.ContentPicker` | `ContentPickerEditor` | none | content `Guid` as a string |
| `DynCms.Tags` | `TagsEditor` | none | JSON array of strings, `null` when empty (Enter or `,` adds, case-insensitive de-dupe) |
| `DynCms.ColorPicker` | `ColorPickerEditor` | none | hex colour as typed (max 7 characters, not validated) |

The rich-text editor is a `contenteditable` driven by `document.execCommand` through `dyncms-admin.js` (`rte.init`,
`rte.setHtml`, `rte.exec`, `rte.saveSelection`, `rte.execWithSelection`; callback `OnHtmlChanged`), pastes as plain
text, has an HTML-source toggle, a link popover and inserts images through `MediaBrowser` in pick mode.

Keep the string contract when you write your own: the Liquid layer, `ContentValueConverter`, the API's value
conversion and the preview sampler all assume it. Registering a definition with an existing alias replaces the
built-in, which is how you would swap the rich-text editor for your own.

---

## 11. Media

`MediaItem` is a tree of folders and files. Files are stored on disk, not in the database:

```
App_Data/media/{first 2 hex of id}/{slug}-{first 8 hex of id}{.ext}
```

served at `/media/{stored path}` with `Cache-Control: public,max-age=86400` and range support (§3).

`UploadAsync` checks the extension against `DynCmsOptions.AllowedUploadExtensions` (`.jpg .jpeg .png .gif .webp .svg
.ico .pdf .txt .md .csv .docx .xlsx .pptx .zip .mp4 .webm .mp3 .wav`, case-insensitive) and the size against
`MaxUploadBytes` (20 MB default). The client's content type is trusted when given, else derived from the extension,
else `application/octet-stream`. Seekable streams are size-checked up front; **non-seekable streams are buffered into
memory first**. There is no check that the target folder exists and no name uniqueness. `CreateFolderAsync` only
requires a name. Deleting a folder deletes its subtree, files first, then the rows in one batch.

`IMediaStorage` (§7) is the seam. Replace `FileSystemMediaStorage` with your own (blob storage, S3) by registering
it **before** `AddDynCms`:

```csharp
builder.Services.AddSingleton<IMediaStorage, BlobMediaStorage>();
builder.AddDynCmsHost(...);           // TryAdd leaves yours in place
```

You then also own serving: `MapDynCmsMedia()` only serves the local folder, so either return absolute URLs from
`GetUrl` or map your own endpoint.

---
## 12. Security

Back-office security is **CMouss.IdentityFramework**, adapted to ASP.NET Core in three pieces:

1. **`CmsIdentity`** (singleton, `src/DynCMS.Core/Security/CmsIdentity.cs`) configures the static `IDFManager`
   against the configured database (SQLite or MSSQL, `TokenValidationMode.DecryptAndValidate`, database backend),
   creates the identity tables via `DatabaseSchema.EnsureTables` (not `EnsureCreated`: the content tables are already
   there), inserts master data, and ensures the DynCMS roles, entities, actions and permissions. It logs a warning
   when the token key is the default. `ICmsIdentity` is the testable surface:

   ```csharp
   public interface ICmsIdentity
   {
       TimeSpan TokenLifetime { get; }
       CmsSignInResult SignIn(string userName, string password, string? ipAddress = null);
       CmsPrincipal?   ValidateToken(string? token);              // rejects tokens of 10 characters or fewer
       int CountUsers();
       IReadOnlyList<CmsUserSummary> GetUsers();
       CmsUserSummary? FindUserByName(string userName);
       CmsUserSummary? GetUser(string userId);
       IReadOnlyList<string> GetRoles();
       string CreateUser(string userName, string password, string fullName, string email, params string[] roleIds);
       void EnsureRole(string roleId, string title);
       void EnsurePermission(string roleId, string entityId, string actionId);
       bool UserHasRole(string userId, string roleId);
       bool UserHasPermission(string userId, string entityId, string actionId);
   }
   public sealed record CmsPrincipal(string UserId, string UserName, string? FullName, string? Email,
                                     IReadOnlyList<string> RoleIds, DateTime TokenExpiresAt) { bool IsInRole(string); bool IsAdmin; }
   public sealed record CmsSignInResult(bool Success, string? Token, DateTime? ExpiresAt, string? Error);
   public sealed record CmsUserSummary(string Id, string UserName, string? FullName, string? Email,
                                       bool IsActive, bool IsLocked, IReadOnlyList<string> RoleIds);
   ```

   Because `IDFManager` is static and process-wide, `CmsIdentity` also has `internal` `Reset()` and `RefreshCache()`
   hooks the runtime calls on database switches and restores.

2. **`Login.razor`** calls `ICmsIdentity.SignIn`, hands the token to the framework's `CookieAuthService`
   (`SetTokenAsync(token, Identity.TokenLifetime)`, which writes the `IDF_AuthToken` cookie from the browser), then
   navigates with `forceLoad: true` so the next request carries it. `returnUrl` is honoured only when it starts with
   `/` and not `//`. `Logout.razor` deletes the cookie in `OnAfterRenderAsync` (it needs JS) and force-loads the login
   page.

3. **`DynCmsAuthenticationHandler`** (scheme `DynCmsAuthDefaults.Scheme = "DynCmsIdentity"`) runs on every request:

   - An `Authorization: Bearer` value starting with `dcms_` is an **API key** (§13.1) and wins over any cookie. It
     resolves to its owner's principal plus one `dyncms:scope` claim per granted scope and a `dyncms:api-key` claim
     with the key id. Failure → `AuthenticateResult.Fail("The API key is unknown, revoked or expired.")`.
   - Otherwise the token is the cookie (`Identity.TokenCookieName`) **or, when there is no cookie**, the bearer
     value. It is validated through the framework (retrying URL-unescaped when it contains `%`); an invalid token is
     `NoResult`, not a failure.
   - The `ClaimsPrincipal` carries `NameIdentifier`, `Name`, `dyncms:auth` (`session` or `api-key`), optional
     `GivenName` / `Email`, and one `Role` claim per role. That is what makes `[Authorize]`, `AuthorizeView` and
     `AuthorizeRouteView` work.
   - **Challenge**: an API request gets a JSON `401` (`ProblemDto`) with `WWW-Authenticate: Bearer realm="DynCMS"`;
     anything else is redirected to `DynCmsOptions.Identity.LoginPath` with a `returnUrl`. "API request" means: the
     path is under the API or MCP base path, **or** an `Authorization` header is present, **or** the request is not a
     navigation (`Sec-Fetch-Mode` is not `navigate` and `Accept` lacks `text/html`).
   - **Forbid**: API requests get a JSON `403`; HTML requests get the bare `403`.

**Roles** (`CmsRoles`): `Administrators` (the framework's own admin role; `CmsRoles.Admin`) and `Editors`
(`CmsRoles.Editor`, created by DynCMS).
**Entities** (`CmsPermissions.Entities`): `Content`, `Media`, `DocumentType`, `Template`, `Dictionary`.
**Actions**: `Read`, `Create`, `Update`, `Delete`, `Publish` (`Create`/`Update`/`Delete` are framework master data,
the other two are added by DynCMS).

Editors get all five actions on `Content`, `Media` and `Dictionary`, plus `Read` on `DocumentType` and `Template`.
Administrators are authorised **by role**, not by permission rows.

**Where permissions are enforced.** Inside the back office, pages are gated by `[Authorize]` and, for `/admin/users`
and `/admin/data`, `[Authorize(Roles = "Administrators")]`; the Blazor components call the services directly and do
not consult the permission rows. The entity/action permissions are enforced by `CmsAccess` in the API layer (§13),
which is also what decides which scopes a user may put on an API key. Blazor circuits have no reliable `HttpContext`,
so UI code that needs the caller uses `CmsCaller.From(principal)` and the static `CmsAuthorization` helpers with the
cascaded `AuthenticationState`, never the scoped `ICmsAccess`.

The identity screens on `/admin/users` are the framework's `IdentityAdminPart` (users, roles, entities, actions,
permissions, apps, tokens, maintenance). They are Bootstrap-based and that page pulls Bootstrap 5.3.3 from a CDN;
both admin layouts pull the Inter font from Google Fonts.

### Production checklist

- [ ] Change `DynCms:Identity:TokenEncryptionKey`; the default is logged as a warning at startup.
- [ ] Change `DynCms:Identity:AdminPassword`; it seeds the administrator on first run.
- [ ] Revoke any API key you committed by accident (`mcp.md`, §23).
- [ ] Keep `dyncms.database.json` out of source control and off public paths; it holds the SQL password.
- [ ] Decide whether `AllowMultipleSessions` and the 30-day token lifetime suit you.
- [ ] Serve over HTTPS; the token cookie is a bearer credential.
- [ ] Decide whether `Api.PublicOpenApi` (anonymous `openapi.json`) is acceptable.

---

## 13. Management API and MCP server

Most of what the back office does can be done over HTTP, so a script, an integration or an AI agent can manage a
site: languages, the dictionary, document types, templates, content, publishing, media, users (list and create),
backups (list and create) and API keys. Not exposed: restore, backup download, database switch and reset, and user
update/delete. Two front ends share one application layer:

```
REST  /api/v1/**  ──┐
                    ├──►  CmsManagement (DynCMS.Core/Api)  ──►  IContentService, ITemplateService, …
MCP   /mcp        ──┘          │
                               └── ICmsAccess: scope check (API keys) + role/permission check (identity framework)
```

Both are mapped by `UseDynCmsHost()` (`app.MapDynCmsApi()` and `app.MapDynCmsMcp()`), so a site gets them without
any extra code. `DynCmsOptions.Api` (`DynCms:Api` in configuration) switches them off or moves them (§16).

### 13.1 API keys

A key is created in the back office at **Settings → API & AI agents** (`/admin/settings/api`) and shown **once**.
Format: `dcms_` + 32 random bytes as unpadded base64url (48 characters). The `ApiKeys` row holds `Name`, `Prefix`
(the first 13 characters, for display), `Hash` (lower-case hex SHA-256 of the secret), `UserId`, `UserName`,
`Scopes`, `CreatedAt`, `ExpiresAt`, `LastUsedAt` (written at most every five minutes), `RevokedAt`. Send it as
`Authorization: Bearer dcms_…`.

```csharp
public interface IApiKeyService      // singleton
{
    string SecretPrefix { get; }     // "dcms_"
    Task<IReadOnlyList<ApiKey>> GetAllAsync();
    Task<IReadOnlyList<ApiKey>> GetForUserAsync(string userId);
    Task<ApiKey?>        GetAsync(Guid id);
    Task<ApiKeyCreated>  CreateAsync(string userId, string userName, string name, IEnumerable<string> scopes, DateTime? expiresAt);
    Task<bool>           RevokeAsync(Guid id);
    Task<ApiKeyPrincipal?> ValidateAsync(string secret);
}
public sealed record ApiKeyCreated(ApiKey Key, string Secret);
public sealed record ApiKeyPrincipal(ApiKey Key, CmsUserSummary User);
```

Every request is authorised in two layers, both in `CmsAccess`:

1. the key must carry the scope the operation needs (`ApiScopes.For(entity, action)`), and
2. the owner must be an administrator or hold the identity framework permission for that entity and action.

So a key can never exceed its owner: the UI only offers the scopes the signed-in user could exercise themselves
(`CmsAuthorization.GrantableScopes`), an editor's key with `*` still cannot write document types, and a disabled or
locked owner fails validation. Revocation is immediate (the validation cache entry is removed). Validated keys are
cached in memory for 60 seconds; within that window a cached hit re-checks the key's expiry but **not** the owner's
active/locked state, so disabling a user takes up to a minute to bite.

| Scope | Gates |
|---|---|
| `content:read` · `content:write` · `content:publish` · `content:delete` | Content read / create+update+move / publish+unpublish / delete. `content:read` also lists languages |
| `media:read` · `media:write` · `media:delete` | Media library |
| `document-types:read` · `document-types:write` | Document types (write covers create, update and delete) **and language create/update/delete** |
| `templates:read` · `templates:write` | Stored templates; `read` also covers validation, preview and the Liquid reference |
| `dictionary:read` · `dictionary:write` | The dictionary |
| `analytics:read` | Visitor reports and the page view log (§15) |
| `users:read` · `users:write` · `system:read` · `system:manage` · `api-keys:manage` · `analytics:manage` | Administrators only |
| `*` | Everything the owner may do |

Session users (the cookie) manage their own keys; through the API, key management needs `api-keys:manage`, so an
agent cannot mint itself a broader key. Administrators can create keys for other users (`forUser`) and list every
user's keys (`?all=true`).

```csharp
public interface ICmsAccess          // scoped; the caller comes from HttpContext.User
{
    CmsCaller? Caller { get; }
    CmsCaller Require();                                 // 401 when anonymous
    CmsCaller Require(string entity, string action);     // 403 with the missing scope or permission in the message
    CmsCaller RequireAdmin(string scope);
    bool Can(string entity, string action);
    IReadOnlyList<ApiScopeDefinition> GrantableScopes();
}
public sealed record CmsCaller(string UserId, string UserName, IReadOnlyList<string> Roles, string AuthMethod,
                               IReadOnlyList<string> Scopes, Guid? ApiKeyId) { bool IsAdmin; bool IsApiKey; static CmsCaller? From(ClaimsPrincipal?); }
```

### 13.2 REST API

Base path `/api/v1` (`DynCmsOptions.Api.BasePath`). The group requires authorization; `GET /api/v1/` is anonymous
and `GET /api/v1/openapi.json` is anonymous while `PublicOpenApi` is true. The OpenAPI document is filtered to the
base path and declares a global `bearer` security scheme named `ApiKey`.

`GET /api/v1/` returns `ApiInfoDto`: name, version, `ready`, the endpoint paths, modelling hints (always), and, when
authenticated, `caller` (id, name, roles, auth method, scopes, key id, effective `Entity:Action` permissions) and,
when the runtime is also ready, `counts`.

| Area | Method and route | Access |
|---|---|---|
| System | `GET /system/database?role=` · `GET /system/backups?role=` · `POST /system/backups?role=` `{ note? }` (`role`: `primary` default, or `analytics`) | admin `system:read` / `system:manage` |
| Users | `GET /users` · `POST /users` `{ userName, password (≥ 6), fullName?, email?, roles? (default Editors) }` · `GET /roles` | admin `users:read` / `users:write` |
| API keys | `GET /api-keys/scopes` (any authenticated) · `GET /api-keys/scopes/grantable` · `GET /api-keys?all=` · `POST /api-keys` `{ name, scopes, expiresAt?, forUser? }` · `DELETE /api-keys/{id}` | session: own keys; API key: admin `api-keys:manage` |
| Languages | `GET /languages` · `GET /languages/{isoCode}` | `content:read` |
| | `POST /languages` · `PUT /languages/{isoCode}` · `DELETE /languages/{isoCode}` (`SaveLanguageRequest`: `isoCode, name, isDefault, isMandatory, fallbackIsoCode, sortOrder`) | `document-types:write` |
| Dictionary | `GET /dictionary` · `GET /dictionary/values?culture=` · `GET/PUT/DELETE /dictionary/{idOrKey}` · `POST /dictionary` | `dictionary:read` / `dictionary:write` |
| Analytics | `GET /analytics?period=\|from=&to=` · `GET /analytics/pages?take=&search=` · `GET /analytics/views?path=&visitorId=&sessionId=&ip=&country=&search=&notFound=&bots=&skip=&take=` | `analytics:read` |
| | `GET/PUT /analytics/settings` · `GET /analytics/data` · `DELETE /analytics/views?olderThanDays=` | admin `analytics:manage` |
| Document types | `GET /property-editors` · `GET /document-types` · `GET/PUT/DELETE /document-types/{idOrAlias}` · `POST /document-types` | `document-types:read` / `document-types:write` |
| Templates | `GET /templates` · `GET /templates/liquid-reference` · `GET /templates/{idOrAlias}` · `POST /templates/validate` `{ content }` · `POST /templates/preview` `{ content, alias?, contentId? }` | `templates:read` |
| | `POST /templates` · `PUT/DELETE /templates/{idOrAlias}` · `POST /templates/{idOrAlias}/duplicate` · `POST /templates/{alias}/override` | `templates:write` |
| Content | `GET /content/tree?rootId=&depth=` · `GET /content?parentId=` · `GET /content/search?q=&take=` (1–200, default 25) · `GET /content/{id}` | `content:read` |
| | `POST /content` · `PUT /content/{id}` · `POST /content/{id}/move` `{ direction }` | `content:write` (+ `content:publish` when `publish: true`) |
| | `POST /content/{id}/publish` `{ cultures? }` · `POST /content/{id}/unpublish` `{ culture? }` | `content:publish` |
| | `DELETE /content/{id}` | `content:delete` |
| Published | `GET /published/route?path=&preview=&culture=` · `GET /published/{id}` · `GET /published/{id}/children` · `GET /published/by-type/{alias}` (all take `preview`, `culture`) | `content:read` |
| Media | `GET /media?folderId=` · `GET /media/{id}` | `media:read` |
| | `POST /media/folders` `{ name, parentId? }` · `POST /media/upload` `{ fileName, contentBase64, folderId?, mimeType? }` · `POST /media/upload-file` (multipart, field `file`, optional `folderId`; antiforgery disabled) · `PUT /media/{id}` `{ name }` | `media:write` |
| | `DELETE /media/{id}` | `media:delete` |

Every DTO is a record in `src/DynCMS.Core/Api/Dtos.cs`; the `[Description]` attributes there are what OpenAPI and
the MCP tool schemas show. JSON is camel-case, nulls omitted, enums as camel-case strings (`ApiJson.Options`).

**Errors** are `ProblemDto { status, title, detail, errors? }`. `CmsApiException` carries the status; the
translation of service exceptions (`CmsManagement.Translate`) maps a message containing "not found" → `404`,
"already" / "still" / "is used by" → `409`, everything else → `400`; `DynCmsNotConfiguredException` → `503`; an
unexpected exception → `500` with the message as `detail`. `errors` carries hints: `{ properties: [...] }` for an
unknown alias, `{ editors: [...] }`, `{ settings: [...] }` (unknown config key), `{ templates: [...] }`,
`{ allowed: [...] }` (template not allowed on the type), `{ languages: [...] }`, `{ roles: [...] }`.

Conventions worth knowing when writing a client:

- Document types and templates are addressed by **id or alias**. `PUT` is a partial update: only fields present
  change, except `properties`, which is the complete list (matched by `id`, then `alias`; the rest are removed).
  Editor aliases, template aliases and config keys are validated against the registries and the definition, so a typo
  fails fast with the valid list. Partials are rejected as page templates; `defaultTemplate` is added to
  `allowedTemplates` automatically, and when the default is removed from the allowed list the first allowed one
  becomes the default.
- Content `values` are keyed by property alias (case-insensitive, normalised to the type's spelling). JSON strings
  are stored as-is; `true`/`false` → `"true"`/`"false"`; integers via `Int64`, other numbers via `double` (invariant);
  arrays and objects → their JSON text; `null` clears. Unknown aliases are a `400`. `replaceValues: true` nulls every
  existing draft value first.
- `POST /content` and `PUT /content/{id}` accept `publish: true`; when validation fails the draft is still saved
  and the response is `PublishResultDto { success: false, errors, content }` (status `201`/`200`).
  `POST …/publish` on failure returns `200` with `success: false` and `content: null`.
- For content whose document type varies by culture, `culture` on create/update names the language the `name`,
  `urlSegment` and varying `values` belong to (default language when omitted); shared values go to the node
  whichever culture is named. `GET /content/{id}` returns `cultures` with each language's state and URL.
- Component templates come back with `id: null` and `kind: "component"`; `PUT`, `DELETE` and duplicate on them are
  a `409` pointing at `…/override`.
- There is no paging on list endpoints and no rate limiting.

### 13.3 MCP server

`/mcp` is a **stateless** Streamable HTTP MCP server (`ModelContextProtocol.AspNetCore`; server name `DynCMS`,
version from the assembly). The endpoint requires authentication; the agent sends its API key as a header:

```bash
claude mcp add --transport http dyncms https://your-site/mcp --header "Authorization: Bearer dcms_…"
```

The 47 tools in `src/DynCMS.Core/Api/Mcp/DynCmsMcpTools.cs` are a **subset** of the REST surface (no children list,
no published-by-id/children/by-type, no roles, no backup list, no API-key management, no multipart upload):

| Group | Tools |
|---|---|
| Overview | `cms_overview`, `list_property_editors`, `database_info`, `create_backup(note?)` |
| Analytics | `analytics_report(period?, from?, to?)`, `analytics_pages(period?, from?, to?, take?, search?)`, `analytics_views(… filters …, skip?, take?)` (§15) |
| Languages | `list_languages`, `create_language(request)`, `update_language(isoCode, request)`, `delete_language(isoCode)` |
| Dictionary | `list_dictionary_items`, `get_dictionary_item(idOrKey)`, `get_dictionary_values(culture?)`, `create_dictionary_item`, `update_dictionary_item`, `delete_dictionary_item` |
| Document types | `list_document_types`, `get_document_type(idOrAlias)`, `create_document_type`, `update_document_type`, `delete_document_type` |
| Templates | `list_templates`, `get_template`, `liquid_reference`, `create_template`, `update_template`, `delete_template`, `duplicate_template`, `create_template_override(alias)`, `validate_template(content)`, `preview_template(request)` |
| Content | `get_content_tree(rootId?, depth?)`, `get_content(id)`, `search_content(term, take?)`, `get_published_content(path, preview?, culture?)`, `create_content`, `update_content(id, request)`, `publish_content(id, cultures?)`, `unpublish_content(id, culture?)`, `move_content(id, direction)`, `delete_content(id)` |
| Media | `list_media(folderId?)`, `get_media(id)`, `create_media_folder(name, parentId?)`, `upload_media(request)`, `rename_media(id, name)`, `delete_media(id)` |
| Users | `list_users`, `create_user(request)` |

Read-only tools are annotated as such; the destructive ones (`delete_*`) are marked destructive. Tool errors carry the
same status and message as the REST API (`404 Content '…' was not found.` plus the `errors` JSON). The server
instructions tell the agent to start with `cms_overview`, to model document types before content, that values are
strings in the editor's stored format, that rich text needs `| raw`, and how languages and the dictionary work.

### 13.4 Extending it

Add the operation to `CmsManagement` (a `partial class` split by area) with its `access.Require(...)` call, then
expose it once in `ManagementApiEndpoints` and once as a tool in `DynCmsMcpTools`. Because the tools bind their JSON
schema from the DTO records in `Api/Dtos.cs`, the `[Description]` attributes there are what the agent reads. Two
gotchas from building it: the MCP SDK needs `TypeInfoResolver` set on a custom `JsonSerializerOptions` (hence
`DefaultJsonTypeInfoResolver` in `ApiJson`), and minimal-API multipart uploads need `.DisableAntiforgery()`.

---

## 14. Backups, restore and database maintenance

`IDatabaseMaintenanceService` (`src/DynCMS.Core/Data/DatabaseMaintenanceService.cs`, singleton) is behind
`/admin/data` and the `/system/*` API routes. All operations are serialised through one `SemaphoreSlim`.

```csharp
public interface IDatabaseMaintenanceService
{
    string BackupFolder { get; }
    Task<DatabaseInfo>                      GetInfoAsync(DatabaseRole role = Primary);
    Task<IReadOnlyList<DatabaseBackupInfo>> ListBackupsAsync(DatabaseRole role = Primary);
    DatabaseBackupInfo?                     FindBackup(string fileName);        // any role; blocks on the gate
    Task<DatabaseBackupInfo>                CreateBackupAsync(string? note = null, DatabaseRole role = Primary);
    Task                                    RestoreBackupAsync(string fileName, bool backupFirst = true, DatabaseRole role = Primary);
    Task<DatabaseBackupInfo>                ImportBackupAsync(string fileName, Stream content, DatabaseRole role = Primary);
    Task                                    DeleteBackupAsync(string fileName);
    Task<DatabaseTestResult>                SwitchConfigurationAsync(DatabaseSetupRequest request);
    Task<DatabaseTestResult>                SwitchAnalyticsConfigurationAsync(DatabaseSetupRequest? request, AnalyticsHistoryAction history);
    Task<AnalyticsHistoryInfo?>             GetLeftoverAnalyticsAsync();        // old rows in the primary db while analytics is separate
    Task<AnalyticsMigrationResult?>         ResolveLeftoverAnalyticsAsync(AnalyticsHistoryAction history);
    Task                                    ResetConfigurationAsync();
}
public sealed record DatabaseInfo(bool IsReady, DatabaseProvider Provider, string Description, string ConfigurationFile,
    bool ConfigurationFileExists, string BackupFolder, string? SqliteFile, long? SqliteFileSize, DateTime? SqliteLastWriteUtc,
    string? Server, int? Port, string? Database, string? UserName, bool? TrustServerCertificate,
    IReadOnlyList<DatabaseTableInfo> Tables, string? TablesError, DatabaseRole Role = Primary, bool SharedWithPrimary = false);
public sealed record DatabaseTableInfo(string Name, long? Rows);
public sealed record DatabaseBackupInfo(string FileName, string FullPath, long? Size, DateTime CreatedUtc,
    DatabaseProvider Provider, string? Database, string? Note, bool IsLocalFile, DatabaseRole Role = Primary) { string Extension; }
```

**Two databases, one mechanism.** Every operation takes a `DatabaseRole`. `Primary` is the content database;
`Analytics` is the analytics database when one is configured (§4, §15) and is refused for backup and restore while
analytics still shares the primary database (the primary backup covers it then). Backups of the analytics database
are ordinary backups with the file name prefix `analytics-` and `Role: Analytics` in the manifest; a restore checks
the backup's role and provider against the target, so a primary backup can never be restored into the analytics
database or the other way round, and restoring one never touches the other. After an analytics restore the tables
are brought up to date and the worker drops its caches (`DynCmsRuntime.NotifyAnalyticsStoreChanged`); nobody is signed out.

**What a backup is.** The whole database: content, templates, languages, dictionary, media *records*, and the
identity tables (users, sessions, API keys) — plus the analytics tables while they share that database. It does
**not** include the files under `App_Data/media` or `dyncms.database.json`. Back those up separately.

| | SQLite | SQL Server |
|---|---|---|
| Create | Online copy via `SqliteConnection.BackupDatabase`, then `PRAGMA journal_mode=DELETE`; file `{dbstem}-{yyyyMMdd-HHmmss}.db` (local time) in `BackupFolder` | `BACKUP DATABASE … WITH INIT, FORMAT, COPY_ONLY, CHECKSUM` to the server's `InstanceDefaultBackupPath`; file `{Database}-{timestamp}.bak` **on the server**; fails if the server reports no default path |
| Restore | Verify the file, clear pools, `BackupDatabase` from the read-only source over the live file, `PRAGMA wal_checkpoint(TRUNCATE)` | Through `master`: `SET SINGLE_USER WITH ROLLBACK IMMEDIATE` → `RESTORE DATABASE … WITH REPLACE` → `SET MULTI_USER` |
| Import | `.db`, `.sqlite`, `.sqlite3`; verified with `PRAGMA schema_version` | `.bak`; noted as "copy this file to the SQL Server's backup folder before restoring" |

`RestoreBackupAsync` requires the backup's provider to match the current one, takes an automatic backup first when
`backupFirst` (the default), then calls `RefreshAfterRestoreAsync` (initializer re-runs, identity caches refresh,
startup tasks do not run). `ImportBackupAsync` sanitises the name to `[A-Za-z0-9][A-Za-z0-9 _.\-()]{0,200}`, adds
` (2)`, ` (3)` on collisions and never restores by itself. `DeleteBackupAsync` deletes the local file and always
removes the manifest entry.

**The manifest** `backups.json` in `BackupFolder` is a JSON array of `{ FileName, Path (absolute), CreatedUtc, Provider,
Database, Note, Size, Role }`, newest first (entries without `Role` are primary). Absolute paths make it machine-specific. Files found in the folder that are not
in the manifest are listed with the note "Found in the backup folder".

`SwitchConfigurationAsync` = `TestConnectionAsync` → optional `CREATE DATABASE` → `DynCmsRuntime.SwitchAsync`
(startup tasks run against the new database, so an empty one gets seeded). Its success message reminds you to sign
in again: sessions belong to the database they were created in. The analytics entry is carried over unchanged.
`SwitchAnalyticsConfigurationAsync(request, history)` moves analytics to the database in `request` (or back into the
primary one when `request` is null): it tests the target with the analytics schema, optionally creates the SQL Server
database, then `DynCmsRuntime.SwitchAnalyticsStoreAsync` pauses the worker (`AnalyticsQueue.Paused`), creates the
tables on the target, and applies the `AnalyticsHistoryAction` the person chose — `Move` copies page views and cached
locations across in batches (`AnalyticsStoreMigrator`), `Delete` discards them — then removes them from the source
(dropping the two tables when the source is the primary database), saves the configuration and bumps
`AnalyticsQueue.StoreGeneration` so the worker forgets its sessions and pending lookups. Nobody is signed out. The old
store never silently keeps a copy: history is either moved or deleted, and the UI asks which.

**Leftover history.** When analytics runs in its own database but the primary one still holds analytics tables (a setup
that pointed a new analytics database at an existing site, or a hand-edited configuration), `GetLeftoverAnalyticsAsync`
reports the counts, the initializer logs a warning, the Data overview and the analytics configuration page show a notice
with the two actions, and `ResolveLeftoverAnalyticsAsync` moves or deletes the rows and drops the tables from the
primary database. The setup page handles the same case up front: with an existing content database and a separate
analytics database, `CountExistingAnalyticsAsync` finds the old page views and the page asks whether to move or delete
them before finishing (`CompleteSetupAsync(request, analytics, history)`). `ResetConfigurationAsync` deletes the configuration file (both entries) and
returns to setup mode.

**The Data section** (`/admin/data[/{view}]`, Administrators): *Overview* (engine, ready flag, table count, on-disk
size including `-wal`/`-shm`, connection facts, per-table row counts, last five backups, "Back up now");
*Configuration* (the setup form pre-filled from the current configuration in existing-database mode, "Test
connection", "Save and switch" behind a confirm, then a forced reload to the login page); *Backups* (note + create,
upload via `InputFile` with progress, up to 8 GB, download for local files, restore with "Take a backup first" on by
default, delete); *Reset* (acknowledgement checkbox + confirm → `/setup`). The tree groups these under **Primary
database**; an **Analytics database** group holds *Configuration* (`/admin/data/analytics`,
`DataAnalyticsConfiguration`: same database or a separate one, connection fields through the shared
`DatabaseConnectionFields`, a move-or-delete choice for the recorded history, a notice with the same choice for
history left in the primary database, test, "Save and apply") and *Backups & restore*
(`/admin/data/analytics-backups`, the same `DataBackups` component with `Role="Analytics"`, which shows a pointer to
the primary backups while analytics is shared). The overview has an "Analytics database" card. `DataFormat`
(`Bytes`, `Rows`, `Local`, `Ago`) is the shared formatter.

---

## 15. Analytics

Every public page view is recorded, Google-Analytics style, without any JavaScript on the site: the renderer
reports the view, a background worker writes it, and the back office **Analytics** section (`/admin/analytics`)
reports on it. Everything lives in `DynCMS.Core/Analytics/` (model, tracker, worker, geolocation, reports) and
`DynCMS.UI/Admin/Components/Analytics/` (the section's views). Added 2026-09-26.

### What is recorded

Table `AnalyticsPageViews` (`PageView`): time (UTC), host, path, query, content id and name, culture, whether the
path was found, referrer and referrer host, pseudonymous **visitor id** (HMAC of IP address + user agent, keyed with
`Identity.TokenEncryptionKey`; never reversible), **session id** (new after 30 minutes without a view,
`Analytics.SessionTimeoutMinutes`), IP address (optional, optionally anonymised), user agent and what
`UserAgentParser` makes of it (browser + major version, operating system, device class, bot flag), the visitor's
preferred language, and the geolocation (country code, country, region, city, coordinates). Table `AnalyticsGeoIp`
caches one lookup per address. Bots are flagged from the user agent and left out of every report; by default they are
not stored at all.

### Where it is stored

`AnalyticsDbContext` (`PageViews`, `GeoIpEntries`) is its own context, created by `AnalyticsDbContextFactory` for
`DatabaseConfigurationStore.Resolve(DatabaseRole.Analytics)`: the `Analytics` entry of `dyncms.database.json` when
there is one, else the primary database, so by default the two tables sit next to the content. Giving analytics its
own database (setup page step 5, or Data → Analytics database) keeps the page view history out of content backups
and restores, gives it its own backups, and on SQLite stops page view writes from competing with editors for the
single-writer lock. The initializer creates the analytics tables on whichever database applies; a separate analytics
database that is unreachable is logged and does not stop the site (views are dropped until it is back, and the worker
re-checks the schema before its first write). See §4 for the file and §14 for moving and backing up.

### How a view gets in

1. `CmsContentRenderer` calls `IAnalyticsPageTracker.Track(...)` after resolving the route (§8). The tracker is
   scoped: it pairs the request with `AnalyticsVisitorContext`, which reads the IP (honouring `X-Forwarded-For`,
   `CF-Connecting-IP`, `X-Real-IP` when *Trust proxy headers* is on), user agent, `Accept-Language`, `Referer` and
   host from the HTTP request while prerendering, and from the SignalR connection afterwards
   (`AnalyticsCircuitHandler` copies it from `IHttpContextAccessor` when the circuit opens).
2. The prerender records the first view and persists a marker in `PersistentComponentState`, so the interactive
   render of the same page does not count it again. Navigations inside the circuit are recorded by the interactive
   render, with the previous page as an internal referrer. Not-found pages are counted from the interactive render
   only (the status-code re-execute discards the prerendered output), and paths with a file extension
   (`/favicon.ico`) are ignored.
3. `IAnalyticsTracker` (`AnalyticsQueue`) is a bounded in-memory channel; rendering never waits for the database.
   `AnalyticsWorker` (a hosted service) drains it in batches, assigns visitor and session ids, applies the IP
   settings, fills the location from the cache, inserts the rows, then resolves missing locations through
   `IGeoLocator` (throttled, `Analytics.GeoLookupDelayMilliseconds`) and updates the rows. Once an hour it deletes views
   older than the retention and stale cache entries. While the database is not ready, queued views are dropped.

Signed-in back-office users are not tracked unless *Include signed-in back-office users* is on; preview renders are
never tracked; `ExcludedPaths` (exact or `prefix*`) are skipped.

### Geolocation

`HttpGeoLocator` calls the configured URL with `{ip}` replaced and reads the JSON for the field names the common
services use (`country`/`country_name`, `country_code`/`countryCode`, `region`/`regionName`, `city`,
`latitude`/`lat`, `longitude`/`lon`, `timezone`, `organization_name`/`org`/`isp`), so
`https://get.geojs.io/v1/ip/geo/{ip}.json` (default, no key), `http://ip-api.com/json/{ip}`, `https://ipapi.co/{ip}/json/`
or `https://ipwho.is/{ip}` work without code. Private and loopback addresses are never sent. Register your own
`IGeoLocator` before `AddDynCms` (for example over a MaxMind GeoLite2 database) to look up offline. Failed lookups are
cached for a day, successful ones for `Analytics.GeoCacheDays`.

### Settings

Administrators change the behaviour at **Analytics → Settings & data** (`AnalyticsSettings`: enabled, store IP,
anonymise IP, track signed-in users, track bots, retention days, geolocation on/off and URL, trust proxy headers,
excluded paths). They are stored as JSON under the key `analytics` in the new `Settings` table (`ISettingsStore`,
a generic key → JSON store for runtime settings) and cached in `IAnalyticsSettingsService`, which the initializer
reloads after every database initialisation. Until saved, `DynCmsOptions.Analytics.Defaults` (`DynCms:Analytics:Defaults`)
applies. The same page shows what is stored, exports CSV (`/admin/analytics/export?from=&to=&bots=`,
any signed-in user), clears the geolocation cache and deletes views (older than *n* days, or all).

### Reports (`IAnalyticsService`)

All reports take an `AnalyticsRange` (half-open UTC interval; `LastDays`/`Days` build one from local calendar days)
and exclude bots: `GetSummaryAsync` (page views, unique visitors, sessions, pages per session, bounce rate, average
session length, 404s), `GetActiveVisitorsAsync` (last *n* minutes), `GetTimeSeriesAsync` (per hour for ranges up to two
days, else per day, aligned to the caller's UTC offset, gaps filled), `GetPagesAsync`, `GetReferrersAsync` (grouped by
host with a channel: Direct, Search, Social, Referral, Internal — `ReferrerChannels.Classify`), `GetCountriesAsync`,
`GetGeoPointsAsync` (per city with coordinates, for the map), `GetBrowsersAsync`, `GetOperatingSystemsAsync`,
`GetDevicesAsync`, `GetLanguagesAsync`, `GetHostsAsync`, `GetViewsAsync` (the raw log with filters and paging),
`GetDataInfoAsync`, `DeleteViewsAsync`, `ClearGeoCacheAsync`, `ExportCsvAsync`. Unique visitors are distinct visitor
ids in the range; a visitor whose address changes counts again.

### The back office section

`/admin/analytics` (every signed-in user; settings need `Administrators`): a period picker (today, yesterday, 7/30/90
days, 12 months, custom dates) shared by the views **Overview** (stat tiles with the change against the previous
period, live "right now" count, an inline SVG traffic chart, top pages, sources, countries, devices and browsers),
**Pages**, **Visitors** (the live log: time, page, location + IP + visitor id, browser/OS/device, source; filters by
path, visitor, session, IP, country, free text, 404s, bots; optional 10-second auto-refresh; every value links to a
filtered log), **Map** (Leaflet, loaded on demand from cdnjs, OpenStreetMap tiles — `Analytics.MapTileUrl` /
`MapTileAttribution` — one bubble per city sized by views, with country and city lists beside it), **Sources**,
**Technology** and **Settings & data**. The dashboard shows a "Views · 7 days" tile.

### API and MCP

Scopes `analytics:read` (reports and the log; any user) and `analytics:manage` (settings, data info, delete;
administrators). REST: `GET /analytics?period=|from=&to=` (the overview report), `GET /analytics/pages`,
`GET /analytics/views` (filters: `path` — append `*` for a prefix —, `visitorId`, `sessionId`, `ip`, `country`,
`search`, `notFound`, `bots`, `skip`, `take`), `GET`/`PUT /analytics/settings`, `GET /analytics/data`,
`DELETE /analytics/views?olderThanDays=`. MCP tools: `analytics_report`, `analytics_pages`, `analytics_views`.
`period` is `today`, `yesterday`, `7d`, `30d` (default), `90d` or `12m`; dates are local `yyyy-MM-dd`, inclusive.

### Privacy notes

IP addresses are personal data in many jurisdictions: decide between storing them, anonymising them (last octet /
last 80 bits zeroed, like Google Analytics' `anonymize_ip`) or not storing them, and set a retention. With geolocation
on, visitor addresses are sent to the configured third-party service. No cookie is set and nothing runs in the
visitor's browser, which also means ad blockers cannot see it.

## 16. Configuration reference

### `DynCmsOptions` (section `DynCms`, bound by `AddDynCmsHost`)

| Option | Default | Notes |
|---|---|---|
| `BasePath` | `null` → content root, else `AppContext.BaseDirectory` | everything below is relative to it |
| `DatabaseConfigFile` | `dyncms.database.json` | |
| `MediaRootPath` / `MediaRequestPath` | `App_Data/media` / `/media` | |
| `BackupRootPath` | `App_Data/backups` | |
| `DefaultCulture` | `en` | used only when the first language is created; later changes are ignored |
| `MaxUploadBytes` | 20 MB | also caps base64 uploads through the API |
| `AllowedUploadExtensions` | see §11 | `HashSet<string>`, case-insensitive |
| `Identity.TokenEncryptionKey` | `change-this-dyncms-token-key` | warned about at startup |
| `Identity.AdminUserName` / `AdminPassword` | `admin` / `Admin123!` | seeds the administrator on first run |
| `Identity.TokenLifetimeDays` | 30 (minimum 1) | |
| `Identity.AllowMultipleSessions` | `true` | |
| `Identity.LoginPath` | `/admin/login` | only the server-side challenge redirect honours it; the Blazor `RedirectToLogin` and `Logout` use the constant `DynCmsUi.LoginPath` |
| `Identity.TokenCookieName` | `IDF_AuthToken` | |
| `Api.Enabled` / `Api.BasePath` | `true` / `/api/v1` | |
| `Api.McpEnabled` / `Api.McpPath` | `true` / `/mcp` | |
| `Api.PublicOpenApi` | `true` | anonymous `openapi.json` |
| `Analytics.Defaults.*` | see §15 | `Enabled`, `StoreIpAddress`, `AnonymizeIp`, `TrackSignedInUsers`, `TrackBots`, `RetentionDays` (365), `GeoLookupEnabled`, `GeoLookupUrl` (GeoJS), `TrustProxyHeaders`, `ExcludedPaths` — the values until an administrator saves settings in the back office |
| `Analytics.SessionTimeoutMinutes` / `GeoCacheDays` / `GeoLookupDelayMilliseconds` / `QueueCapacity` | 30 / 30 / 250 / 10 000 | worker tuning |
| `Analytics.MapTileUrl` / `MapTileAttribution` | OpenStreetMap | the back-office map's tiles |
| `Api.MaxRequestBodyBytes` | 32 MB | **not used anywhere**; uploads are limited by `MaxUploadBytes` and Kestrel's defaults |
| `Plugins.Enabled` | `true` | load plugins at all (§25) |
| `Plugins.RootPath` | `App_Data/plugins` | one sub-folder per plugin |
| `Plugins.AllowUpload` | `true` | uploads from the back office and the API; off = deploy by copying only |
| `Plugins.MaxPackageBytes` | 64 MB | upload limit |
| `Plugins.EnableControllers` | `true` | `MapControllers()` and plugin `[ApiController]` discovery |
| `Plugins.StaticAssetsRequestPath` | `/_content` | plugin `wwwroot` is served at `{prefix}/{id}/…` |

`DynCmsPaths` (singleton) resolves the paths to absolute ones once, at construction: `BasePath`, `DatabaseConfigPath`,
`MediaRootPath`, `MediaRequestPath` (normalised to `/…`), `BackupRootPath`, `PluginsRootPath`.

### `DynCmsHostOptions` (the delegate passed to `AddDynCmsHost`)

| Option | Default | Notes |
|---|---|---|
| `ConfigurationSection` | `DynCms` | the section bound to `DynCmsOptions` |
| `Layout` | `typeof(SiteLayout)` | any `LayoutComponentBase`; validated to be an `IComponent` |
| `Language` | `en` | `<html lang>` until the database is ready |
| `IncludeSiteStylesheet` | `true` | links `dyncms-site.css` |
| `Stylesheets`, `Scripts` | empty | added to `<head>` / after `blazor.web.js`, resolved through `Assets[]` (fingerprinted) |
| `AdditionalAssemblies` | empty | more routable components |
| `IncludeEntryAssembly` | `true` | scan the entry assembly for `@page` components |
| `SeedStarterSite` | `true` | registers `StarterSiteSeeder` as the *first* startup task |
| `StarterContent` | `EmptySite` | what an empty database gets when the setup page was *not* the one initialising it (`EmptySite` or `DemoBlog`); the setup page's own choice wins |
| `ServeWwwrootAtRuntime` | `true` | `UseStaticFiles()` + `UseRouting()` ahead of the setup gate |
| `UseHttpsRedirection`, `UseHsts`, `UseExceptionHandler` | `true` | HSTS and the exception handler only outside Development |
| `PluginServiceProvider` | `true` | wrap the container in `PluginServiceProviderFactory` so plugin services are injectable (§25); turn off with a third-party container |

`DynCmsHost.RunAsync(string[] args, Action<DynCmsHostOptions>? configureOptions = null, Action<IDynCmsBuilder>?
configureCms = null)` is the no-`Program.cs` entry point.

### `appsettings.json` (`src/DynCMS.Web`)

```json
{
  "DynCms": {
    "Identity": { "TokenEncryptionKey": "change-this-dyncms-token-key", "AdminUserName": "admin", "AdminPassword": "Admin123!" }
  }
}
```

Only the values a new site must change are spelled out; everything else (`MediaRootPath`, `MediaRequestPath`,
`MaxUploadBytes`, `TokenLifetimeDays`, `AllowMultipleSessions`, …) keeps its `DynCmsOptions` default.
`appsettings.Development.json` holds logging levels only.

---
## 17. Extension points

| Want to… | Do this |
|---|---|
| Add a code template | `.AddTemplate<TComponent>("alias", "Name")` (§9.1) |
| Add or replace a property editor | `.AddPropertyEditor(new PropertyEditorDefinition { … })` (§10); the same alias replaces a built-in |
| Run work once the database exists | `.AddStartupTask<TTask>()` with `IDynCmsStartupTask` (runs on every start and after `/setup`; make it idempotent) |
| Change paths, upload rules, identity, API settings | `AddDynCms(o => …)` or the `DynCms` configuration section (§16) |
| Change the public layout, stylesheets, scripts, pipeline switches | the `DynCmsHostOptions` delegate (§16) |
| Replace media storage | register `IMediaStorage` before `AddDynCmsHost` (§11) |
| Replace a Core service | register it before `AddDynCmsHost`; everything except the registries and the plain adds listed in §3 uses `TryAdd*`. `DatabaseSetupService` and `DatabaseMaintenanceService` depend on the concrete `DynCmsRuntime`, so replacing `IDynCmsRuntime` does not affect them |
| Pin the language for a scope | set `ICultureContext.Culture` (§7) |
| Render content anywhere | inject `IPublishedContentQuery`, or drop in `<CmsContentRenderer Path="…" />` (§8) |
| Render a stored template from code | `IStoredTemplateRenderer` / `ILiquidTemplateEngine` (§9.4, §24) |
| Get a media URL | `IMediaService.GetUrl(item)` or `<CmsImage MediaId="…" />` |
| Expose an operation to scripts and agents | add it to `CmsManagement`, then to the endpoints and the MCP tools (§13.4) |
| Add your own pages, APIs, static files | as in any ASP.NET Core app; literal routes beat the catch-all (§18) |
| Add back-office sections, navigation, APIs and data without redeploying | a plugin (§25): `DynCmsPlugin` with `@page` components, `MenuItems`, `ConfigureServices`, `MapEndpoints`, a private data folder |

What there is **no** hook for: events on save/publish, output caching, and per-entity permission checks inside the
Blazor back office.

---

## 18. Hosting DynCMS in your own application

Install `DynCMS.Host` (it brings `DynCMS.Core` and `DynCMS.UI` with it). Three levels of involvement:

**1. No code.** `Program.cs` in a `dotnet new web` project:

```csharp
await DynCMS.Host.DynCmsHost.RunAsync(args);
```

Delete the template's `app.MapGet("/", ...)`: an endpoint at `/` beats the catch-all page. The first start lands on
`/setup`; after that an empty database gets the starter site (§20).

**2. Your templates, seeding and options.** `AddDynCmsHost` returns the `IDynCmsBuilder`, so the whole §17
surface chains on:

```csharp
using DynCMS.Host;

var builder = WebApplication.CreateBuilder(args);

builder.AddDynCmsHost(o =>
    {
        o.Layout = typeof(MyLayout);                               // any LayoutComponentBase; default SiteLayout
        o.Stylesheets.Add("site.css");                             // resolved through Assets[], so wwwroot files are fingerprinted
        o.AdditionalAssemblies.Add(typeof(SomePlugin).Assembly);   // more routable components
        o.SeedStarterSite = false;                                 // you seed instead
    })
    .AddTemplate<HomeTemplate>("home", "Home page")
    .AddStartupTask<MySeeder>();

var app = builder.Build();
app.UseDynCmsHost();
app.Run();
```

`DynCmsHost.RunAsync(args, options, cms)` takes the same two delegates when you do not need the builder in your hands.

**3. Your own shell.** Keep the services and the pipeline, replace `App.razor` / `Routes.razor`:

```csharp
app.UseDynCmsHost<MyApp>();
```

Your `Routes.razor` must scan the DynCMS assemblies (the admin, the setup page and the catch-all content page live
there) and handle `NotAuthorized`:

```razor
@inject DynCmsHostOptions Options

<Router AppAssembly="typeof(MyApp).Assembly"
        AdditionalAssemblies="Options.GetRouterAssemblies(typeof(MyApp).Assembly)"
        NotFoundPage="typeof(DynCMS.Host.Components.Pages.NotFound)">
    <Found Context="routeData">
        <AuthorizeRouteView RouteData="routeData" DefaultLayout="typeof(MyLayout)">
            <NotAuthorized><RedirectToLogin /></NotAuthorized>
        </AuthorizeRouteView>
        <FocusOnNavigate RouteData="routeData" Selector="h1" />
    </Found>
</Router>
```

Your `App.razor` must link `DynCmsHost.ShellStylesheet`, `DynCmsHost.SiteStylesheet` (if you use the default
theme), `DynCmsUi.AdminStylesheet` and your app's `{ApplicationName}.styles.css` bundle, include `<ImportMap />`,
`<ReconnectModal />` and `blazor.web.js`, and choose the render mode per request with
`HttpContext.AcceptsInteractiveRouting() ? InteractiveServer : null` on both `<HeadOutlet>` and `<Routes>`. Start
from a copy of `src/DynCMS.Host/Components/App.razor`. Keep `DynCMS.Host` in the router assemblies unless you also
provide your own `/{*Path}`, `/Error` and `/not-found` pages.

**Your own static files, endpoints and pages.** They all coexist with the CMS; literal routes beat the `/{*Path}`
catch-all page.

- `wwwroot` files (PDFs, videos, downloads) are served by `MapStaticAssets()` *and*, because `ServeWwwrootAtRuntime`
  defaults to on, by the static file middleware ahead of routing. The second one is what serves files copied into
  `wwwroot` after the build and answers range requests correctly (`MapStaticAssets` sends the whole file with a `206`,
  [dotnet/aspnetcore#63320](https://github.com/dotnet/aspnetcore/issues/63320), which breaks `<video>` seeking).
  Files uploaded through the media library go through `MapDynCmsMedia()`, which handles ranges itself.
- Minimal APIs and controllers: `app.MapGet(...)` / `builder.Services.AddControllers()` + `app.MapControllers()`
  before or after `UseDynCmsHost()`, as usual. `[Authorize]` on them uses the DynCMS scheme (cookie or API key).
  Until the database is set up, non-navigation requests get a `503` from the setup gate. Remember that `AddDynCms`
  configured the app-wide minimal-API JSON options (nulls omitted, enums as camel-case strings).
- Your own routable components (`@page "/contact"`) are found because the entry assembly is in the router list;
  they render in `DynCmsHostOptions.Layout`. `dotnet new web` has no `_Imports.razor`, so add one with at least
  `@using Microsoft.AspNetCore.Components.Web` (for `PageTitle`, `NavLink`, …) and the `DynCMS.*` namespaces you use.
  A custom property editor also needs `@using DynCMS.UI.Admin.PropertyEditors`.

**Without the host package.** `AddDynCms(...)`, `AddDynCmsUI()`, `UseDynCmsSetup()`, `MapDynCmsMedia()`,
`MapDynCmsBackups()`, `MapDynCmsApi()`, `MapDynCmsMcp()` and `InitializeDynCmsAsync()` are public on Core and UI.
`UseDynCmsHost` in `src/DynCMS.Host/DynCmsHostExtensions.cs` is the reference for the order they go in.

**Decisions the package makes for you**

- Interactive server render mode, set globally in `App.razor`. Nothing here is WASM-safe, even though the UI and
  Host projects declare `SupportedPlatform browser`.
- `dyncms.database.json`, `App_Data/media` and `App_Data/backups` live in the content root and must be writable;
  `DynCms:BasePath` moves them.
- `BlazorDisableThrowNavigationException=true` and `RequiresAspNetWebAssets=true` in your project, from the package's
  `build/DynCMS.Host.props`. The second one matters when your project has no `.razor` file at all (`dotnet new web` plus
  the package): the Web SDK only ships `_framework/blazor.web.js` when it sees one, and without the script `/setup` and
  `/admin` render but nothing on them reacts.
- The starter-site seeder is the *first* startup task. If you register your own seeder and leave `SeedStarterSite`
  on, the starter runs first and your seeder sees a non-empty database; turn it off.
- Anything that must run once the database exists goes in an `IDynCmsStartupTask`, not after `Build()`. On a fresh
  install the database only appears when the setup page completes.
- The identity framework's Blazor UI statics (`IDFBlazorUIConfig.*`) are set to the `/admin` paths.

---

## 19. The back office: routes and building blocks

### Route map

`Admin/Pages/_Imports.razor` applies `AdminLayout` and `[Authorize]` to every page in that folder.

| Route | Page | Access |
|---|---|---|
| `/setup` | `Setup/DatabaseSetup.razor` (`BlankLayout`) | anonymous; bounced to `/` once configured |
| `/admin/login?returnUrl=` · `/admin/logout` | `Admin/Auth/*` (`BlankLayout`) | anonymous |
| `/admin` | `Dashboard` | authenticated |
| `/admin/content` · `/admin/content/{id:guid}?culture=` | `ContentSection`: tree and editor | authenticated |
| `/admin/media` | `MediaSection` | authenticated |
| `/admin/settings` · `/admin/settings/document-types[/new \| /{id:guid}]` | `SettingsSection`: document types | authenticated |
| `/admin/settings/templates[/new?role=partial \| /{id:guid} \| /component/{alias}]` | template editor | authenticated |
| `/admin/settings/languages` | languages: add, rename, default, mandatory, fallback, remove | authenticated |
| `/admin/settings/dictionary[/new?parent= \| /{id:guid}]` | dictionary | authenticated |
| `/admin/settings/editors` | property editor catalogue | authenticated |
| `/admin/settings/api` | API keys, connection snippets for curl and Claude Code | authenticated |
| `/admin/settings/plugins` | `PluginsPanel`: install, start, stop, reload, uninstall plugins | `Administrators` |
| `/admin/{**rest}` | `PluginPageHost`: mounts the `/admin/…` pages of running plugins, else a not-found panel | authenticated |
| `/admin/data[/{view}]` (`configuration`, `backups`, `analytics`, `analytics-backups`, `reset`) | Overview, primary configuration and backups, analytics database configuration and backups, reset | `Administrators` |
| `/admin/analytics[/{view}]` (`pages`, `visitors?path=&visitor=&session=&ip=&country=`, `map`, `sources`, `technology`, `settings`) | `AnalyticsSection` (§15) | authenticated; `settings` needs `Administrators` |
| `/admin/users` | the identity framework's `IdentityAdminPart` | `Administrators` |
| `/` · `/{*Path}?preview=true` | `CmsPage`: plugin site pages first, then the content tree | anonymous (preview needs a signed-in user) |
| `/Error` · `/not-found` | `Error`, `NotFound` | anonymous |
| `/media/{**path}` | media endpoint | anonymous |
| `/admin/data/backups/download/{name}` | backup download endpoint | `Administrators` |
| `/api/v1/**` · `/api/v1/openapi.json` | management REST API (§13) | API key or cookie; root and OpenAPI anonymous |
| `/mcp` | MCP server (§13.3) | API key or cookie |
| `/_content/{plugin}/{**path}` | static files of a running plugin (§25) | anonymous |
| whatever running plugins map | plugin endpoints and controllers (§25) | as the plugin decides |

`SettingsSection` hosts all of Settings on one page and derives its mode by matching `Nav.Uri`; it subscribes to
`LocationChanged`, `ITemplateRegistry.Changed` and `IPluginManager.Changed`. `AdminLayout` also subscribes to the
latter to show the top-bar links running plugins contribute.

### Building blocks

If you add a screen to `DynCMS.UI`, these are the pieces to build it from:

| Piece | Surface |
|---|---|
| `Modal` | `Title`, `ChildContent`, `Footer`, `OnClose`, `Size` (`Modal.ModalSize.Small` / `Medium` / `Large`). Backdrop click closes; no Escape handling, no focus trap |
| `ConfirmDialog` | `Title` ("Are you sure?"), `Message`, `ConfirmText` ("Confirm"), `Danger`, `OnConfirm`, `OnCancel` |
| `ToastService` (scoped) + `ToastHost` (in `AdminLayout`) | `Success` / `Info` / `Warning` / `Error(title, detail?)`, `Error(Exception, title?)`, `Dismiss(id)`; keeps five; auto-dismiss after 4 s (9 s for errors) |
| `Icon` | `Name` (default `document`), `Size` (18), `Class`. 82 inline SVG icons, names in `Icon.Names`; an unknown name silently renders `document` |
| `RedirectToLogin` | in `<NotAuthorized>`: force-loads the login page with `returnUrl`, or shows "Access denied" to a signed-in user without the role |
| `MediaBrowser` | `PickMode`, `ImagesOnly`, `InitialFolderId`, `OnPick`. In pick mode it hands the item to the caller and does not close itself; wrap it in a large `Modal` |
| `PropertyEditorBase` | §10 |
| `DataFormat` | `Bytes`, `Rows`, `Local`, `Ago` |
| `dyncms-admin.js` | `rte.*` (rich text), `copyText(text)`, `focusElement(element)`; import as `./_content/DynCMS.UI/dyncms-admin.js` |
| `dyncms-admin.css` | every class is prefixed `dc-`; design tokens are CSS custom properties (`--dc-primary`, `--dc-surface`, `--dc-radius`, `--dc-shadow-*`, …). There is no CSS isolation in `DynCMS.UI` |

`AdminLayout` is a top bar with hard-coded `NavLink`s (Dashboard, Content, Media, Settings; Users and Data inside
`<AuthorizeView Roles="Administrators">`), a "View site" link, a user card (initials, name, "Administrator" or
"Editor" by role) and the logout link.

### How the main editors work

**ContentEditor** (`Id`, `Culture`, `OnChanged`, `OnDeleted`; hosted by `ContentSection` with `@key`): loads the node
with `GetAsync` (so the type's properties are present), the languages, and resolves the culture (`?culture=` → the
selector → the default). Tabs come from `ContentType.Groups` plus an Info tab. Each property row renders the editor
through `DynamicComponent` with `Value = node.GetValue(alias, culture)`, keyed by property id and culture so varying
editors remount on a language switch. Edits call `SetValue(alias, value, culture)` and mark the editor dirty; the
Info tab edits the URL segment (slugged on change), the template (from `Templates.Pages`, filtered by the type's
allowed list) and the sort order (Move up/down call `MoveAsync` immediately). *Save* → `SaveAsync`. *Save & publish*
→ save, then either the `PublishDialog` (varying type with several languages) or `PublishAsync` directly; validation
errors are split into the current language (shown under the fields) and other languages (an alert with buttons that
switch to them). *Unpublish* → `UnpublishAsync(id, culture)`, re-applying unsaved edits onto the returned node.
*Delete* → `ConfirmDialog` → `DeleteAsync`. There are no keyboard shortcuts here.

**PublishDialog** (`Node`, `Languages`, `Current`, `OnPublish`, `OnCancel`): one checkbox per language; disabled
when the language has no name yet, or locked on when it is mandatory and not yet published; the current language
and every locked one are pre-selected.

**CreateContentDialog** (`ParentId`, `OnCreated`, `OnCancel`): loads `GetAllowedChildTypesAsync(parentId)`; zero
types → an explanation with a link to document types; one → pre-selected; several → a card grid. Enter creates.

**DocumentTypeEditor** (`Id?`, `OnSaved`, `OnDeleted`): tabs *Design* (groups with rename/remove, property rows
with move/edit/remove, "Add group"), *Structure* (allow as root, allow varying by language, allowed child types
including itself), *Templates* (checkboxes over `Templates.Pages`; the first ticked becomes the default), *Info*
(alias, description, ids, dates). The name auto-fills the alias with `Slug.ToAlias` until the alias is edited by
hand; a hand-edited alias is stored as typed. Removing a property is immediate, without a confirm.
**PropertyDialog** edits a clone of a `PropertyType` (name, alias, description, group, editor, mandatory, vary by
language, and the editor's `ConfigFields`); `ApplyProperty` copies it back and rejects duplicate aliases.

**ContentTree** loads the whole tree once per `Version` (`GetTreeAsync`), expands ancestors of the selected node,
filters to a flat list of up to 60 name matches, and shows a language badge (count of existing cultures), a muted
dot for unpublished and a warning dot for pending changes.

**Keyboard shortcuts** exist in the template editor (`Ctrl+S`, `Ctrl+Space`, `Ctrl+/`) and the dictionary item
editor (`Ctrl+S`); Enter creates content, folders, languages and tags.

---

## 20. The starter site and the archived demo

The demo site lived in `src/DynCMS.Web` until 2026-09-26, when that project was reduced to the minimal host project
(§2). Its files were moved unchanged to `_archive/DynCMS.Web-demo/` (own `README.md` with what it showed and where
to look). Nothing in the archive is built or referenced by the solution; the inventory below is kept because
`DemoSeeder` is still the most complete example of seeding a site in code.

### What `StarterSiteSeeder` creates (host package, when `SeedStarterSite` is on and there are no document types)

The setup page asks for the **starting content** (`DatabaseSetupRequest.StarterContent`, enum
`DynCMS.Core.Services.StarterContent`: `EmptySite` | `DemoBlog`). `DatabaseSetupService.CompleteSetupAsync` (and
`DatabaseMaintenanceService.SwitchConfigurationAsync`, the Data tab) puts the answer into the singleton
`StartupContext.RequestedStarterContent` for the duration of the initialisation and clears it afterwards; the seeder
reads it and falls back to `DynCmsHostOptions.StarterContent` when it is null (an ordinary start on an existing
configuration file whose database happens to be empty). Both choices create:

- Templates: partials `card` and `navigation`; pages `page` (the page starter), `sectionPage` (the listing starter)
  and `home` (hero with optional image + body + children-as-cards; the demo variant adds featured and latest articles).
- Document type `page` (children `page`; templates `page`, `sectionPage`): `summary`, `bodyText` (mandatory rich
  text), `image` (images only), `intro` (group Section), `metaDescription` (group SEO).
- Document type `home` (root; children `page`; templates `home`, `page`): `heroTitle`, `heroText`, `heroImage`,
  `bodyText`, `metaDescription`.

**Empty website**: a single published root "My site". Nothing else.

**Demo blog** (`DemoBlogSeeder`, `DemoBlogTemplates`), on top of that:

| | |
|---|---|
| Document types | `article` (`summary`*, `bodyText`*, `image`, `publishDate`* date, `author`, `category` dropdown News/Tutorial/Release notes, `tags`, `featured` toggle), `blog` (`intro`, `pageSize` numeric 1–50; children `article`); `home` gets `blog` as an allowed child |
| Templates | partial `articleCard`; pages `blog` (newest first, `?category=` filter with `where`, `paginate: page_size` where `page_size = content.pageSize \| default: 6 \| plus: 0`, dictionary labels with fallbacks) and `article` (meta line, tags, three more articles from `content.siblings`) |
| Dictionary | folder `paging` with `paging.previous`, `paging.next`, `paging.pageOf`, `paging.summary` in the default language |
| Media | `hero.svg` at the root; folder "Blog" with seven generated gradient SVGs |
| Content | root **DynCMS Demo** (hero image, links to the blog) → **About** → **How it was built**; **Blog** (`pageSize` 6) with 14 published articles dated 2–60 days back (three pages) and one draft that only shows in preview |

The demo is a plain `IDynCmsStartupTask` companion (scoped, registered by `AddDynCmsHost` next to the seeder),
so it is also the shortest example of seeding a site in code: types, templates, media, dictionary, content, publish.

### What `DemoSeeder` created (`_archive/DynCMS.Web-demo/Seed/DemoSeeder.cs`)

It ran on every start. It returns early when `Demo:Seed` is false. The `editor` user is ensured **every** start
(independently of content); everything else only when there are zero document types. It ignores every
`PublishResult`, so a validation failure would seed drafts silently.

| | |
|---|---|
| Users | `editor` (Erin Editor, `Editors`), password `Demo:EditorPassword` |
| Languages | the default (`en`, made mandatory), `de` "Deutsch" and `fr` "Français", both falling back to `en` |
| Dictionary | folders `blog`, `page`, `paging`; keys `blog.featuredArticles`, `blog.allArticles`, `blog.noArticles`, `blog.by`, `page.inThisSection`, `page.otherLanguages`, `paging.previous`, `paging.next`, `paging.pages`, `paging.pageOf` ("Page {page} of {pages}"), `paging.summary` ("Showing {from}–{to} of {total} articles"), each in three languages |
| Media | `hero-gradient.svg` at the root; folder "Articles" with seven generated gradient SVGs |
| Document types | `homePage` (root, varies; hero group with `heroTitle`*†, `heroText`†, `heroImage`, `ctaLabel`†, `ctaLink`; `bodyText`†, `accentColor`; `metaDescription`†), `textPage` (varies; `subtitle`†, `bodyText`*†, `image`, `metaDescription`†; templates `textPage`, `translatedPage`, `pagingPage`, `simplePage`, `sectionPage`), `articleList` (`intro`, `pageSize` numeric 1–50), `article` (`summary`*, `bodyText`*, `image`, `publishDate`*, `author`, `category` dropdown, `tags`, `featured` toggle). `*` mandatory, `†` varies by language |
| Component templates | `home` → `HomeTemplate`, `textPage` → `TextPageTemplate`, `articleList` → `ArticleListTemplate`, `article` → `ArticleTemplate` (`Program.cs`) |
| Stored templates | partials `card`, `navigation`; pages `simplePage`, `sectionPage`, `translatedPage` (`DemoTemplates.TranslatedPage`), `pagingPage` (`DemoTemplates.PagingPage`) |
| Content | root **DynCMS Demo** (three languages) → **About** (`/about`, `/de/ueber-uns`, `/fr/a-propos`) → **Team** (English only); **Blog** (invariant) with 14 published articles and one draft; **Languages** (renders with `translatedPage`; subtitle in English only, to show fallback); **Paging** (renders with `pagingPage`) |

Helper components in `_archive/DynCMS.Web-demo/Components/Templates/`: `ArticleCard`, `Breadcrumbs`, `PageLanguages` (per-page
language switcher built on `GetCultureLinksAsync`, with `hreflang` alternates in `<HeadContent>`; it injects
`IDictionaryService` directly because it is not a template), and `DemoText` (`CultureOf`, `LongDate`, `ShortDate`).
`ArticleListTemplate` is the paging example: `[SupplyParameterFromQuery(Name = "page")]`, `pageSize` from the
content (clamped 1–50), articles ordered by `publishDate` desc, `CmsPager` with dictionary labels, a page number in
the title and `rel="prev"/"next"` links. The archive's README walks through what the demo showed and where to look.

---
## 21. Conventions

**C#**

- File-scoped namespaces, primary constructors, collection expressions (`[]`), `required`/`init` on models.
- Interface and implementation share a file for small services; `IPublishedContentQuery`, `IContentService`,
  `ITemplateService` and the other larger ones have their own interface file.
- Async everywhere; `CancellationToken ct = default` is the last parameter.
- Return `IReadOnlyList<T>`, never `List<T>`, from services.
- Alias comparisons are `StringComparer.OrdinalIgnoreCase`; alias dictionaries are built with it. Group names are
  compared ordinally in places, so keep their casing consistent.
- User-facing failures are `InvalidOperationException` with a sentence an editor can act on: "'Article' is used by
  12 content item(s). Delete that content first." They are caught in the UI and shown as toasts, and translated to
  `400`/`404`/`409` by the API.
- XML doc comments on public types explain *why*, not *what*.
- One `DbContext` per operation (§5).

**Razor**

- Admin CSS classes are prefixed `dc-`, all in `dyncms-admin.css`. There is no CSS isolation in `DynCMS.UI`; the
  static asset base path is `_content/DynCMS.UI`.
- Icons: `<Icon Name="document" Size="18" />`; valid names come from `Icon.Names`.
- Notifications: inject the scoped `ToastService` and call `Success` / `Info` / `Warning` / `Error`.
- Components that load data guard `OnParametersSetAsync` with a cache key so they do not re-query on every render:
  see `CmsContentRenderer._resolvedKey`, `CmsLiquidTemplate._renderedKey`, `CmsImage._loadedFor`.
- Text inputs under interactive server rendering: use `@bind` with `@bind:event="oninput"` (and `@bind:after` when
  needed) rather than `value="@x" @oninput=...`, which drops keystrokes when typing fast.
- Public-site classes come from `src/DynCMS.Host/wwwroot/dyncms-site.css`: `site`, `site-container`, `site-header`,
  `site-nav`, `site-languages`, `site-main`, `site-footer`, `btn`, `btn-primary`, `hero`, `hero-grid`, `eyebrow`,
  `lead`, `hero-image`, `section-head`, `cards`, `card`, `card-image`, `card-body`, `page`, `page-head`, `page-image`,
  `prose`, `subpages`, `breadcrumbs`, `article-meta`, `tags`, `tag`, `pager` (with `active`, `disabled`, `pager-gap`),
  `pager-summary`, `muted`, `site-notfound`, `site-error`. The Liquid starters, the starter site and the archived demo
  templates use them, so keep them or adjust the starters.

**Naming**

- Document type, property and template aliases: camelCase, produced by `Slug.ToAlias` when derived from a name.
- URL segments: kebab-case, produced by `Slug.ToUrlSegment`.
- Property editor aliases are namespaced (`DynCms.TextBox`); use your own prefix for custom ones.
- API scopes: `entity:action` in kebab-case (`document-types:write`).

---

## 22. Constraints and gotchas

**Schema**

- No EF migrations. `DatabaseSchema` only *adds* tables and columns. Renaming or retyping anything on an existing
  database is on you; move to migrations before you evolve the model in production.
- Content and identity tables share one database on purpose. That is why `EnsureCreated` is never used.
- `DatabaseSchema` relies on an EF internal API (`IMigrationsModelDiffer`, `EF1001` suppressed). An EF Core major
  upgrade is the thing most likely to break it.
- Several uniqueness checks are SQL `==` (case-sensitive on SQLite) while the in-memory dictionaries are
  case-insensitive: document type aliases, template aliases, published-content type lookup.

**Hosting**

- `_framework/blazor.web.js` is a static web asset the Web SDK adds only when the project contains a `.razor` content item
  (`Microsoft.NET.Sdk.Web.ProjectSystem.targets` derives `RequiresAspNetWebAssets` from it). A host project that has
  deleted every component gets a shell whose `<script>` answers 503 in setup mode (the request falls through to the
  catch-all page) and 404 afterwards, with no server-side error. `build/DynCMS.Host.props` forces the property to `true`;
  keep the import (or a `.razor` file) in place. Found 2026-09-26 when `src/DynCMS.Web` was reduced to the minimal project.

**Scale-out**

- `ITemplateRegistry`, `IPropertyEditorRegistry`, `DictionaryCache`, `DatabaseConfigurationStore`, the API-key
  validation cache and the Fluid template cache are **per process**. Behind a load balancer, a template saved on
  node A is not live on node B until node B re-initialises. Stay on a single instance, or add your own invalidation,
  before scaling out.
- The identity framework's `IDFManager` is static and process-wide, which is why `CmsIdentity` is a singleton with
  `Reset()` and `RefreshCache()` hooks for database switches and restores.
- `ReleaseConnections()` clears **all** SQLite and SQL Server connection pools in the process, including the host's
  own.

**Queries**

- Property values are JSON columns: you cannot filter on them in SQL.
- Every `IPublishedContentQuery` call re-reads the `Languages` table. URL building is two queries per node;
  `GetByContentTypeAsync` and `GetAncestorsAsync` issue them per result; `GetByRouteAsync` loads all children (with
  their types) at each level of the path. Fine for hundreds of pages, not for tens of thousands.
- `SearchAsync` is a `LIKE` over name and URL segment, then an in-memory pass over every varying node's per-language
  names when it has not filled `take`. No property or full-text search.
- There is no output cache. Every request re-queries and re-renders.

**Content**

- `ContentNode.Level` is **0 at the root**.
- Deleting a node deletes its whole subtree, immediately and permanently. Nothing checks content-picker references.
- Publishing a node does **not** publish its children. An unpublished ancestor does not hide its published children
  from `GetChildrenAsync`/`GetByContentTypeAsync`, but their URLs will not route.
- `MoveAsync` only reorders siblings. There is no API to move a node to a different parent.
- Renaming keeps the URL segment; changing the segment changes the URL, and there are no redirects.
- A URL segment equal to a language code or its primary subtag (`en`, `de`, …) is unreachable, because the first path
  segment is tested as a language prefix first.
- Content fallback stops at the end of the language's fallback chain; only the dictionary falls through to the
  default language.
- Media files live outside the database: a database backup does **not** include `App_Data/media`.

**Hosting**

- Blazor **interactive server** only; the render mode is set globally in `src/DynCMS.Host/Components/App.razor`.
- `App.razor` keeps the `[ExcludeFromInteractiveRouting]` → static SSR switch although no DynCMS page uses the
  attribute: the login page runs interactively and writes the cookie through the identity framework's JS-based
  cookie service, then force-reloads so the server handler sees it. The switch is there for a host's own static-SSR
  pages. (The comment in `build/DynCMS.Host.props` still says the login pages render statically; the code is right.)
- A configuration file that exists but points at an unreachable database makes the host **fail to start**; only a
  missing file gives you the setup page.
- CMouss.IdentityFramework targets .NET 8 / EF Core 9 and runs here on .NET 10 / EF Core 10 through binary
  compatibility.
- `/admin/users` loads Bootstrap from a CDN and both admin layouts load Inter from Google Fonts; an offline
  deployment needs to vendor those.

---

## 23. Known issues

Things the audit of 2026-09-25 found in the code. They are listed here so nobody documents them as behaviour; fix
them at the source when you touch the area.

1. **Vary-by-culture flags cannot be changed after creation.** `ContentTypeService.SaveAsync`'s update branch copies
   every field of the type and its properties except `VariesByCulture`. Toggling *Allow varying by language* or
   *Vary by language* on an existing type, in the back office or through `PUT /document-types/{id}`, is silently
   discarded. Fix: copy the two flags in the update branch (and decide what to do with existing nodes' `Cultures`
   when a type starts varying).
2. **Template alias case.** `TemplateService.SaveAsync` checks alias uniqueness with a case-sensitive SQL `==`, but
   `TemplateRegistry.SetStored` builds a case-insensitive dictionary that throws on duplicates. Saving `Card` next to
   `card` would persist the row and then fail on the registry refresh.
3. **A committed API key.** `mcp.md` at the repository root contains an API key secret (markdown-escaped). Treat it
   as leaked: revoke it under Settings → API & AI agents and delete the file. This is also the likely reason a
   configured MCP client gets a `401` from `/mcp`.
4. **`Api.MaxRequestBodyBytes` is dead.** Nothing reads it; base64 uploads are capped by `MaxUploadBytes` and
   multipart by Kestrel.
5. **Disabled owners keep working for up to 60 seconds.** The API-key validation cache re-checks key expiry but not
   the owner's active/locked state within its lifetime.
6. **`Identity.LoginPath` is only half honoured.** The server-side challenge uses it; `RedirectToLogin`, `Logout`
   and the identity UI statics use the constant `/admin/login`.
7. **Deleting a language can leave `IsPublished` true on nodes with an empty culture map**, which are then invisible
   in every language.
8. **`ContentNode.SetValue(alias, value, null)`** writes a varying property into the shared dictionary without
   complaint, while the two-argument overload throws.
9. **`TextArea`'s `placeholder`** is read by the editor but not declared in its `ConfigFields`, so the dialog never
   offers it.
10. **The archived demo's seeded article** about the API says "Settings → API keys"; the page is "API & AI agents".
11. **`SqlServer` connection strings** require a port; named instances are not supported.

---

## 24. Recipes

### Add a component template

```razor
@* 1. src/DynCMS.Web/Components/Templates/LandingTemplate.razor *@
@inherits CmsTemplateBase
@inject IPublishedContentQuery Query

<h1>@Content.Name</h1>
@Content.Html("bodyText")
<h2>@T("landing.more", "More")</h2>
<ul>
    @foreach (var child in _children) { <li><a href="@child.Url">@child.Name</a></li> }
</ul>

@code {
    private IReadOnlyList<PublishedContent> _children = [];

    protected override async Task OnParametersSetAsync() =>
        _children = await Query.GetChildrenAsync(Content.Id, Content.IsPreview);   // pass IsPreview through
}
```

```csharp
// 2. Program.cs
.AddTemplate<LandingTemplate>("landing", "Landing page")
```

3. In `Settings → Document types`, add `landing` to the type's **Templates** tab and make it the default.

### Turn a component template into an editable one

`Settings → Templates → <the component> → Create an editable version`. DynCMS writes a stored template with the
same alias, scaffolded from the document type that uses it. Edit and save; it is live. Delete it to fall back to the
component.

### Add a custom property editor

```razor
@* MySite/Components/Editors/MarkdownEditor.razor *@
@using DynCMS.UI.Admin.PropertyEditors
@inherits PropertyEditorBase

<textarea id="@InputId" class="@InputClass" rows="@(ConfigInt("rows") ?? 10)"
          value="@Value" @onchange="e => SetValueAsync(e.Value?.ToString())"></textarea>
```

```csharp
// Program.cs — chained on AddDynCmsHost (or AddDynCms(...).AddDynCmsUI() without the host package)
builder.AddDynCmsHost()
    .AddPropertyEditor(new PropertyEditorDefinition
    {
        Alias = "MyCompany.Markdown",
        Name  = "Markdown",
        Icon  = "edit",
        Description = "Markdown source.",
        ComponentType = typeof(MarkdownEditor),
        ConfigFields = [new("rows", "Rows", Type: ConfigFieldType.Number)]
    });
```

It appears in the document-type editor's property dialog straight away. Store a plain string; if you store JSON,
`ContentValueConverter` and the Liquid layer parse it into an array or hash automatically. Reusing a built-in alias
replaces that editor.

### Seed content in code

```csharp
public sealed class MySeeder(IContentTypeService types, IContentService content) : IDynCmsStartupTask
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        if ((await types.GetAllAsync(ct)).Count > 0) return;      // idempotent: this runs on every start

        var home = await types.SaveAsync(new ContentType
        {
            Alias = "homePage", Name = "Home page", AllowedAsRoot = true,
            AllowedTemplateAliases = ["home"], DefaultTemplateAlias = "home",
            Properties =
            [
                new PropertyType
                {
                    Alias = "bodyText", Name = "Body text",
                    EditorAlias = PropertyEditorAliases.RichText, GroupName = "Content"
                }
            ]
        }, ct);

        var node = await content.CreateAsync(home.Id, null, "Home", ct: ct);   // 4th positional parameter is the culture
        node.SetValue("bodyText", "<p>Hello.</p>");
        await content.SaveAsync(node, ct);

        var result = await content.PublishAsync(node.Id, ct: ct);              // 2nd positional parameter is the culture list
        if (!result.Success) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Message)));
    }
}

// Program.cs
.AddStartupTask<MySeeder>();
```

`DemoSeeder` (`_archive/DynCMS.Web-demo/Seed/DemoSeeder.cs`) is the full worked example: a user, languages, dictionary, media,
four document types, stored templates and a published tree in three languages (`SetName(culture, …)`,
`SetValue(alias, value, culture)`, `PublishAsync(id)` for every named language).

### Query content from your own code

```csharp
app.MapGet("/sitemap.txt", async (IPublishedContentQuery query) =>
{
    var root = await query.GetRootAsync();
    if (root is null) return Results.NotFound();

    var children = await query.GetChildrenAsync(root.Id);
    return Results.Text(string.Join('\n', new[] { root.Url }.Concat(children.Select(c => c.Url))));
});
```

`IPublishedContentQuery` is scoped and safe to inject into components, minimal API endpoints and hosted scopes
alike; outside a request or circuit it answers in the default language.

### Render Liquid yourself

```csharp
public class Renderer(IStoredTemplateRenderer renderer, IPublishedContentQuery query)
{
    public async Task<string?> RenderAsync(string route, string alias)
    {
        var content = await query.GetByRouteAsync(route);
        return content is null ? null : await renderer.RenderAsync(alias, content, LiquidRequest.ForPath(route));
    }
}
```

For source that is not stored (the back office's own preview path) use `ILiquidTemplateEngine.RenderSourceAsync`:

```csharp
var scope = new LiquidRenderScope(query, media, dictionary, preview: true, LiquidRequest.ForPath(content.Url));
var html  = await engine.RenderSourceAsync("scratch", source, content, scope);
```

### Pin the language for a site (host-per-language instead of a path prefix)

`ICultureContext` is scoped, and a Blazor circuit has its own scope, so set it where both prerendering and the
circuit run: your layout.

```razor
@* MyLayout.razor *@
@inherits LayoutComponentBase
@inject ICultureContext CultureContext
@inject NavigationManager Nav

@Body

@code {
    protected override void OnInitialized()
    {
        if (new Uri(Nav.Uri).Host.EndsWith(".de"))
            CultureContext.Culture = "de";
    }
}
```

Every `IPublishedContentQuery` and dictionary call made from components under that layout then answers in German
without a `/de/` prefix. Routing still honours a prefix when one is present, and the URLs the query builds keep the
prefix for non-default languages.

### Call the API from a script

```bash
KEY=dcms_…
curl -s -H "Authorization: Bearer $KEY" https://your-site/api/v1/ | jq .caller
curl -s -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
     -d '{"contentType":"article","parentId":"<blog id>","name":"Hello","values":{"summary":"Hi","bodyText":"<p>Hi</p>","publishDate":"2026-09-25"},"publish":true}' \
     https://your-site/api/v1/content
```

---

## 25. Plugins

Added 2026-09-27. A plugin is a Razor class library that is loaded into the running site from
`App_Data/plugins/{id}/` (configurable, §16) and can be started, stopped, reloaded, installed and uninstalled without a
restart. The plugin contract (`IDynCmsPlugin`, `DynCmsPlugin`, `IPluginContext`, `PluginMenuItem`, `CmsRoles`) lives in
`DynCMS.Plugins.Sdk`; the machinery that loads and runs plugins lives in `DynCMS.Core/Plugins/`; the back office page is `DynCMS.UI/Admin/Components/PluginsPanel.razor`;
the sample is `samples/DynCMS.Plugin.Guestbook`.

### 25.1 Writing a plugin

1. `dotnet new razorclasslib -n My.Plugin` (net10.0) and reference **only** `DynCMS.Plugins.Sdk`
   (`PackageReference` outside this repository; inside it a `ProjectReference` with `Private="false"` plus an `Import` of
   `src/DynCMS.Plugins.Sdk/build/DynCMS.Plugins.Sdk.props` and `.targets`, which NuGet does by itself for the package).
   Do not reference `DynCMS.Core` or `DynCMS.UI`: they are the site's internals, not a supported surface. The SDK's MSBuild
   files set what a plugin needs (`CopyLocalLockFileAssemblies=false`, the ASP.NET Core framework reference, the
   `/_content/{id}` static asset path, global C# usings for `DynCMS.Plugins`, `.Components` and `.Services`) and add the
   `PackDynCmsPlugin` target (25.2). Anything else the plugin uses (Entity Framework Core, say) is an ordinary
   `PackageReference` at the version the site ships; it is not copied into the package because the site provides it.
   Razor does not pick the global usings up for component tags: put `@using DynCMS.Plugins`,
   `@using DynCMS.Plugins.Components` and `@using DynCMS.Plugins.Services` into the plugin's `_Imports.razor`, or
   `<Icon>` renders as an unknown HTML element and shows nothing (no build error).
   The SDK exposes, besides the contract: `Icon` (the back office icon set), `ToastService` and `CmsRoles`. Plugin pages
   get their `dc-*` CSS classes from the site's back office stylesheet.
2. Add **one** public class deriving from `DynCmsPlugin` (or implementing `IDynCmsPlugin`) with a parameterless
   constructor. Metadata (`Name`, `Description`, `Version`, `Author`) defaults to the assembly attributes
   (`AssemblyTitle`, `Description`, `InformationalVersion`, `Company` in the project file); `Id` defaults to the assembly
   name and must stay URL-safe (`[A-Za-z0-9][A-Za-z0-9._-]*`).
   **Declare the oldest DynCMS you support** with `[assembly: DynCmsMinimumVersion("0.1.0")]` (or override
   `MinimumCmsVersion`). It is required: on install, start and rescan the plugin manager compares it with
   `CmsVersion.Application`; a plugin that needs a newer DynCMS, or declares nothing, is not attached (an upload is
   rejected with "requires DynCMS x.y.z or newer ... Update DynCMS first"; a plugin found on disk shows as *Failed*
   with the same message). The comparison ignores `-prerelease` suffixes. The back office *System* tab
   (`/admin/system`, `GET /api/v1/system/version`, MCP `system_version`) shows the application version, the database
   schema version (`CmsVersion.DatabaseSchema`, recorded in the Settings table under `system.version` at startup;
   bump it when a release changes the database incompatibly) and each plugin's compatibility.
3. Override what you need:

| Member | Purpose |
|---|---|
| `ConfigureServices(IServiceCollection, IPluginContext)` | the plugin's services (see 25.3) |
| `MapEndpoints(IEndpointRouteBuilder, IPluginContext)` | minimal-API endpoints; they exist while the plugin runs |
| `StartAsync(IPluginContext, ct)` | after the services exist: create/migrate the database, warm caches. Throwing marks the plugin *failed* |
| `StopAsync(IPluginContext, ct)` | before the services are disposed |
| `MenuItems` | `PluginMenuItem(Title, Url, Icon, Placement, Roles, Order)`: top bar (`Main`) or the Settings tree (`Settings`); `Roles` is a comma-separated role list, null = every signed-in user |
| `Icon` | a name from `Icon.razor`'s set |

4. Add pages as ordinary components with `@page`. A template that starts with `/admin/` is a **back-office page**:
   it renders inside `AdminLayout` for signed-in users (any role; check roles yourself with `AuthorizeView` when needed).
   Any other template is a **site page** rendered inside the host's site layout. Route parameters and constraints
   (`{id:int}`, `{slug}`, `{**rest}`), `[SupplyParameterFromQuery]`, `@layout` (nested inside the host layout),
   `PageTitle` and `HeadContent` all work. Use the `dc-*` classes and `DynCMS.UI.Admin.Components` (`Icon`, `Modal`,
   `ConfirmDialog`, `ToastService`) in admin pages so they look native.
5. Optional: a `wwwroot` folder (served at `/_content/{id}/…`, the RCL convention, so `<link href="_content/My.Plugin/x.css">`
   works), `[ApiController]` classes, `IHostedService` registrations (started and stopped with the plugin), a
   `settings.json` next to the binaries (see `IPluginContext.Configuration`).

`IPluginContext` gives the plugin: `PluginId`, `PluginDirectory`, `DataDirectory` (created, kept across updates),
`GetDataPath(relative)`, `SqliteConnectionString(file)`, `StaticAssetsRequestPath`, `Configuration` (the host's
`Plugins:{id}` section overlaid with `settings.json`, reloaded on change), `Services` (the application's root provider
including the plugin's own services once started; create a scope for scoped ones), `Logger` (category
`DynCMS.Plugins.{id}`) and `Environment`.

### 25.2 Packaging, installing, lifecycle

The plugin folder layout:

```
App_Data/plugins/
  plugins.json            { "<id>": { "enabled": true, "installedAt": "…" } }
  .shadow/, .staging/     working folders, cleaned on start
  <id>/
    bin/                  the assemblies (a bare <id>/*.dll drop-in works too)
    wwwroot/              optional static files
    settings.json         optional configuration
    data/                 the plugin's private files (IPluginContext.DataDirectory)
```

A **package** is a `.zip` with `bin/` (or the dlls at the root) and an optional `wwwroot/`, or a single `.dll`. The
SDK's `PackDynCmsPlugin` target (build/DynCMS.Plugins.Sdk.targets, imported by every plugin project) builds `bin/<Configuration>/<name>.plugin.zip` after every build and, with
`-p:DynCmsPluginDeployDir=<site>/App_Data/plugins`, copies the layout straight into a site (then click *Reload*).

Install by uploading on **Settings → Plugins** (`DynCms:Plugins:AllowUpload`, administrators), through
`POST /api/v1/plugins/upload` (multipart, `plugins:manage`), or by copying the folder and clicking *Rescan folder* /
restarting. Uploading a plugin whose id already exists replaces its binaries and keeps `data/` and `settings.json`.

States and operations (`IPluginManager`; REST under `/api/v1/plugins`; MCP `list_plugins`, `start_plugin`,
`stop_plugin`, `reload_plugin`, `uninstall_plugin`):

| Operation | Effect |
|---|---|
| **Start** | loads the assemblies if needed, builds the service container, starts hosted services, `StartAsync`, discovers pages, maps endpoints, registers controllers, publishes the menu items; `enabled: true` |
| **Stop** | removes pages, endpoints, controllers and menu items, `StopAsync`, stops hosted services, disposes the services; assemblies stay loaded; `enabled: false` (does not start on the next boot) |
| **Reload** | stop + unload + load from disk + start; for a new build copied into the folder |
| **Uninstall** | stop, unload, delete `bin/` and `wwwroot/`; `data/` is kept unless *also delete data* is ticked (`?deleteData=true`) |
| **Failed** | loading or `StartAsync` threw; the message is shown on the card and in the API (`error`) |

On boot, `DynCmsRuntime` starts every enabled plugin right after the database is ready (so plugins may use CMS services
while starting); nothing loads in setup mode. Changes are recorded in `plugins.json`. `IPluginManager.Changed` and
`IPluginRouter.Changed` fire after every change; `AdminLayout`, `SettingsSection`, `PluginsPanel` and every mounted
plugin page subscribe, which is why stopping a plugin turns an open plugin page into the not-found view at once.

### 25.3 How it works

**Loading** (`PluginLoadContext`, `PluginManager.Load`). The binaries are shadow-copied to `.shadow/<id>-<stamp>/`
and loaded from there in a collectible `AssemblyLoadContext`, so the files in `bin/` stay replaceable on Windows. The
context loads *only* assemblies the application does not have: anything on the trusted platform assembly list or
already in the default context (the framework, DynCMS, EF Core, everything the site references) is shared, which is
what makes `ComponentBase`, `IContentService`, `DbContext` and friends the same types inside the plugin. Extra
dependencies of the plugin are loaded from its folder (`AssemblyDependencyResolver`, then directory probing). Unloading
is best effort: Blazor's component factory, MVC's descriptor caches and reflection all keep types alive, so a reloaded
plugin's old context usually lingers until the process ends. The shadow folder is removed when possible and otherwise
on the next start. Memory grows a little per reload; that is a development convenience, not a production loop.

**Services** (`PluginServiceProvider.cs`). The application container is immutable once built, so
`AddDynCmsHost` installs `PluginServiceProviderFactory` (`builder.ConfigureContainer`, switch
`DynCmsHostOptions.PluginServiceProvider`). It builds the container as usual and wraps it: `GetService(T)` asks the
application first and, when that has no answer, the running plugins' containers (`PluginContainer`, one per running
plugin, built from the `IServiceCollection` the plugin filled). A type defined in a plugin assembly, or generic over one
(`IOptions<MyOptions>`, `IEnumerable<IMyThing>`, `ILogger<MyService>`), is answered by the plugin first, so a plugin's
`Configure<T>()`, `AddDbContextFactory<T>()` and `AddLogging()` behave as in a normal app. `IEnumerable<T>` merges both
sides. Lifetimes are honoured: singletons live in the plugin container (disposed when it stops), scoped instances in
the wrapper scope (disposed with it), transients are tracked like the built-in container does; scoped-from-root throws
when `ValidateScopes` is on. Implementation types are constructed with `ActivatorUtilities` against the wrapper, so a
plugin service's constructor may take host and plugin services alike. Keyed services inside plugins are not supported.

Three scopes matter and all three had to be pointed at the wrapper: (1) the root, which `IHost.Services`,
`app.Services` and `IPluginContext.Services` are; (2) request scopes, created by `DefaultHttpContextFactory`, which
the factory re-registers so it is constructed against the wrapper (that is what lets minimal-API parameters and
controller constructors receive plugin services; `IServiceProviderIsService` is also answered by the wrapper so the
parameter is bound as a service, not as a body); (3) Blazor circuit scopes, created by the internal
`Microsoft.AspNetCore.Components.Server.Circuits.CircuitFactory`, which the factory re-registers the same way by
reflection. Should a future ASP.NET Core rename that type, `PluginContainerRegistry.CircuitScopesRedirected` becomes
false, `@inject` of plugin services in interactive components stops working, and plugins fall back to
`IPluginContext.Services`; everything else keeps working. Static prerendering renders with the request scope's *inner*
provider (the `EndpointHtmlRenderer` is built by the application container), so `PluginPageOutlet` mounts the plugin
component only once the circuit is interactive (`OnAfterRender`, never called during prerendering) and shows a spinner
until then; plugin pages therefore are not prerendered and are not visible without JavaScript.

**Pages** (`PluginRouter`, `PluginPageOutlet`, `PluginPageHost`, `CmsPage`). Plugin assemblies are deliberately
*not* given to Blazor's `Router` (its route table cache would pin them, and the endpoint side is fixed at startup).
Instead two catch-alls host them: `/admin/{**rest}` in `DynCMS.UI` for the back office and the site's `/{*Path}` in
`DynCMS.Host`, both rendering `PluginPageOutlet`, which asks `IPluginRouter.Match(path, admin)` and renders the
component with `DynamicComponent` (route values converted to the `[Parameter]` types; unknown keys dropped; the page's
own `@layout` wrapped in a `LayoutView`). The router is built when plugins start: every `[Route]` on an `IComponent` is
parsed with `TemplateParser`, matched with `TemplateMatcher`, constraints resolved through the application's
`IInlineConstraintResolver`, and ordered by `RoutePrecedence.ComputeInbound`. Because literal routes beat catch-alls,
built-in pages always win over a plugin page with the same URL, and a plugin cannot shadow `/admin/content`. Paths
with a language prefix (`/de/guestbook`) are not matched against plugin pages.

**Endpoints and controllers** (`PluginEndpoints.cs`). `MapEndpoints` receives a `PluginEndpointRouteBuilder`: a
private `IEndpointRouteBuilder` whose `DataSources` the plugin's `MapGet`/`MapGroup` calls fill. `PluginManager`
publishes the endpoints of all running plugins through one `PluginEndpointDataSource` (added to `app.DataSources` by
`MapDynCmsPlugins()`), whose change token makes routing rebuild its matcher, so endpoints appear and disappear with the
plugin. Authorization metadata works as usual (`RequireAuthorization`, `[Authorize]`), and the endpoints run after
`UseAuthentication`. Controllers: `AddDynCms` calls `AddControllers()`, `UseDynCmsHost` maps them
(`Plugins.EnableControllers`); a plugin assembly that contains `ControllerBase` types is added to the
`ApplicationPartManager` on start and removed on stop, with `PluginActionDescriptorChangeProvider` telling MVC to
re-scan. Static files: `MapDynCmsPlugins()` maps `{StaticAssetsRequestPath}/{plugin}/{**path}` to the running plugin's
`wwwroot` (path-traversal guarded, `no-cache` in Development, one day otherwise).

**Security.** Uploading a plugin is remote code execution by design; only administrators can (UI and API scopes
`plugins:read`/`plugins:manage` are `AdminOnly`), and `AllowUpload=false` limits installation to people with file
access. Plugin admin pages are behind `[Authorize]` (any signed-in user); a plugin that needs more checks roles itself.
Plugin site pages and endpoints are as public as the plugin makes them.

### 25.4 Limitations

- No isolation: a plugin runs in-process with the application's rights and can call anything.
- Unload is best effort (see above); replace binaries and *Reload* freely in development, restart before measuring memory.
- Plugin pages are interactive-only (no prerender), so they are not indexable without JavaScript and show a spinner first.
- One plugin per assembly; a plugin cannot register `IDynCmsStartupTask`s, templates or property editors in the host
  registries (those are fixed at startup) — use `StartAsync` for seeding.
- Plugins must be built against the same DynCMS (and framework) version the site runs; shared assemblies are always
  the host's copy.
- Keyed services, `IHostedService`s registered by the host and `Configure<T>()` of *host* option types from a plugin are
  not supported.
