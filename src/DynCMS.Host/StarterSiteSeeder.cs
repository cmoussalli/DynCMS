using DynCMS.Core.Models;
using DynCMS.Core.PropertyEditors;
using DynCMS.Core.Services;
using DynCMS.Core.Templates;
using Microsoft.Extensions.Logging;

namespace DynCMS.Host;

/// <summary>
/// Gives an empty database something to show. What exactly is decided on the <c>/setup</c> page
/// (<see cref="StartupContext.RequestedStarterContent"/>), falling back to <see cref="DynCmsHostOptions.StarterContent"/>:
/// <list type="bullet">
/// <item><see cref="StarterContent.EmptySite"/>: two document types (home, page), the stored Liquid templates and a
/// single published home page. Nothing else; the site is yours to fill.</item>
/// <item><see cref="StarterContent.DemoBlog"/>: the same, plus a blog with paged articles, categories, tags, images
/// and a draft (<see cref="DemoBlogSeeder"/>).</item>
/// </list>
/// Everything it creates is ordinary content, editable in the back office. Skipped when any document type exists;
/// disabled with <see cref="DynCmsHostOptions.SeedStarterSite"/>.
/// </summary>
internal sealed class StarterSiteSeeder(
    IContentTypeService contentTypes,
    IContentService content,
    ITemplateService templates,
    StartupContext startup,
    DynCmsHostOptions options,
    DemoBlogSeeder demoBlog,
    ILogger<StarterSiteSeeder> logger) : IDynCmsStartupTask
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        if ((await contentTypes.GetAllAsync()).Count > 0) return;

        var choice = startup.RequestedStarterContent ?? options.StarterContent;
        logger.LogInformation("Empty database: creating the starter site ({Choice})", choice);

        // ---- Templates (Settings → Templates). Partials first: the pages render them. ----
        await templates.SaveAsync(new Template
        {
            Alias = "card", Name = "Card", Role = TemplateRole.Partial,
            Description = "A link with an image, a title and a summary. Rendered from other templates with {% render 'card', content: item %}.",
            Content = TemplateSamples.PartialCard
        });
        await templates.SaveAsync(new Template
        {
            Alias = "navigation", Name = "Navigation", Role = TemplateRole.Partial,
            Description = "The children of the home page as a menu. Render it with {% render 'navigation' %}.",
            Content = TemplateSamples.PartialNavigation
        });
        await templates.SaveAsync(new Template
        {
            Alias = "page", Name = "Page",
            Description = "Breadcrumbs, heading, image, body text and the pages below.",
            Content = TemplateSamples.Starter
        });
        await templates.SaveAsync(new Template
        {
            Alias = "sectionPage", Name = "Section page",
            Description = "Heading, intro and the pages below it as cards, twelve to a page.",
            Content = TemplateSamples.ListPage
        });
        await templates.SaveAsync(new Template
        {
            Alias = "home", Name = "Home page",
            Description = choice == StarterContent.DemoBlog
                ? "Hero with image, featured and latest articles, body text and the top-level pages as cards."
                : "Hero, body text and the top-level pages as cards.",
            Content = choice == StarterContent.DemoBlog ? DemoBlogTemplates.Home : HomeTemplate
        });

        // ---- Document types (Settings → Document types) ----
        var page = await contentTypes.SaveAsync(new ContentType
        {
            Alias = "page", Name = "Page", Icon = "document",
            Description = "A page with rich text. Can hold more pages.",
            AllowedChildTypeAliases = ["page"],
            AllowedTemplateAliases = ["page", "sectionPage"], DefaultTemplateAlias = "page", SortOrder = 1,
            Properties =
            [
                Prop("summary", "Summary", PropertyEditorAliases.TextArea, "Content", description: "Shown where the page is listed as a card.", config: new() { ["rows"] = "3" }),
                Prop("bodyText", "Body text", PropertyEditorAliases.RichText, "Content", mandatory: true),
                Prop("image", "Image", PropertyEditorAliases.MediaPicker, "Content", config: new() { ["imagesOnly"] = "true" }),
                Prop("intro", "Intro", PropertyEditorAliases.TextArea, "Section", description: "Used by the 'Section page' template.", config: new() { ["rows"] = "3" }),
                Prop("metaDescription", "Meta description", PropertyEditorAliases.TextArea, "SEO", config: new() { ["rows"] = "2", ["maxLength"] = "160" })
            ]
        });

        var home = await contentTypes.SaveAsync(new ContentType
        {
            Alias = "home", Name = "Home", Icon = "home",
            Description = "The site root. Only one is needed.",
            AllowedAsRoot = true,
            AllowedChildTypeAliases = [page.Alias],
            AllowedTemplateAliases = ["home", "page"], DefaultTemplateAlias = "home", SortOrder = 0,
            Properties =
            [
                Prop("heroTitle", "Hero title", PropertyEditorAliases.TextBox, "Hero", config: new() { ["maxLength"] = "80" }),
                Prop("heroText", "Hero text", PropertyEditorAliases.TextArea, "Hero", config: new() { ["rows"] = "3" }),
                Prop("heroImage", "Hero image", PropertyEditorAliases.MediaPicker, "Hero", description: "Optional. Shown next to the hero text.", config: new() { ["imagesOnly"] = "true" }),
                Prop("bodyText", "Body text", PropertyEditorAliases.RichText, "Content"),
                Prop("metaDescription", "Meta description", PropertyEditorAliases.TextArea, "SEO", config: new() { ["rows"] = "2", ["maxLength"] = "160" })
            ]
        });

        if (choice == StarterContent.DemoBlog)
        {
            await demoBlog.SeedAsync(home, page, ct);
            return;
        }

        // ---- Content: the home page and nothing else ----
        var root = await content.CreateAsync(home.Id, null, "My site", ct: ct);
        root.SetValue("heroTitle", "Your site is up");
        root.SetValue("heroText", "This is the only page so far. Sign in to the back office to make it yours.");
        root.SetValue("bodyText",
            "<h2>Where to go next</h2>" +
            "<ul>" +
            "<li><strong>Content</strong> — this page. Edit it, and add pages below it.</li>" +
            "<li><strong>Settings → Document types</strong> — the shape of your content: properties and editors.</li>" +
            "<li><strong>Settings → Templates</strong> — the Liquid markup that renders it. Changes are live at once.</li>" +
            "<li><strong>Media</strong> — images and files for your pages.</li>" +
            "</ul>" +
            "<p>The back office is at <a href=\"/admin\">/admin</a>. The administrator account comes from the <code>DynCms:Identity</code> configuration section.</p>");
        root.SetValue("metaDescription", "A new DynCMS site.");
        await content.SaveAsync(root, ct);
        await content.PublishAsync(root.Id, ct: ct);
    }

    internal static PropertyType Prop(string alias, string name, string editor, string group, bool mandatory = false,
        string? description = null, Dictionary<string, string>? config = null) => new()
    {
        Alias = alias, Name = name, EditorAlias = editor, GroupName = group, Mandatory = mandatory,
        Description = description, Config = config ?? new(StringComparer.OrdinalIgnoreCase)
    };

    /// <summary>The home page template of the empty site. Uses the classes from dyncms-site.css.</summary>
    private const string HomeTemplate = """
        {%- comment -%}
          Home page: a hero, the body text and the top-level pages as cards.
          "content" is the home page; edit its properties under Content, this markup under Settings → Templates.
        {%- endcomment -%}
        {%- assign hero_image = content.heroImage | media %}
        <section class="hero">
          <div class="site-container{% if hero_image %} hero-grid{% endif %}">
            <div>
              <span class="eyebrow">{{ content.name }}</span>
              {%- if content.heroTitle %}
              <h1>{{ content.heroTitle }}</h1>
              {%- else %}
              <h1>{{ content.name }}</h1>
              {%- endif %}
              {%- if content.heroText %}
              <p class="lead">{{ content.heroText }}</p>
              {%- endif %}
              <p><a class="btn btn-primary" href="/admin">Open the back office</a></p>
            </div>
            {%- if hero_image %}
            <img class="hero-image" src="{{ hero_image.url }}" alt="" />
            {%- endif %}
          </div>
        </section>

        <section class="site-container">
          {%- if content.bodyText %}
          <div class="prose">
            {{ content.bodyText | raw }}
          </div>
          {%- endif %}

          {%- if content.children.size > 0 %}
          <div class="section-head">
            <h2>Pages</h2>
          </div>
          <div class="cards">
            {%- for item in content.children %}
            {% render 'card', content: item %}
            {%- endfor %}
          </div>
          {%- endif %}
        </section>
        """;
}
