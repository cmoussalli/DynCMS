# DynCMS

An Umbraco-style content management system built with **.NET 10**, **Blazor Web App** (interactive server) and **EF Core**
on **SQLite** or **SQL Server**. Security (users, roles, entity/action permissions, sign-in tokens) is provided by **CMouss.IdentityFramework**.

## Solution layout

| Project | Type | Purpose |
|---|---|---|
| `src/DynCMS.Core` | Class library | Content model, EF Core SQLite persistence, services, template & property-editor registries, security adapter |
| `src/DynCMS.UI` | Razor class library | Back office at `/admin`, property editors, rendering components (`CmsContentRenderer`, `CmsImage`) |
| `src/DynCMS.Host` | Razor class library | **The NuGet package to install.** Blazor shell, request pipeline, catch-all page, starter site; `DynCmsHost.RunAsync(args)` is a whole program |
| `src/DynCMS.Web` | Blazor Web App | The minimal host project: what a new site looks like after `dotnet new blazor` plus a reference to `DynCMS.Host` (six files, no pages or layout of its own) |

## Run it

```bash
dotnet run --project src/DynCMS.Web
```

The first start has no database yet, so every page shows **`/setup`**; pick SQLite, choose whether the site starts
**empty** (only a home page) or as the **demo blog** (a paged blog with categories, tags, images and a draft), and
finish. The site is then at `/` and the back office at `/admin`.

| Account | Username | Password | Role |
|---|---|---|---|
| Administrator | `admin` | `Admin123!` | `Administrators` (everything, incl. Users & security) |

### Database setup (first run)

On start the app looks for `dyncms.database.json` in the application root (the content root, `src/DynCMS.Web`
when running from source). If it exists the database is loaded from it; if not, every page redirects to the
**`/setup`** page, where you choose:

1. **Create a new database** or **Use an existing database**.
2. **SQLite** (a file inside the app folder, default `App_Data/dyncms.db`) or **SQL Server**
   (server, port, database name, user, password, *trust server certificate*; for a new database DynCMS can
   connect to `master` and run `CREATE DATABASE` for you, or you untick that and point it at an empty database a DBA created).
3. **Starting content**: **Empty website** (a published home page and nothing else, plus the document types and
   templates you need to add pages) or **Demo blog** (the same, plus *About* and a *Blog* with fourteen articles six
   to a page, categories, tags, generated images, dictionary labels and one unpublished draft). Only applies when
   the database has no content yet.
4. **Test connection** checks the settings without changing anything; **Finish setup** writes the file, creates the
   schema (content and identity tables share the one database), the administrator account and the starting
   content, then sends you to the login page. No restart needed.

The file looks like this (the SQL Server password is stored in it, so protect it like `appsettings.json`):

```json
{ "Provider": "SqlServer",
  "SqlServer": { "Server": "localhost", "Port": 1433, "Database": "DynCMS", "UserName": "sa", "Password": "...", "TrustServerCertificate": true } }
```

```json
{ "Provider": "Sqlite", "Sqlite": { "DatabasePath": "App_Data/dyncms.db" } }
```

Delete `dyncms.database.json` (and, for SQLite, the `App_Data` folder) to start over; the setup page comes back on the next start.
Set `options.SeedStarterSite = false` in `Program.cs` to skip the starter site altogether; `options.StarterContent`
picks what an empty database gets when nobody was asked (the app started on an existing `dyncms.database.json`).

### The demo site (archived)

Until 2026-09-26 `src/DynCMS.Web` was a demo site: three languages, a dictionary, media, four document types, four
Blazor component templates, stored Liquid templates and a blog paged both ways. It was moved unchanged to
`_archive/DynCMS.Web-demo` so that `src/DynCMS.Web` shows what a new project looks like. The archive's README lists what the
demo showed and where to look; its `DemoSeeder.cs` remains the full worked example for seeding content in code.

## Concepts (Umbraco vocabulary)

- **Document types** (`Settings → Document types`): the schema of a page. Properties are grouped into tabs, each property uses a **property editor** (textbox, textarea, rich text, numeric, toggle, date, dropdown, media picker, content picker, tags, colour). The *Structure* tab controls where content may be created (allowed at root, allowed child types); the *Templates* tab which Blazor components can render it.
- **Content** (`Content`): a hierarchical tree. Each node has a draft state and a published snapshot; *Save* updates the draft, *Save & publish* validates mandatory properties and copies the draft to the published snapshot. URLs derive from the tree: the first root is `/`, its children `/segment`, and so on. `?preview=true` renders the draft for signed-in users.
- **Languages** (`Settings → Languages`): the languages the site publishes in. The default language is served at `/`, every other one under
  `/{iso-code}/` (`/de/ueber-uns`). A document type with *Allow varying by language* gives its content a name, URL segment and publish state
  **per language**, and the properties ticked *Vary by language* a value per language; the other properties are shared. The content editor has
  a language switcher, *Save & publish* asks which languages to publish, and a language can be *mandatory* (must be live before any other) or
  have a *fallback* (its values show where the current language is empty). The archived demo (`_archive/DynCMS.Web-demo`) is a worked example.
