using System.Text;
using DynCMS.Core.Models;
using DynCMS.Core.PropertyEditors;

namespace DynCMS.Core.Templates;

/// <summary>One of the starting points offered when a template is created.</summary>
public sealed record TemplateStarter(string Key, string Name, string Description, string Icon, TemplateRole Role, string Source);

/// <summary>Starting points for stored templates, used by the back office editor and the demo seeder.</summary>
public static class TemplateSamples
{
    /// <summary>What a new page template starts with in the editor.</summary>
    public const string Starter = """
        {%- comment -%}
          Liquid template. "content" is the page being rendered; property values are reached
          by alias, e.g. {{ content.bodyText | raw }}. Output is HTML-encoded unless you add | raw.
        {%- endcomment -%}
        <article class="site-container page">
          {%- if content.ancestors.size > 0 %}
          <nav class="breadcrumbs" aria-label="Breadcrumb">
            {%- for a in content.ancestors %}
            <a href="{{ a.url }}">{{ a.name }}</a><span>/</span>
            {%- endfor %}
            <span class="current">{{ content.name }}</span>
          </nav>
          {%- endif %}

          <header class="page-head">
            <h1>{{ content.name }}</h1>
          </header>

          {%- assign image = content.image | media %}
          {%- if image %}
          <img class="page-image" src="{{ image.url }}" alt="{{ content.name }}" loading="lazy" />
          {%- endif %}

          <div class="prose">
            {{ content.bodyText | raw }}
          </div>

          {%- if content.children.size > 0 %}
          <nav class="subpages">
            <h2>In this section</h2>
            <ul>
              {%- for child in content.children %}
              <li><a href="{{ child.url }}">{{ child.name }}</a></li>
              {%- endfor %}
            </ul>
          </nav>
          {%- endif %}
        </article>
        """;

    /// <summary>An empty page template with just the outline.</summary>
    public const string BlankPage = """
        <article class="site-container page">
          <h1>{{ content.name }}</h1>

        </article>
        """;

    /// <summary>A partial that renders one item passed to it: {% render 'card', content: item %}.</summary>
    public const string PartialCard = """
        {%- comment -%}
          Partial view. Render it from another template and pass the item it should show:
            {% for child in content.children %}{% render 'card', content: child %}{% endfor %}
          Inside a partial, "content" is whatever the caller passed in.
        {%- endcomment -%}
        <a class="card" href="{{ content.url }}">
          {%- assign image = content.image | media %}
          {%- if image %}
          <img class="card-image" src="{{ image.url }}" alt="{{ content.name }}" loading="lazy" />
          {%- endif %}
          <div class="card-body">
            <h3>{{ content.name }}</h3>
            {%- if content.summary %}
            <p>{{ content.summary }}</p>
            {%- endif %}
          </div>
        </a>
        """;

    /// <summary>A partial with no assumptions about the item it renders.</summary>
    public const string PartialBlank = """
        {%- comment -%}
          Partial view. Render it from another template with {% render 'alias' %}, and pass
          the item it should show with {% render 'alias', content: item %}.
        {%- endcomment -%}
        <div class="partial">
          {{ content.name }}
        </div>
        """;

    /// <summary>A page that lists its children through a partial, twelve to a page.</summary>
    public const string ListPage = """
        {%- comment -%}
          Listing page. The children are shown twelve to a page: "paginate" takes the page number from
          ?page= in the address and gives back the items of that page plus the links to the other pages.
        {%- endcomment -%}
        <section class="site-container page">
          <header class="page-head">
            <h1>{{ content.name }}</h1>
            {%- if content.intro %}
            <p class="lead">{{ content.intro }}</p>
            {%- endif %}
          </header>

          {%- assign paged = content.children | paginate: 12 %}
          {%- if paged.total == 0 %}
          <p class="muted">Nothing here yet.</p>
          {%- else %}
          <div class="cards">
            {%- for item in paged.items %}
            {% render 'card', content: item %}
            {%- endfor %}
          </div>
          {%- if paged.page_count > 1 %}
          <nav class="pager" aria-label="Pagination">
            {%- if paged.has_previous %}
            <a href="{{ paged.previous_url }}" rel="prev">‹</a>
            {%- endif %}
            {%- for p in paged.pages %}
            <a href="{{ p.url }}"{% if p.is_current %} class="active" aria-current="page"{% endif %}>{{ p.number }}</a>
            {%- endfor %}
            {%- if paged.has_next %}
            <a href="{{ paged.next_url }}" rel="next">›</a>
            {%- endif %}
          </nav>
          {%- endif %}
          {%- endif %}
        </section>
        """;

