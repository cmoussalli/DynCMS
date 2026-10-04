# DynCMS

**DynCMS is a content management system for .NET developers.** It turns an empty ASP.NET Core project into a
complete CMS with a single package reference: a back office at `/admin`, a content tree with draft and publish,
document types, templates, media, languages, a REST API, an MCP server for AI agents, built-in analytics and a plugin
system. It is built with **.NET 10**, **Blazor Web App** (interactive server) and **EF Core** on **SQLite** or
**SQL Server**, and follows the vocabulary of Umbraco, so if you have used a classic CMS you already know where things are.
Users, roles and permissions come from [CMouss.IdentityFramework](https://www.nuget.org/packages/CMouss.IdentityFramework).

What you get:

- **Content you model yourself.** Document types define the fields of a page, property editors (text, rich text, media
  picker, tags, …) edit them, and the content tree decides the URLs.
- **Draft and publish.** Edit freely; *Save & publish* validates and copies the draft to the live snapshot. `?preview=true` shows drafts.
- **Two kinds of templates.** Liquid templates edited live in the back office (with a code editor, suggestions and live
  preview), or Blazor components compiled into your app. A stored template can override a component without a redeploy.
- **Languages and a dictionary.** Per-language names, URLs, values and publish state, with fallbacks.
- **Media library**, served from `/media`.
- **REST API and MCP server** (`/api/v1`, `/mcp`) with scoped API keys, so integrations and AI agents can manage the site.
- **Analytics** with no script and no cookie: page views, visitors, sources, countries and a world map.
- **Backups and restore**, database switching and a files manager, all in the back office.
- **Plugins** that administrators upload at runtime, with no redeploy and no restart.

| Package | What it is |
|---|---|
| `DynCMS.Host` | **The package to install.** Pulls in everything below, plus the Blazor shell, request pipeline and a starter site |
| `DynCMS.Core` | Content model, persistence, services, template and property-editor registries, security |
| `DynCMS.UI` | The back office, property editors and rendering components |
| `DynCMS.Plugins.Sdk` | The only reference a plugin needs |

---

## Quick start: a new site

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet new web -n MySite
cd MySite
dotnet add package DynCMS.Host
```

Add this to the `<PropertyGroup>` of `MySite.csproj` (without it `/setup` and `/admin` render but no button reacts, see
[DEVELOPER-GUIDE](DEVELOPER-GUIDE.md) §18):

```xml
<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
```

Replace the whole of `Program.cs` (the template's `MapGet("/")` would otherwise shadow the CMS pages):

```csharp
await DynCMS.Host.DynCmsHost.RunAsync(args);
```

Run it:

```bash
dotnet run
```

The first start has no database, so every page redirects to **`/setup`**. There you choose:

1. **SQLite** (a file under `App_Data/`) or **SQL Server** (DynCMS can create the database for you or use an empty one).
2. **Starting content**: an **empty website** (just a home page) or the **demo blog** (articles, categories, tags, images).
3. Press **Test connection**, then **Finish setup**. The schema, the administrator and the content are created and you land on the login page, with no restart.

The site is now at `/` and the back office at `/admin`. The default administrator is **`admin` / `Admin123!`**. Change the
password, and `DynCms:Identity:TokenEncryptionKey`, in `appsettings.json` before going anywhere near production:

```json
{
  "DynCms": {
    "Identity": { "AdminUserName": "admin", "AdminPassword": "a-strong-password", "TokenEncryptionKey": "a-long-random-string" }
  }
}
```

The setup page writes `dyncms.database.json` next to the app (it contains the SQL Server password if you use one, so
protect it like `appsettings.json`). Delete it, and `App_Data` for SQLite, to run setup again.

### Customising the host

When you want your own templates, seed data, layout or stylesheet, spell the builder out instead of calling `RunAsync`.
`AddDynCmsHost` returns the CMS builder, so everything chains:

```csharp
using DynCMS.Host;

var builder = WebApplication.CreateBuilder(args);

builder.AddDynCmsHost(o =>
    {
        o.Layout = typeof(MyLayout);          // your LayoutComponentBase instead of the built-in site layout
        o.Stylesheets.Add("site.css");        // from your wwwroot
        o.SeedStarterSite = false;            // you seed your own content
    })
    .AddTemplate<HomeTemplate>("home", "Home page")   // a Blazor component template
    .AddStartupTask<MySeeder>();                       // runs once the database is ready

var app = builder.Build();
app.UseDynCmsHost();                          // or app.UseDynCmsHost<MyApp>() with your own root component
app.Run();
```

Your own `wwwroot` files, minimal APIs, controllers and `@page` components work as in any ASP.NET Core app (add an
`_Imports.razor` for the latter). `src/DynCMS.Web` in this repository is exactly this: an empty web project with one
reference to `DynCMS.Host`.

A component template inherits `CmsTemplateBase` and reads its page through `Content`:

```razor
@inherits CmsTemplateBase

<h1>@Content.Name</h1>
@Content.Html("bodyText")
<CmsImage MediaId="@Content["image"]" />
```

Useful members: `Content.Value<T>("alias")`, `Content.Html`, `Content.Tags`, `Content.Date`, `IPublishedContentQuery` for
navigation, `Paging` and `<CmsPager>` for long lists, and `T("dictionary.key")` for translated text. The full
reference, including the Liquid API for stored templates, is in the [developer guide](DEVELOPER-GUIDE.md).

---

## Running from source

```bash
git clone https://github.com/cmoussalli/DynCMS.git
cd DynCMS
dotnet run --project src/DynCMS.Web
```

| Project | Purpose |
|---|---|
| `src/DynCMS.Core` | Class library: model, persistence, services, security |
| `src/DynCMS.UI` | Razor class library: back office, editors, rendering components |
| `src/DynCMS.Host` | Razor class library: the NuGet package to install |
| `src/DynCMS.Plugins.Sdk` | The plugin contract and its MSBuild packaging |
| `src/DynCMS.Web` | The minimal host project |
| `samples/DynCMS.Plugin.Guestbook` | A complete plugin example |

To build the packages locally: `dotnet pack` each of `DynCMS.Plugins.Sdk`, `DynCMS.Core`, `DynCMS.UI` and
`DynCMS.Host` with `-o` to one folder, then point a `nuget.config` at that folder. Pushing a `v*` tag publishes all
four to nuget.org through `.github/workflows/nuget.yml`.

---

## Writing a plugin

A plugin is a Razor class library that references **only** `DynCMS.Plugins.Sdk`. An administrator uploads it under
**Settings → Plugins** (a `.zip` or a `.dll`), or you copy it into `App_Data/plugins/{id}/`. It starts without a
redeploy or a restart, and can be stopped, reloaded and uninstalled the same way. A plugin can bring:

- **Pages** (`@page` components). Routes under `/admin/…` render inside the back office, any other route inside the site layout.
- **Navigation** links in the back-office top bar or the Settings tree.
- **Services** injectable into its components, endpoints and controllers, with access to the CMS content services.
- **APIs**: minimal-API endpoints and `[ApiController]`s.
- **A private data folder** (for a SQLite database, say) that survives updates, and a `wwwroot` folder served at `/_content/{id}/…`.
- **Property editors** and **component templates** that appear next to the built-in ones.
- **UI slots**: the dashboard, below the content editor, the head of every site page and the end of it.
- **Event handlers** (`ICmsEventHandler`) for publish, save, delete and media uploads.

### 1. Create the project

```bash
dotnet new razorclasslib -n Acme.Hello --framework net10.0
cd Acme.Hello
dotnet add package DynCMS.Plugins.Sdk
```

Delete the template's sample component and `wwwroot` content, then add `@using` lines to `_Imports.razor` (Razor does not
pick up the SDK's global usings for component tags, and a missing one is a build error):

```razor
@using Microsoft.AspNetCore.Components.Web
@using DynCMS.Plugins
@using DynCMS.Plugins.Components
@using DynCMS.Plugins.Services
@using DynCMS.Plugins.Models
```

Do not reference `DynCMS.Core` or `DynCMS.UI`: the SDK carries everything a plugin normally needs. The SDK's build files
also make sure only your own assemblies go into the package; the site already has DynCMS, the framework and EF Core. If
your plugin uses another library (EF Core SQLite, say), add it as an ordinary `PackageReference` at the version the site ships.

Useful project properties (shown on the Plugins page):

```xml
<PropertyGroup>
  <AssemblyTitle>Hello</AssemblyTitle>
  <Description>Says hello in the back office and on the site.</Description>
  <Company>Acme</Company>
  <Version>1.0.0</Version>
</PropertyGroup>
```

### 2. The plugin class

Add **one** public class deriving from `DynCmsPlugin`. Declare the oldest DynCMS version you support: it is required,
and a plugin without it, or needing a newer DynCMS, is not attached.

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

[assembly: DynCmsMinimumVersion("0.1.0")]

namespace Acme.Hello;

public sealed class HelloPlugin : DynCmsPlugin
{
    public override string Icon => "message";

    // A link in the back-office top bar.
    public override IReadOnlyList<PluginMenuItem> MenuItems =>
    [
        new("Hello", "/admin/hello", Icon: "message")
    ];

    // Services for this plugin's components, endpoints and controllers.
    public override void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<GreetingService>();
    }

    // Minimal-API endpoints; they exist while the plugin runs.
    public override void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context)
    {
        endpoints.MapGet("/api/plugins/hello", (GreetingService g) => g.Greet("world")).AllowAnonymous();
    }
}

public sealed class GreetingService
{
    public string Greet(string name) => $"Hello, {name}!";
}
```

Other overrides: `StartAsync` / `StopAsync` (create a database, warm caches; throwing in `StartAsync` marks the plugin
*failed*), `PropertyEditors`, `Templates` and `UiExtensions`. `IPluginContext` gives you `DataDirectory`,
`SqliteConnectionString("file.db")`, `Configuration` (the `Plugins:{id}` section of `appsettings.json` overlaid with a
`settings.json` in the plugin folder), `Services`, `Logger` and `Environment`.

### 3. Pages

Pages are ordinary components. A route starting with `/admin/` is a back-office page (signed-in users; use `AuthorizeView`
to restrict by role); anything else is a site page at exactly that URL.

`Pages/HelloAdmin.razor`:

```razor
@page "/admin/hello"
@inject GreetingService Greeter
@inject IContentService Content

<PageTitle>Hello</PageTitle>
<div class="dc-card">
    <h2><Icon Name="message" /> @Greeter.Greet("back office")</h2>
</div>
```

`Pages/HelloPublic.razor`:

```razor
@page "/hello"
@inject GreetingService Greeter

<PageTitle>Hello</PageTitle>
<h1>@Greeter.Greet("visitor")</h1>
```

Admin pages get the back office's `dc-*` styles for free, plus the SDK's `Icon`, `Modal`, `ConfirmDialog` and
`ToastService`. Route parameters, `[SupplyParameterFromQuery]`, `@layout` and `PageTitle` all work, and a site page is
also reachable under a language prefix (`/de/hello`).

### 4. Build and install

```bash
dotnet build
```

Every build produces `bin/Debug/Acme.Hello.plugin.zip`. Upload it under **Settings → Plugins**, or use the
`POST /api/v1/plugins/upload` endpoint with an API key that has the `plugins:manage` scope.

For a tight edit-build-test loop, point the build straight at a site's plugin folder and press **Reload** on the Plugins page:

```bash
dotnet build -p:DynCmsPluginDeployDir=../MySite/App_Data/plugins
```

Uploading a plugin with an id that already exists replaces its binaries and keeps its `data/` folder and `settings.json`.
The id is the assembly name unless you override `Id`.

> A plugin runs with the rights of the application, so install only code you trust. To turn uploads off and deploy by
> copying instead, set `DynCms:Plugins:AllowUpload` to `false`.

`samples/DynCMS.Plugin.Guestbook` is a complete example: a public page, a moderated admin section, its own SQLite
database, an API, a controller, a stylesheet, a dashboard widget, a content-editor panel, an event handler, a property
editor, a template and site head/footer slots.

---

## Concepts

- **Document types** (*Settings → Document types*): the schema of a page. Properties are grouped into tabs and use a property editor. *Structure* controls where content may be created; *Templates* controls which templates can render it.
- **Content**: a tree of nodes, each with a draft and a published snapshot. The first root is `/`, its children `/segment`, and so on.
- **Languages** (*Settings → Languages*): the default language is served at `/`, every other under `/{iso}/`. A document type can allow content to vary by language, per property.
- **Dictionary**: translated texts printed by key (`{{ 'blog.readMore' | dictionary }}` in Liquid, `T("blog.readMore")` in Blazor).
- **Media**: files under `App_Data/media`, served at `/media/…`.
- **Templates**: *page templates* render a page, *partials* are reusable fragments (`{% render 'card', content: item %}`), and both can be Liquid (stored in the database, edited live) or Blazor components (compiled in). A stored template with the same alias overrides a component.

## API and AI agents

Everything in the back office is also available programmatically at `/api/v1` (OpenAPI at `/api/v1/openapi.json`) and as an
MCP server at `/mcp`. Authenticate with an **API key** created under **Settings → API & AI agents**; it acts as you,
limited to the scopes you pick:

```bash
curl -H "Authorization: Bearer dcms_…" https://your-site/api/v1/
claude mcp add --transport http dyncms https://your-site/mcp --header "Authorization: Bearer dcms_…"
```

## Security

Sign-in, roles and permissions come from CMouss.IdentityFramework, sharing the content database. The back office uses a
cookie; the API and MCP use hashed API keys checked against both their scopes and the owner's permissions. Before
production: change the administrator password and `TokenEncryptionKey`, serve over HTTPS, and protect
`dyncms.database.json`.

## More

The [developer guide](DEVELOPER-GUIDE.md) covers the boot sequence, persistence, the content model, the service
reference, rendering, templates and the Liquid API, property editors, the management API, backups, analytics,
configuration, hosting DynCMS in your own application, and the plugin system in depth (§25).

DynCMS is released under the [MIT license](LICENSE).
