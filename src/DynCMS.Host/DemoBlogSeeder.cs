using System.Text;
using System.Text.Json;
using DynCMS.Plugins.Models;
using DynCMS.Core.PropertyEditors;
using DynCMS.Core.Services;
using DynCMS.Core.Templates;
using Microsoft.Extensions.Logging;

namespace DynCMS.Host;

/// <summary>
/// The demo blog the <c>/setup</c> page offers (<see cref="StarterContent.DemoBlog"/>). Runs inside
/// <see cref="StarterSiteSeeder"/> after it has created the shared document types and templates, and adds:
/// <list type="bullet">
/// <item>document types <c>blog</c> (intro, articles per page) and <c>article</c> (summary, body, image, publish
/// date, author, category, tags, featured), and templates <c>blog</c>, <c>article</c> and the <c>articleCard</c> partial;</item>
/// <item>a media folder with generated images, a few dictionary labels for the pager;</item>
/// <item>a home page with a hero image, an <em>About</em> page with a child, and a <em>Blog</em> with fourteen
/// published articles (six to a page, so the list runs to three pages) and one draft that only shows in preview.</item>
/// </list>
/// Everything is ordinary content: delete the demo pages in the back office when you are done looking.
/// </summary>
internal sealed class DemoBlogSeeder(
    IContentTypeService contentTypes,
    IContentService content,
    ITemplateService templates,
    IMediaService media,
    ILanguageService languages,
    IDictionaryService dictionary,
    ILogger<DemoBlogSeeder> logger)
{
    /// <param name="home">The home document type created by the starter site (gets the blog as an allowed child).</param>
    /// <param name="page">The page document type created by the starter site.</param>
    public async Task SeedAsync(ContentType home, ContentType page, CancellationToken ct = default)
    {
        logger.LogInformation("Creating the demo blog: document types, templates, media, dictionary and content");

        // ---- Templates (Settings → Templates). The partial first: the pages render it. ----
        await templates.SaveAsync(new Template
        {
            Alias = "articleCard", Name = "Article card", Role = TemplateRole.Partial,
            Description = "A card for one article: image, category, date, title and summary. Rendered with {% render 'articleCard', content: item %}.",
            Content = DemoBlogTemplates.ArticleCard
        });
        await templates.SaveAsync(new Template
        {
            Alias = "blog", Name = "Blog",
            Description = "The articles below the page, newest first, filtered by ?category= and paged with the 'paginate' filter. The page size is a property of the page.",
            Content = DemoBlogTemplates.Blog
        });
        await templates.SaveAsync(new Template
        {
            Alias = "article", Name = "Article",
            Description = "One article: category, date and author, summary, image, body text, tags and three more articles.",
            Content = DemoBlogTemplates.Article
        });

        // ---- Document types (Settings → Document types) ----
        var article = await contentTypes.SaveAsync(new ContentType
        {
            Alias = "article", Name = "Article", Icon = "edit",
            Description = "A blog post. Lives below the blog page.",
            AllowedTemplateAliases = ["article"], DefaultTemplateAlias = "article", SortOrder = 3,
            Properties =
            [
                Prop("summary", "Summary", PropertyEditorAliases.TextArea, "Content", mandatory: true, description: "Shown in listings and under the heading.", config: new() { ["rows"] = "3" }),
                Prop("bodyText", "Body text", PropertyEditorAliases.RichText, "Content", mandatory: true),
                Prop("image", "Image", PropertyEditorAliases.MediaPicker, "Content", config: new() { ["imagesOnly"] = "true" }),
                Prop("publishDate", "Publish date", PropertyEditorAliases.DatePicker, "Details", mandatory: true, description: "Articles are listed newest first."),
                Prop("author", "Author", PropertyEditorAliases.TextBox, "Details"),
                Prop("category", "Category", PropertyEditorAliases.Dropdown, "Details", description: "The blog page can be filtered by category.", config: new() { ["items"] = "News\nTutorial\nRelease notes" }),
                Prop("tags", "Tags", PropertyEditorAliases.Tags, "Details"),
                Prop("featured", "Featured", PropertyEditorAliases.Toggle, "Details", description: "Featured articles appear at the top of the home page.", config: new() { ["label"] = "Show on home page" })
            ]
        });

        var blog = await contentTypes.SaveAsync(new ContentType
        {
            Alias = "blog", Name = "Blog", Icon = "newspaper",
            Description = "Lists the articles below it, a page at a time.",
            AllowedChildTypeAliases = [article.Alias],
            AllowedTemplateAliases = ["blog"], DefaultTemplateAlias = "blog", SortOrder = 2,
            Properties =
            [
                Prop("intro", "Intro", PropertyEditorAliases.TextArea, "Content", config: new() { ["rows"] = "3" }),
                Prop("pageSize", "Articles per page", PropertyEditorAliases.Numeric, "Settings", description: "How many articles one page of the list shows.", config: new() { ["min"] = "1", ["max"] = "50", ["step"] = "1" })
            ]
        });

        // The home page may now hold a blog as well as pages.
        home.AllowedChildTypeAliases = [page.Alias, blog.Alias];
        home = await contentTypes.SaveAsync(home);

        // ---- Dictionary (Settings → Dictionary): the pager's labels, in the default language ----
        var iso = (await languages.GetDefaultAsync(ct)).IsoCode;
        var pagingFolder = await dictionary.SaveAsync(new DictionaryItem { Key = "paging" }, ct);
        await DictionaryAsync("paging.previous", "Previous");
        await DictionaryAsync("paging.next", "Next");
        await DictionaryAsync("paging.pageOf", "Page {page} of {pages}");
        await DictionaryAsync("paging.summary", "Showing {from}–{to} of {total} articles");

        async Task DictionaryAsync(string key, string text)
        {
            var item = new DictionaryItem { Key = key, ParentId = pagingFolder.Id };
            item.Set(iso, text);
            await dictionary.SaveAsync(item, ct);
        }

        // ---- Media (generated gradients, so the demo needs no files of its own) ----
        var hero = await media.UploadAsync(null, "hero.svg", "image/svg+xml", Svg("Hello", "#2f47d1", "#7b8cff"), ct);
        var folder = await media.CreateFolderAsync(null, "Blog", ct);
        var imgStart = await media.UploadAsync(folder.Id, "getting-started.svg", "image/svg+xml", Svg("Getting started", "#1f9d6a", "#8fe3c0"), ct);
        var imgTypes = await media.UploadAsync(folder.Id, "document-types.svg", "image/svg+xml", Svg("Document types", "#d98a12", "#ffd58a"), ct);
        var imgTemplates = await media.UploadAsync(folder.Id, "templates.svg", "image/svg+xml", Svg("Templates", "#a13bd6", "#e2b3ff"), ct);
        var imgPaging = await media.UploadAsync(folder.Id, "paging.svg", "image/svg+xml", Svg("Paging", "#c0392b", "#ff9f9a"), ct);
        var imgMedia = await media.UploadAsync(folder.Id, "media.svg", "image/svg+xml", Svg("Media", "#0e7c86", "#7fd6de"), ct);
        var imgApi = await media.UploadAsync(folder.Id, "api.svg", "image/svg+xml", Svg("API", "#4b3fd6", "#b3adff"), ct);
        var imgData = await media.UploadAsync(folder.Id, "database.svg", "image/svg+xml", Svg("Database", "#5c6b7a", "#c3ccd6"), ct);

        // ---- Content ----
        var root = await content.CreateAsync(home.Id, null, "DynCMS Demo", ct: ct);
        root.SetValue("heroTitle", "A small blog to look around in");
        root.SetValue("heroText", "This site was created by the setup page to show what DynCMS does: document types, Liquid templates, media, a paged blog with categories and tags, and a draft you can preview.");
        root.SetValue("heroImage", hero.Id.ToString());
        root.SetValue("bodyText",
            "<h2>What to look at</h2>" +
            "<ul>" +
            "<li><strong>The blog</strong> has fourteen articles, six to a page, so the list runs to three pages. Filter it by category and page through: the address carries both.</li>" +
            "<li><strong>Content</strong> in the back office is the tree behind these pages. The blog holds one draft that only shows with <code>?preview=true</code>.</li>" +
            "<li><strong>Settings → Document types</strong> defines the shape of an article: summary, body, image, date, author, category, tags and a featured toggle.</li>" +
            "<li><strong>Settings → Templates</strong> holds the Liquid that renders everything here. The <em>Blog</em> template is the paging example; edits are live at once.</li>" +
            "<li><strong>Settings → Dictionary</strong> has the pager's labels, and <strong>Media</strong> the generated images.</li>" +
            "</ul>" +
            "<p>Everything is ordinary content. Delete the demo pages under <strong>Content</strong> when you start your own site, or keep what is useful.</p>");
        root.SetValue("metaDescription", "A DynCMS demo site with a paged blog.");
        await SaveAndPublishAsync(root, ct);

        var about = await content.CreateAsync(page.Id, root.Id, "About", ct: ct);
        about.SetValue("summary", "What this demo site is and how it was made.");
        about.SetValue("bodyText",
            "<p>This site was seeded by the <em>Demo blog</em> option of the setup page. It runs on the stock <em>DynCMS.Host</em> package: no custom code, " +
            "just document types, content and stored Liquid templates that you can open and change in the <a href=\"/admin\">back office</a>.</p>" +
            "<h2>How pages become URLs</h2>" +
            "<p>Pages live in a tree under the home page and their address follows their place in it: this page is at <code>/about</code>, " +
            "the page below it at <code>/about/how-it-was-built</code>, and an article at <code>/blog/its-name</code>.</p>" +
            "<p>A page uses the <em>Page</em> template by default. Switch it to <em>Section page</em> on the Info tab to show its children as cards, twelve to a page.</p>");
        about.SetValue("metaDescription", "About the DynCMS demo site.");
        await SaveAndPublishAsync(about, ct);

        var howBuilt = await content.CreateAsync(page.Id, about.Id, "How it was built", ct: ct);
        howBuilt.SetValue("summary", "The pieces the demo is made of, and where to find each one.");
        howBuilt.SetValue("bodyText",
            "<ul>" +
            "<li><strong>Document types:</strong> <em>Home</em>, <em>Page</em>, <em>Blog</em> and <em>Article</em>, under Settings → Document types.</li>" +
            "<li><strong>Templates:</strong> <em>Home page</em>, <em>Page</em>, <em>Section page</em>, <em>Blog</em> and <em>Article</em>, plus the partials <em>Card</em>, <em>Article card</em> and <em>Navigation</em>, under Settings → Templates.</li>" +
            "<li><strong>Media:</strong> a hero image and a <em>Blog</em> folder of generated pictures, under Media.</li>" +
            "<li><strong>Dictionary:</strong> the labels of the pager, under Settings → Dictionary.</li>" +
            "</ul>" +
            "<p>The seeder that created all this is <code>DemoBlogSeeder</code> in the <em>DynCMS.Host</em> package, a good starting point for seeding a site of your own.</p>");
        await SaveAndPublishAsync(howBuilt, ct);

        var blogPage = await content.CreateAsync(blog.Id, root.Id, "Blog", ct: ct);
        blogPage.SetValue("intro", "News, tutorials and release notes. Pick a category, or page through all of them: the address carries the page number and the category, so every page can be bookmarked.");
        blogPage.SetValue("pageSize", "6");
        await SaveAndPublishAsync(blogPage, ct);

        // Fourteen published articles, six to a page: three pages. Dates go back from today so the order is obvious.
        var today = DateTime.UtcNow.Date;

        await ArticleAsync(article.Id, blogPage.Id, "Getting started with DynCMS", imgStart.Id, "Tutorial", today.AddDays(-2),
            "Sign in to the back office and publish your first page in five minutes.",
            "<p>Sign in at <code>/admin</code> with the administrator account from the <code>DynCms:Identity</code> configuration section. Open <strong>Content</strong>, pick a node in the tree and use " +
            "<em>Save &amp; publish</em> to make changes live.</p><h2>Preview</h2><p>Every content item has a preview link on its Info tab that renders the draft with its template. This blog holds one draft article that only appears there.</p>",
            ["tutorial", "basics"], featured: true, ct);

        await ArticleAsync(article.Id, blogPage.Id, "Modelling content with document types", imgTypes.Id, "Tutorial", today.AddDays(-5),
            "Document types describe the shape of your content: properties, groups, allowed children and templates.",
            "<p>Open <strong>Settings → Document types</strong>. Each property uses a <em>property editor</em>: a textbox, a rich text editor, a date picker, a media picker, tags and so on.</p>" +
            "<p>The <strong>Structure</strong> tab controls where content can be created in the tree, and the <strong>Templates</strong> tab which templates can render it. " +
            "The <em>Article</em> type behind this page is the example: open it and add a property, then look at the article in the editor.</p>",
            ["tutorial", "document types"], featured: true, ct);

        await ArticleAsync(article.Id, blogPage.Id, "Rendering content with Liquid templates", imgTemplates.Id, "Tutorial", today.AddDays(-9),
            "Templates are stored in the database, written in Liquid and live the moment you save them.",
            "<p>Open <strong>Settings → Templates</strong>. A template receives the page as <code>content</code> and reaches its properties by alias: " +
            "<code>{{ content.summary }}</code>, <code>{{ content.bodyText | raw }}</code>, <code>{{ content.image | media }}</code>.</p>" +
            "<p>Partials such as <em>Article card</em> are rendered with <code>{% render 'articleCard', content: item %}</code>, so a card is written once and used on the home page, the blog and at the bottom of this article.</p>",
            ["templates", "liquid"], featured: true, ct);

        await ArticleAsync(article.Id, blogPage.Id, "Paging a long list", imgPaging.Id, "Tutorial", today.AddDays(-11),
            "The blog shows six articles at a time; the page number lives in the address, so every page can be bookmarked and crawled.",
            "<p>The <em>Blog</em> template pipes the articles through <code>{{ articles | paginate: page_size }}</code>. What comes back is the items of the requested page together with the numbers " +
            "(<code>page</code>, <code>page_count</code>, <code>total</code>, <code>first</code>, <code>last</code>) and the links (<code>previous_url</code>, <code>next_url</code>, one <code>url</code> per page).</p>" +
            "<p>The page number comes from <code>?page=</code> in the address. A number out of range is clamped, so <code>?page=99</code> shows the last page rather than an empty one, " +
            "and the first page has no <code>?page=</code> at all, so each page has one address. The links keep the rest of the query string: page through the <em>Tutorial</em> category and you stay in it.</p>" +
            "<p><em>Articles per page</em> is a property of the blog page, so an editor changes it in the back office without touching the template. The <em>Section page</em> template does the same for ordinary pages, twelve to a page.</p>",
            ["tutorial", "paging", "liquid"], featured: false, ct);

        await ArticleAsync(article.Id, blogPage.Id, "DynCMS 1.0 released", null, "Release notes", today.AddDays(-14),
            "The first release ships document types, a content tree with publishing, a media library, users and eleven property editors.",
            "<p>Thanks to everyone who tested the early builds. This article has no image, so its card shows a plain placeholder instead.</p>", ["release"], featured: false, ct);

        await ArticleAsync(article.Id, blogPage.Id, "Managing media in the back office", imgMedia.Id, "Tutorial", today.AddDays(-17),
            "Upload images and files into folders, pick them on content with the media picker and serve them from /media.",
            "<p>Open <strong>Media</strong> in the back office. Files live in folders, are stored under <code>App_Data/media</code> and are served at <code>/media/…</code>.</p>" +
            "<p>A <em>media picker</em> property stores the id of the chosen item; a template renders it with <code>{{ content.image | media }}</code> for the item or <code>{{ content.image | media_url }}</code> for just the address.</p>",
            ["tutorial", "media"], featured: false, ct);

        await ArticleAsync(article.Id, blogPage.Id, "Partials: reuse markup across templates", imgTemplates.Id, "Tutorial", today.AddDays(-20),
            "A card, a menu or a footer written once and rendered from any template with {% render 'alias' %}.",
            "<p>A <strong>partial view</strong> is a stored template with the role <em>Partial</em>. It is never offered as a page template; other templates pull it in with " +
            "<code>{% render 'card', content: item %}</code>, and inside it <code>content</code> is whatever the caller passed.</p>" +
            "<p>The back office shows which templates render a partial, and refuses to delete one that is still in use.</p>",
            ["templates", "liquid"], featured: false, ct);

        await ArticleAsync(article.Id, blogPage.Id, "Categories and tags", imgTypes.Id, "Tutorial", today.AddDays(-24),
            "A dropdown for the one category an article belongs to, and a tags editor for as many keywords as you like.",
            "<p>The <em>Category</em> property is a <strong>dropdown</strong> whose options are set on the document type. The blog page reads the categories that are in use with " +
            "<code>{{ articles | map: 'category' | uniq }}</code> and filters by <code>?category=</code> with the <code>where</code> filter.</p>" +
            "<p><em>Tags</em> are stored as a list and arrive in a template as an array, so <code>{% for tag in content.tags %}</code> just works.</p>",
            ["tutorial", "document types"], featured: false, ct);

        await ArticleAsync(article.Id, blogPage.Id, "Previewing drafts before you publish", imgStart.Id, "Tutorial", today.AddDays(-28),
            "Every content item has a draft and a published snapshot. Add ?preview=true to see the draft with its real template.",
            "<p><em>Save</em> updates the draft, <em>Save &amp; publish</em> copies it to the published snapshot. The preview link on the Info tab opens the page with " +
            "<code>?preview=true</code>, which renders the draft for signed-in back-office users and shows a preview bar with a link back to the editor.</p>" +
            "<p>Unpublished items, such as the draft article in this blog, only appear in preview mode.</p>",
            ["tutorial", "basics"], featured: false, ct);

        await ArticleAsync(article.Id, blogPage.Id, "Backing up and restoring the database", imgData.Id, "News", today.AddDays(-33),
            "The back office can take a backup of the database and restore it later; backups are listed with their size and date.",
            "<p>Administrators find <strong>Backups &amp; restore</strong> in the <em>Data</em> section of the back office. A backup is a copy of the database, stored under <code>App_Data/backups</code>.</p>" +
            "<p>Restoring replaces the current database, so the page asks for confirmation first.</p>",
            ["news", "backups"], featured: false, ct);

        await ArticleAsync(article.Id, blogPage.Id, "Talking to DynCMS from code: the management API", imgApi.Id, "Tutorial", today.AddDays(-38),
            "A REST API with OpenAPI documentation covers content, document types, media, templates and languages, secured by API keys with scopes.",
            "<p>Create an API key under <strong>Settings → API &amp; AI agents</strong>, choose its scopes and send it with your requests. " +
            "The OpenAPI document describes every operation, so a client can be generated in any language.</p>",
            ["tutorial", "api"], featured: false, ct);

        await ArticleAsync(article.Id, blogPage.Id, "Let an AI agent edit your site with MCP", imgApi.Id, "News", today.AddDays(-45),
            "DynCMS ships a Model Context Protocol server, so an AI agent can create document types, write content and edit templates through the same application layer as the back office.",
            "<p>The MCP server exposes the management operations as tools. Point an agent at it with an API key and it can build a site from a brief, " +
            "fill in texts or tidy up templates, with every change going through the same validation as the back office.</p>",
            ["news", "api", "mcp"], featured: false, ct);

        await ArticleAsync(article.Id, blogPage.Id, "Running DynCMS on SQL Server", imgData.Id, "Tutorial", today.AddDays(-52),
            "SQLite is the default, but the setup page can create or use a SQL Server database just as easily.",
            "<p>On the <code>/setup</code> page choose <strong>SQL Server</strong>, enter the server, database name and credentials, and let DynCMS create the database or point it at an empty one. " +
            "<em>Test connection</em> checks the settings before anything is written. The choice is stored in <code>dyncms.database.json</code>.</p>",
            ["tutorial", "sql server"], featured: false, ct);

        await ArticleAsync(article.Id, blogPage.Id, "DynCMS 0.9 preview", null, "Release notes", today.AddDays(-60),
            "The preview before 1.0: the content tree, document types and the first property editors, with SQLite persistence.",
            "<p>Feedback from the preview shaped the publishing model and the template editor that shipped in 1.0.</p>", ["release"], featured: false, ct);

        // One draft: saved, never published, so it only shows up in preview mode and in the back office.
        var draft = await content.CreateAsync(article.Id, blogPage.Id, "Working draft: what is coming next", ct: ct);
        draft.SetValue("summary", "This article is saved but not published, so it only shows up in preview mode.");
        draft.SetValue("bodyText", "<p>Roadmap ideas: version history, scheduled publishing, editorial workflow.</p>");
        draft.SetValue("publishDate", today.AddDays(3).ToString("yyyy-MM-dd"));
        draft.SetValue("author", "DynCMS Team");
        draft.SetValue("category", "News");
        await content.SaveAsync(draft, ct);
    }

    private async Task ArticleAsync(Guid typeId, Guid parentId, string name, Guid? imageId, string category, DateTime date,
        string summary, string body, string[] tags, bool featured, CancellationToken ct)
    {
        var node = await content.CreateAsync(typeId, parentId, name, ct: ct);
        node.SetValue("summary", summary);
        node.SetValue("bodyText", body);
        node.SetValue("image", imageId?.ToString());
        node.SetValue("publishDate", date.ToString("yyyy-MM-dd"));
        node.SetValue("author", "DynCMS Team");
        node.SetValue("category", category);
        node.SetValue("tags", JsonSerializer.Serialize(tags));
        node.SetValue("featured", featured ? "true" : "false");
        await SaveAndPublishAsync(node, ct);
    }

    private async Task SaveAndPublishAsync(ContentNode node, CancellationToken ct)
    {
        await content.SaveAsync(node, ct);
        var result = await content.PublishAsync(node.Id, ct: ct);
        if (!result.Success)
            logger.LogWarning("Demo content '{Name}' was saved but not published: {Errors}", node.Name, string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    private static PropertyType Prop(string alias, string name, string editor, string group, bool mandatory = false,
        string? description = null, Dictionary<string, string>? config = null) =>
        StarterSiteSeeder.Prop(alias, name, editor, group, mandatory, description, config);

    /// <summary>A 3:2 gradient with a caption, so the demo has pictures without shipping any files.</summary>
    private static MemoryStream Svg(string text, string from, string to)
    {
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="1200" height="800" viewBox="0 0 1200 800">
              <defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="{from}"/><stop offset="1" stop-color="{to}"/></linearGradient></defs>
              <rect width="1200" height="800" rx="32" fill="url(#g)"/>
              <circle cx="980" cy="180" r="140" fill="rgba(255,255,255,0.12)"/>
              <circle cx="200" cy="650" r="220" fill="rgba(255,255,255,0.08)"/>
              <text x="80" y="700" font-family="Segoe UI, system-ui, sans-serif" font-size="72" font-weight="700" fill="#fff">{System.Net.WebUtility.HtmlEncode(text)}</text>
            </svg>
            """;
        return new MemoryStream(Encoding.UTF8.GetBytes(svg));
    }
}
