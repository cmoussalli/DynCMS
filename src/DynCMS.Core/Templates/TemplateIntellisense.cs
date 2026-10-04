using DynCMS.Plugins.Models;
using DynCMS.Core.PropertyEditors;

namespace DynCMS.Core.Templates;

/// <summary>
/// One entry in the editor's completion popup. <paramref name="Kind"/> decides where it is offered
/// (variable, property, filter, tag, snippet, partial) and how it is badged.
/// </summary>
public sealed record TemplateCompletion(string Label, string Kind, string Insert, string? Detail = null);

/// <summary>A property of a document type, as the editor's "Properties" list shows it.</summary>
public sealed record TemplateProperty(string Alias, string Name, string Editor, string Insert, bool Mandatory, string OwnerName);

/// <summary>
/// What the template editor suggests while you type: the variables and filters the Liquid engine exposes,
/// the Liquid tags, the properties of the document types that use the template and the partials that exist.
/// </summary>
public static class TemplateIntellisense
{
    public static IReadOnlyList<TemplateCompletion> Variables { get; } =
    [
        new("content", "variable", "content", "The page being rendered"),
        new("content.name", "variable", "content.name", "Name of the page"),
        new("content.url", "variable", "content.url", "Path to the page"),
        new("content.id", "variable", "content.id", "Unique id"),
        new("content.children", "variable", "content.children", "Pages below this one"),
        new("content.parent", "variable", "content.parent", "The page above"),
        new("content.ancestors", "variable", "content.ancestors", "From the root down"),
        new("content.siblings", "variable", "content.siblings", "Pages next to this one"),
        new("content.content_type", "variable", "content.content_type", "Document type alias"),
        new("content.content_type_name", "variable", "content.content_type_name", "Document type name"),
        new("content.level", "variable", "content.level", "Depth in the tree, 1 at the root"),
        new("content.sort_order", "variable", "content.sort_order", "Position among siblings"),
        new("content.published_at", "variable", "content.published_at", "When it was last published"),
        new("content.updated_at", "variable", "content.updated_at", "When it was last saved"),
        new("content.values", "variable", "content.values", "Every property value by alias"),
        new("content.culture", "variable", "content.culture", "Language of the page, e.g. de-DE"),
        new("content.cultures", "variable", "content.cultures", "This page in every language: iso_code, name, url, is_current"),
        new("site.root", "variable", "site.root", "The home page"),
        new("site.culture", "variable", "site.culture", "The language being served"),
        new("site.languages", "variable", "site.languages", "The home page in every language, for a switcher"),
        new("site.dictionary", "variable", "site.dictionary", "Every dictionary text in the page's language, by key"),
        new("preview", "variable", "preview", "True while a draft is shown"),
        new("request.path", "variable", "request.path", "The path being requested, e.g. /blog"),
        new("request.url", "variable", "request.url", "Path and query string, e.g. /blog?page=2"),
        new("request.query", "variable", "request.query", "The query string as a hash: request.query.page"),
        new("request.query.page", "variable", "request.query.page", "The ?page= number, what paginate reads")
    ];

    public static IReadOnlyList<TemplateCompletion> Filters { get; } =
    [
        new("raw", "filter", "raw", "Output HTML unescaped — needed for rich text"),
        new("media", "filter", "media", "Media picker value → url, name, is_image…"),
        new("media_url", "filter", "media_url", "Media picker value → URL"),
        new("content_by_id", "filter", "content_by_id", "Content picker value → page"),
        new("content_url", "filter", "content_url", "Content picker value → URL"),
        new("content_of_type", "filter", "content_of_type", "'article' | content_of_type"),
        new("children_of", "filter", "children_of", "Children of a page or id"),
        new("tags", "filter", "tags", "Parse the JSON list of the tags editor"),
        new("json", "filter", "json", "Parse a JSON value"),
        new("dictionary", "filter", "dictionary", "'blog.readMore' | dictionary — text from Settings → Dictionary in the page's language"),
        new("paginate", "filter", "paginate: 10", "One page of a list (?page=): items, page, page_count, total, pages, previous_url, next_url"),
        new("page_url", "filter", "page_url", "3 | page_url — the URL of page 3 on this path"),
        new("date", "filter", "date: \"%d %B %Y\"", "Format a date"),
        new("default", "filter", "default: \"—\"", "Fallback when the value is empty"),
        new("escape", "filter", "escape", "HTML-encode"),
        new("strip_html", "filter", "strip_html", "Remove all tags"),
        new("truncate", "filter", "truncate: 120", "Shorten to a number of characters"),
        new("truncatewords", "filter", "truncatewords: 25", "Shorten to a number of words"),
        new("upcase", "filter", "upcase", "UPPER CASE"),
        new("downcase", "filter", "downcase", "lower case"),
        new("capitalize", "filter", "capitalize", "First letter upper case"),
        new("size", "filter", "size", "Length of a list or string"),
        new("first", "filter", "first", "First item"),
        new("last", "filter", "last", "Last item"),
        new("join", "filter", "join: \", \"", "Join a list into text"),
        new("split", "filter", "split: \",\"", "Split text into a list"),
        new("sort", "filter", "sort", "Sort a list"),
        new("reverse", "filter", "reverse", "Reverse a list"),
        new("uniq", "filter", "uniq", "Remove duplicates"),
        new("where", "filter", "where: \"alias\", \"value\"", "Keep matching items"),
        new("map", "filter", "map: \"name\"", "Take one member of every item"),
        new("slice", "filter", "slice: 0, 3", "Part of a list or string"),
        new("append", "filter", "append: \"\"", "Add text at the end"),
        new("prepend", "filter", "prepend: \"\"", "Add text at the start"),
        new("replace", "filter", "replace: \"a\", \"b\"", "Replace text"),
        new("plus", "filter", "plus: 1", "Add"),
        new("minus", "filter", "minus: 1", "Subtract"),
        new("times", "filter", "times: 2", "Multiply"),
        new("divided_by", "filter", "divided_by: 2", "Divide")
    ];