    /// <summary>A navigation partial built from the site root.</summary>
    public const string PartialNavigation = """
        {%- comment -%}
          Site navigation. Renders the children of the home page; {% render 'navigation' %} from any template.
        {%- endcomment -%}
        {%- assign home = site.root %}
        <nav class="site-nav">
          <a class="site-nav-home" href="{{ home.url }}">{{ home.name }}</a>
          <ul>
            {%- for item in home.children %}
            <li><a href="{{ item.url }}">{{ item.name }}</a></li>
            {%- endfor %}
          </ul>
        </nav>
        """;

    /// <summary>The starting points offered when a new template is created.</summary>
    public static IReadOnlyList<TemplateStarter> Starters { get; } =
    [
        new("page", "Page template", "Breadcrumbs, heading, image and body text.", "layout", TemplateRole.Page, Starter),
        new("list", "Listing page", "Heading, intro and the children rendered through the 'card' partial.", "list", TemplateRole.Page, ListPage),
        new("blank", "Blank page", "Just the outer element and the page name.", "file", TemplateRole.Page, BlankPage),
        new("card", "Card partial", "A link with an image, a title and a summary for one item.", "box", TemplateRole.Partial, PartialCard),
        new("navigation", "Navigation partial", "The children of the home page as a menu.", "menu", TemplateRole.Partial, PartialNavigation),
        new("partial", "Blank partial", "An empty fragment to build up yourself.", "layers", TemplateRole.Partial, PartialBlank)
    ];

    public static TemplateStarter? GetStarter(string? key) =>
        Starters.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The source a new template of <paramref name="role"/> starts with.</summary>
    public static string For(TemplateRole role) => role == TemplateRole.Partial ? PartialBlank : Starter;

    // ---- scaffolding from a document type ----------------------------------------------------------

    /// <summary>
    /// Builds a Liquid page template that outputs every property of <paramref name="type"/>, each with the
    /// markup that fits its property editor. Used when a template is created for a document type, and when a
    /// component template is turned into an editable stored template.
    /// </summary>
    public static string Scaffold(ContentType? type, TemplateRole role = TemplateRole.Page)
    {
        if (type is null) return For(role);

        var body = new StringBuilder();
        var properties = type.Properties.OrderBy(p => p.SortOrder).ToList();

        foreach (var group in properties.GroupBy(p => string.IsNullOrWhiteSpace(p.GroupName) ? "Content" : p.GroupName))
        {
            body.Append("\n  ").Append("{%- comment -%} ").Append(group.Key).Append(" {%- endcomment -%}\n");
            foreach (var property in group)
                body.Append(PropertyMarkup(property));
        }

        var inner = body.Length == 0 ? "\n  <p>This document type has no properties yet.</p>\n" : body.ToString();
        var sb = new StringBuilder();

        if (role == TemplateRole.Partial)
        {
            sb.Append("{%- comment -%}\n  Partial for \"").Append(type.Name).Append("\" content. Render it from another template with\n")
              .Append("    {% render '").Append(type.Alias).Append("Card', content: item %}\n{%- endcomment -%}\n")
              .Append("<div class=\"").Append(type.Alias).Append("-card\">\n")
              .Append("  <h3><a href=\"{{ content.url }}\">{{ content.name }}</a></h3>\n")
              .Append(inner)
              .Append("</div>\n");
            return sb.ToString();
        }

        sb.Append("{%- comment -%}\n  Template for \"").Append(type.Name).Append("\" (").Append(type.Alias)
          .Append(") content, scaffolded from its properties.\n  Change anything you like: saving makes it live at once.\n{%- endcomment -%}\n")
          .Append("<article class=\"site-container page ").Append(type.Alias).Append("\">\n")
          .Append("""
                    {%- if content.ancestors.size > 0 %}
                    <nav class="breadcrumbs" aria-label="Breadcrumb">
                      {%- for a in content.ancestors %}
                      <a href="{{ a.url }}">{{ a.name }}</a><span>/</span>
                      {%- endfor %}
                      <span class="current">{{ content.name }}</span>
                    </nav>
                    {%- endif %}

                    <header class="page-head">
                      <h1>{{ content.name }}</h1>
                    </header>

                  """)
          .Append(inner)
          .Append("</article>\n");
        return sb.ToString();
    }