- **Dictionary** (`Settings → Dictionary`): translated texts that templates print by key — button labels, headings, empty-state
  messages — the way Umbraco's dictionary works. Every item has a unique key (`blog.readMore`) and one text per language, and can be
  filed under another item for organisation. A Liquid template prints one with `{{ 'blog.readMore' | dictionary }}`
  (`| dictionary: 'Read more'` gives a fallback text), a Blazor template with `T("blog.readMore")`. A language without a text shows
  its fallback language's text, then the default language's; when nothing exists the key itself is printed so the gap is visible.
- **Media** (`Media`): folders and uploaded files stored under `App_Data/media`, served at `/media/...`.
- **Templates** (`Settings → Templates`): every template the site can use is listed and managed here.
  - **Page templates** render a whole page and are the ones picked on document types and content.
  - **Partial views** are reusable fragments — a card, a navigation menu, a footer — that a template pulls in with
    `{% render 'alias' %}`, or `{% render 'alias', content: item %}` to hand it one item to render. Inside a partial,
    `content` is whatever the caller passed. Partials are deliberately *not* offered as page templates, and a partial
    cannot be deleted while another template still renders it.
  - **Stored templates** (both kinds) are written in [Liquid](https://shopify.github.io/liquid/) (rendered by
    [Fluid](https://github.com/sebastienros/fluid)), live in the `Templates` table and are created and edited in the back
    office; saving makes the change live at once.
    `content` is the page (`{{ content.name }}`, `{{ content.url }}`, any property by alias such as `{{ content.bodyText | raw }}`,
    plus `content.children`, `content.parent`, `content.ancestors`, `content.siblings`); `site.root` is the home page and `preview`
    tells whether a draft is shown. `content.culture` is the language being rendered and `content.cultures` / `site.languages` list the
    page / home page in every language (`iso_code`, `name`, `url`, `is_current`) for a language switcher. `request.path`,
    `request.url` and `request.query` describe the request being answered. Filters: `media` / `media_url` (media picker values), `content_by_id` / `content_url`
    (content picker values), `'article' | content_of_type`, `children_of`, `tags`, `json`, `paginate: 10` (one page of a list, chosen by
    `?page=`, with the links to the other pages), `page_url`, plus the standard Liquid filters.
    Output is HTML-encoded unless piped through `raw`.
  - **Component templates** are ordinary Blazor components that inherit `CmsTemplateBase` and are registered in `Program.cs`.
    They are compiled into the application, so their markup cannot be edited in the back office — but opening one and
    choosing **Create an editable version** writes a stored template with the same alias, scaffolded from the document
    type that uses it. A stored template with the same alias **overrides** the component, so a site can take a template
    over without redeploying; delete the stored one and the component takes over again.

### The template editor

`Settings → Templates` opens a Liquid editor built for editors, not just developers:

- syntax highlighting, line numbers, auto-indent, bracket and quote completion, `Tab` / `Shift+Tab` to indent,
  `Ctrl+/` to comment a selection and `Ctrl+S` to save;
- **suggestions** as you type inside `{{ … }}` and `{% … %}` (`Ctrl+Space` to ask for them): the document type's
  properties with the right filter already attached (rich text gets `| raw`, a media picker `| media_url`, a date
  `| date: "…"`), the page variables, every filter and tag, and the partials that exist;
- **Insert**, **Snippets** and **Partials** menus for clicking markup in without typing it;
- a **live preview** beside the code, rendered against real content — pick any page from the tree, or a made-up sample
  of a document type when there is no content yet — with the site's own stylesheets, refreshing as you type;
- a **Help** pane of clickable properties, variables and filters;
- **Info & usage**: which document types allow the template, how much content uses it, which templates render it and
  which partials it renders.

New templates start from a **starting point**: a page template, a listing page, a card or navigation partial, a blank
one, or a scaffold generated from a document type with every property already in the markup.

Component templates registered in code look like this:

```csharp
builder.Services
    .AddDynCms(o => builder.Configuration.GetSection("DynCms").Bind(o))
    .AddDynCmsUI()
    .AddTemplate<HomeTemplate>("home", "Home page")
    .AddTemplate<ArticleTemplate>("article", "Article");
```

Inside a template read values with `Content.Value<T>("alias")`, `Content.Html("bodyText")`, `Content.Tags("tags")`,
`Content.Date("publishDate")` and render pickers with `<CmsImage MediaId="@Content["image"]" />`.
Navigate with `IPublishedContentQuery` (`GetChildrenAsync`, `GetAncestorsAsync`, `GetByContentTypeAsync`, `GetByRouteAsync`).
Page a long list with `Paging.Page(items, pageSize, page)` and `<CmsPager Paging="_paging" />`, which links the pages as
`?page=n` (`ArticleListTemplate.razor` in `_archive/DynCMS.Web-demo` is a worked example).

## Use DynCMS in your own project (NuGet)

`DynCMS.Host` turns an empty ASP.NET Core web project into a running CMS. It pulls in `DynCMS.Core` and `DynCMS.UI`
and owns the Blazor shell (`App`, `Routes`, layout, catch-all page), the request pipeline and a starter site.

```bash
dotnet new web -n MySite && cd MySite
dotnet add package DynCMS.Host
```

`Program.cs` — replace the whole file (the template's `app.MapGet("/", …)` would otherwise shadow the CMS pages).
The package's `build/DynCMS.Host.props` sets `RequiresAspNetWebAssets=true`, so `_framework/blazor.web.js` is shipped even
though this project has no `.razor` file of its own (the Web SDK would otherwise leave it out, and `/setup` and `/admin`
would render without interactivity):

```csharp
await DynCMS.Host.DynCmsHost.RunAsync(args);
```

Run it. The first start lands on `/setup` to pick a database; after that an empty database gets a starter site
(a *Home* and a *Page* document type, Liquid templates, a published home page) and `/admin` is the back office.
The administrator account comes from the `DynCms:Identity` section of `appsettings.json`
(defaults `admin` / `Admin123!` — change them, and the `TokenEncryptionKey`).

When you want your own templates, seeding or options, spell the builder out. `AddDynCmsHost` returns the same
`IDynCmsBuilder` as `AddDynCms`, so everything chains:

```csharp
using DynCMS.Host;

var builder = WebApplication.CreateBuilder(args);

builder.AddDynCmsHost(o =>
    {
        o.Layout = typeof(MyLayout);          // your LayoutComponentBase instead of the built-in SiteLayout
        o.Stylesheets.Add("site.css");        // from your wwwroot
        o.SeedStarterSite = false;            // you seed your own content below
    })
    .AddTemplate<HomeTemplate>("home", "Home page")
    .AddStartupTask<MySeeder>();

var app = builder.Build();
app.UseDynCmsHost();                          // or app.UseDynCmsHost<MyApp>() with your own App.razor / Routes.razor
app.Run();
```

`src/DynCMS.Web` in this repository is exactly such a project: `dotnet new blazor --empty` with everything in the template's
`Components/` folder except `_Imports.razor`, and `wwwroot/app.css`, removed, because the host supplies the shell, the pages
and the CSS. A `Components/Templates/` folder appears the day you add your first component template; a new project has none.

Your own `wwwroot` files, minimal APIs, controllers and `@page` components work as in any ASP.NET Core app
(add an `_Imports.razor` for the latter — `dotnet new web` has none). `DynCmsHostOptions` also covers the
`<html lang>`, extra scripts, extra router assemblies and the HTTPS / HSTS / exception-handler switches. The escape hatches (own shell, no host package at all) are in the
[developer guide, §18](DEVELOPER-GUIDE.md#18-hosting-dyncms-in-your-own-application).

To build the packages from source: `dotnet pack src/DynCMS.Core`, `src/DynCMS.UI` and `src/DynCMS.Host` (all `-o` one folder),
then point a `nuget.config` at that folder.

## API & AI agents

Everything in the back office is also available programmatically, so integrations and AI agents can manage a site
without a browser:

- **REST API** at `/api/v1` (OpenAPI at `/api/v1/openapi.json`): languages, the dictionary, document types, templates (create, validate, preview),
  content (create, save, publish, move, delete), published content, media uploads, users, backups, API keys and
  visitor analytics (reports and the page view log).
- **MCP server** at `/mcp` (Streamable HTTP) with the same operations as tools, for Claude Code, Claude Desktop and
  any other MCP client.

Authentication is by **API key**: sign in, open **Settings → API & AI agents**, pick a name and the scopes, and copy
the key (it is shown once). The key acts as you, limited to the scopes you chose — never more than your own
permissions — and can be revoked at any time.

```bash
curl -H "Authorization: Bearer dcms_…" https://your-site/api/v1/
claude mcp add --transport http dyncms https://your-site/mcp --header "Authorization: Bearer dcms_…"
```

Details, scopes and conventions: [developer guide, §13](DEVELOPER-GUIDE.md#13-management-api-and-mcp-server).

## Analytics

The back office has a Google-Analytics-style **Analytics** section (`/admin/analytics`): page views, unique visitors,
sessions, bounce rate and session length with a traffic chart; top pages; sources (direct, search, social, referral);
countries and a **world map** of visitor cities; browsers, operating systems, devices and languages; and a live log of
every page view with the visitor's location, IP address, browser and referrer. Tracking is done on the server when a
page renders — no script on the site, no cookie, nothing for ad blockers to block — and written by a background
worker, so pages are not slowed down. Locations come from a configurable IP geolocation service (GeoJS by default,
cached per address). Administrators choose what is stored (IP address, anonymised or not; signed-in users; bots),
for how long, and can export CSV or delete data. The same reports are available through the API and MCP
(`analytics:read`). Analytics can live in its **own database** (SQLite or SQL Server, chosen at setup or later under
Data → Analytics database, with the history moved across): content backups and restores then never touch the page
view history, which gets its own backups. Details: [developer guide, §15](DEVELOPER-GUIDE.md#15-analytics).

## Plugins

DynCMS can be extended at runtime with **plugins**: Razor class libraries that any developer builds against the
small `DynCMS.Plugins.Sdk` package (the only reference a plugin needs) and an administrator uploads under **Settings → Plugins** (a `.dll` or a `.zip`), or copies
into `App_Data/plugins/{id}/`. No redeploy, no restart. A plugin can bring:

- **Pages.** Components with `@page`: routes under `/admin/…` render inside the back office for signed-in users,
  every other route renders inside the site layout, at exactly the URL the component declares.
- **Navigation.** Links in the back-office top bar or in the Settings tree.
- **Services**, injectable into the plugin's own components, endpoints and controllers with the usual `@inject` and
  constructor injection, and free to depend on anything the host registers (content services, logging, options).
- **APIs.** Minimal-API endpoints mapped by the plugin and `[ApiController]`s found in its assembly.
- **A private folder** (`App_Data/plugins/{id}/data`) for a SQLite database or any other files, which survives updates
  and is only deleted on uninstall when asked for.
- **Static files** from a `wwwroot` folder, served at `/_content/{id}/…`.
- **Content access and events.** The SDK includes the content, media, language and dictionary services, and a plugin can react
  to publishes, saves, deletes and media uploads with an `ICmsEventHandler`.
- **Editor and rendering extensions.** Property editors and component templates that show up next to the built-in ones.
- **UI slots.** Components in the dashboard, below the content editor, in the head of every site page and at the end of it.

Plugins are **started, stopped, reloaded and uninstalled** from the Plugins page and through the API and MCP
(`plugins:read`, `plugins:manage`). A stopped plugin keeps its files and data but nothing it adds is reachable. The
`samples/DynCMS.Plugin.Guestbook` project is a complete example (public guestbook page, moderated admin section, own
SQLite database, API, controller, stylesheet, dashboard widget, content-editor panel, event handler, property editor, template, site head/footer slots); building it produces an uploadable package. A plugin runs with the rights
of the application, so install only code you trust, or turn uploads off (`DynCms:Plugins:AllowUpload`) and deploy by
copying. Details: [developer guide, §25](DEVELOPER-GUIDE.md#25-plugins).

## Security (CMouss.IdentityFramework)

- `DynCMS.Core/Security/CmsIdentity.cs` configures `IDFManager` from `DynCms:Identity` settings and the configured database
  (SQLite or MSSQL, same database as the content), creates the identity tables, the `Editors` role and the
  `Content`/`Media`/`DocumentType` entities with `Read/Create/Update/Delete/Publish` actions.
- Login (`/admin/login`) calls `IDFManager.authService.AuthUserLogin`; the token is stored by the framework's
  `CookieAuthService` (`IDF_AuthToken` cookie).
- `DynCmsAuthenticationHandler` validates that cookie (or an `Authorization: Bearer` header) on every request through
  `IDFManager.authService.AuthUserToken`, producing a `ClaimsPrincipal` with role claims. This is what makes
  `[Authorize]`, `AuthorizeView` and `AuthorizeRouteView` work in the Blazor back office.
- `Users` (`/admin/users`, administrators only) hosts the framework's `IdentityAdminPart`: users, roles, entities,
  actions, permissions, apps, tokens and maintenance.
- Change `DynCms:Identity:TokenEncryptionKey` and the admin password per environment.
- API keys (`ApiKeys` table, hashed) authenticate the REST API and the MCP server; each call is checked against the
  key's scopes and then the owner's roles/permissions.

## Notes

- Schema creation uses `IRelationalDatabaseCreator.CreateTables` guarded by a probe table (`DatabaseSchema`), because the content
  and identity contexts share one database and `EnsureCreated` stops as soon as any table exists. `DatabaseSchema` also adds
  tables and columns a newer DynCMS version introduced (for example `Templates.Role`, which marks a template as a page
  template or a partial) to a database that already exists, so upgrading does not need a fresh database. Switch to EF Core
  migrations before evolving the content model in production.
- The identity framework targets .NET 8 / EF Core 9 and runs on .NET 10 with EF Core 10 through binary compatibility.