    public static IReadOnlyList<TemplateCompletion> Tags { get; } =
    [
        new("if", "tag", "if ", "Render when a condition holds"),
        new("elsif", "tag", "elsif ", "Another condition"),
        new("else", "tag", "else ", "Otherwise"),
        new("endif", "tag", "endif ", "Close an if"),
        new("unless", "tag", "unless ", "Render when a condition does not hold"),
        new("endunless", "tag", "endunless ", "Close an unless"),
        new("for", "tag", "for item in ", "Loop over a list"),
        new("endfor", "tag", "endfor ", "Close a for"),
        new("case", "tag", "case ", "Branch on a value"),
        new("when", "tag", "when ", "One branch of a case"),
        new("endcase", "tag", "endcase ", "Close a case"),
        new("assign", "tag", "assign name = ", "Store a value in a variable"),
        new("capture", "tag", "capture name ", "Store rendered markup in a variable"),
        new("endcapture", "tag", "endcapture ", "Close a capture"),
        new("render", "tag", "render '", "Render a partial"),
        new("include", "tag", "include '", "Render a partial in the current scope"),
        new("comment", "tag", "comment ", "Start a comment"),
        new("endcomment", "tag", "endcomment ", "Close a comment"),
        new("break", "tag", "break ", "Leave the loop"),
        new("continue", "tag", "continue ", "Next item"),
        new("cycle", "tag", "cycle ", "Alternate between values"),
        new("increment", "tag", "increment ", "Counter up"),
        new("decrement", "tag", "decrement ", "Counter down")
    ];

    /// <summary>Whole constructs, offered as soon as a <c>{</c> is typed and from the Snippets menu.</summary>
    public static IReadOnlyList<TemplateCompletion> Snippets { get; } =
    [
        new("{{ value }}", "snippet", "{{ $0 }}", "Output a value"),
        new("{{ value | raw }}", "snippet", "{{ $0 | raw }}", "Output HTML unescaped"),
        new("{% if %}", "snippet", "{% if $0 %}\n\n{% endif %}", "Condition"),
        new("{% if / else %}", "snippet", "{% if $0 %}\n\n{% else %}\n\n{% endif %}", "Condition with a fallback"),
        new("{% unless %}", "snippet", "{% unless $0 %}\n\n{% endunless %}", "Inverted condition"),
        new("{% for %}", "snippet", "{% for item in content.children %}\n  $0\n{% endfor %}", "Loop over the children"),
        new("{% for / empty %}", "snippet", "{% for item in content.children %}\n  $0\n{% else %}\n  <p>Nothing here yet.</p>\n{% endfor %}", "Loop with an empty state"),
        new("{% assign %}", "snippet", "{% assign name = $0 %}", "Store a value"),
        new("{% capture %}", "snippet", "{% capture name %}$0{% endcapture %}", "Store markup"),
        new("{% render 'partial' %}", "snippet", "{% render '$0' %}", "Render a partial"),
        new("{% render with item %}", "snippet", "{% render '$0', content: item %}", "Render a partial for one item"),
        new("{% case %}", "snippet", "{% case $0 %}\n  {% when 'a' %}\n  {% else %}\n{% endcase %}", "Branch on a value"),
        new("{% comment %}", "snippet", "{% comment %}$0{% endcomment %}", "Comment"),
        new("Image from a media picker", "snippet", "{%- assign image = content.$0 | media %}\n{%- if image %}\n<img src=\"{{ image.url }}\" alt=\"{{ content.name }}\" loading=\"lazy\" />\n{%- endif %}", "Media picker → <img>"),
        new("Link from a content picker", "snippet", "{%- assign target = content.$0 | content_by_id %}\n{%- if target %}\n<a href=\"{{ target.url }}\">{{ target.name }}</a>\n{%- endif %}", "Content picker → <a>"),
        new("Breadcrumbs", "snippet", "{%- if content.ancestors.size > 0 %}\n<nav class=\"breadcrumbs\">\n  {%- for a in content.ancestors %}\n  <a href=\"{{ a.url }}\">{{ a.name }}</a><span>/</span>\n  {%- endfor %}\n  <span class=\"current\">{{ content.name }}</span>\n</nav>\n{%- endif %}", "Trail from the root"),
        new("Child list", "snippet", "{%- if content.children.size > 0 %}\n<ul>\n  {%- for child in content.children %}\n  <li><a href=\"{{ child.url }}\">{{ child.name }}</a></li>\n  {%- endfor %}\n</ul>\n{%- endif %}", "Links to the pages below"),
        new("Language switcher", "snippet", "{%- if content.cultures.size > 1 %}\n<nav class=\"languages\" aria-label=\"Languages\">\n  {%- for lang in content.cultures %}\n  <a href=\"{{ lang.url }}\" hreflang=\"{{ lang.iso_code }}\"{% if lang.is_current %} class=\"current\" aria-current=\"page\"{% endif %}>{{ lang.name }}</a>\n  {%- endfor %}\n</nav>\n{%- endif %}", "Links to this page in its other languages"),
        new("Dictionary text", "snippet", "{{ '$0' | dictionary }}", "A translated label from Settings → Dictionary"),
        new("Articles of a type", "snippet", "{%- assign items = '$0' | content_of_type %}\n{%- for item in items %}\n<a href=\"{{ item.url }}\">{{ item.name }}</a>\n{%- endfor %}", "Every published page of a document type"),
        new("Paged list", "snippet", "{%- assign paged = content.children | paginate: $0 %}\n{%- for item in paged.items %}\n<a href=\"{{ item.url }}\">{{ item.name }}</a>\n{%- endfor %}\n{%- if paged.page_count > 1 %}\n<nav class=\"pager\" aria-label=\"Pagination\">\n  {%- if paged.has_previous %}<a href=\"{{ paged.previous_url }}\" rel=\"prev\">‹</a>{%- endif %}\n  {%- for p in paged.pages %}\n  <a href=\"{{ p.url }}\"{% if p.is_current %} class=\"active\" aria-current=\"page\"{% endif %}>{{ p.number }}</a>\n  {%- endfor %}\n  {%- if paged.has_next %}<a href=\"{{ paged.next_url }}\" rel=\"next\">›</a>{%- endif %}\n</nav>\n{%- endif %}", "A page of the children with links to the other pages (?page=)")
    ];