    /// <summary>The Liquid that outputs one property, chosen by its property editor.</summary>
    private static string PropertyMarkup(PropertyType property)
    {
        var alias = property.Alias;
        var label = property.Name;
        var css = ToCssClass(alias);

        return property.EditorAlias switch
        {
            PropertyEditorAliases.RichText =>
                $"  {{%- if content.{alias} %}}\n  <div class=\"prose {css}\">{{{{ content.{alias} | raw }}}}</div>\n  {{%- endif %}}\n",

            PropertyEditorAliases.MediaPicker =>
                $"  {{%- assign {alias}Media = content.{alias} | media %}}\n" +
                $"  {{%- if {alias}Media %}}\n" +
                $"  <img class=\"{css}\" src=\"{{{{ {alias}Media.url }}}}\" alt=\"{{{{ content.name }}}}\" loading=\"lazy\" />\n" +
                $"  {{%- endif %}}\n",

            PropertyEditorAliases.ContentPicker =>
                $"  {{%- assign {alias}Page = content.{alias} | content_by_id %}}\n" +
                $"  {{%- if {alias}Page %}}\n" +
                $"  <p class=\"{css}\"><a href=\"{{{{ {alias}Page.url }}}}\">{{{{ {alias}Page.name }}}}</a></p>\n" +
                $"  {{%- endif %}}\n",

            PropertyEditorAliases.Tags =>
                $"  {{%- assign {alias}List = content.{alias} | tags %}}\n" +
                $"  {{%- if {alias}List.size > 0 %}}\n" +
                $"  <ul class=\"{css}\">{{%- for tag in {alias}List %}}<li>{{{{ tag }}}}</li>{{%- endfor %}}</ul>\n" +
                $"  {{%- endif %}}\n",

            PropertyEditorAliases.DatePicker =>
                $"  {{%- if content.{alias} %}}\n  <time class=\"{css}\">{{{{ content.{alias} | date: \"%d %B %Y\" }}}}</time>\n  {{%- endif %}}\n",

            PropertyEditorAliases.Toggle =>
                $"  {{%- if content.{alias} %}}\n  <span class=\"{css}\">{label}</span>\n  {{%- endif %}}\n",

            PropertyEditorAliases.ColorPicker =>
                $"  {{%- if content.{alias} %}}\n  <span class=\"{css}\" style=\"background:{{{{ content.{alias} }}}}\"></span>\n  {{%- endif %}}\n",

            PropertyEditorAliases.TextArea =>
                $"  {{%- if content.{alias} %}}\n  <p class=\"{css}\">{{{{ content.{alias} }}}}</p>\n  {{%- endif %}}\n",

            _ =>
                $"  {{%- if content.{alias} %}}\n  <p class=\"{css}\">{{{{ content.{alias} }}}}</p>\n  {{%- endif %}}\n"
        };
    }

    private static string ToCssClass(string alias)
    {
        var sb = new StringBuilder(alias.Length + 4);
        foreach (var c in alias)
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append('-');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