    /// <summary>The Liquid keywords offered inside an expression.</summary>
    public static IReadOnlyList<TemplateCompletion> Keywords { get; } =
    [
        new("and", "keyword", "and ", "Both conditions"),
        new("or", "keyword", "or ", "Either condition"),
        new("contains", "keyword", "contains ", "Text or list contains"),
        new("empty", "keyword", "empty", "Nothing in it"),
        new("blank", "keyword", "blank", "Empty or whitespace"),
        new("true", "keyword", "true", ""),
        new("false", "keyword", "false", "")
    ];

    /// <summary>Everything the editor offers for one template, given the document types and partials in play.</summary>
    public static IReadOnlyList<TemplateCompletion> Build(
        IEnumerable<TemplateProperty> properties,
        IEnumerable<TemplateDefinition> partials)
    {
        var items = new List<TemplateCompletion>(Variables);
        items.AddRange(Filters);
        items.AddRange(Tags);
        items.AddRange(Snippets);
        items.AddRange(Keywords);

        foreach (var property in properties)
            items.Add(new TemplateCompletion($"content.{property.Alias}", "property", property.Insert, $"{property.Editor} · {property.OwnerName}"));

        foreach (var partial in partials)
            items.Add(new TemplateCompletion(partial.Alias, "partial", partial.Alias, partial.Name));

        return items;
    }

    /// <summary>The properties of <paramref name="types"/>, de-duplicated by alias, with the Liquid that outputs each.</summary>
    public static IReadOnlyList<TemplateProperty> PropertiesOf(IEnumerable<ContentType> types, IPropertyEditorRegistry? editors = null)
    {
        var result = new List<TemplateProperty>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in types)
        {
            foreach (var property in type.Properties.OrderBy(p => p.SortOrder))
            {
                if (!seen.Add(property.Alias)) continue;
                var editorName = editors?.Get(property.EditorAlias)?.Name ?? Friendly(property.EditorAlias);
                result.Add(new TemplateProperty(property.Alias, property.Name, editorName, Expression(property), property.Mandatory, type.Name));
            }
        }

        return result;
    }

    /// <summary>The Liquid expression that outputs a property, chosen by its property editor.</summary>
    public static string Expression(PropertyType property) => property.EditorAlias switch
    {
        PropertyEditorAliases.RichText => $"content.{property.Alias} | raw",
        PropertyEditorAliases.MediaPicker => $"content.{property.Alias} | media_url",
        PropertyEditorAliases.ContentPicker => $"content.{property.Alias} | content_url",
        PropertyEditorAliases.Tags => $"content.{property.Alias} | tags | join: \", \"",
        PropertyEditorAliases.DatePicker => $"content.{property.Alias} | date: \"%d %B %Y\"",
        _ => $"content.{property.Alias}"
    };

    private static string Friendly(string editorAlias) =>
        editorAlias.Contains('.') ? editorAlias[(editorAlias.LastIndexOf('.') + 1)..] : editorAlias;
}
