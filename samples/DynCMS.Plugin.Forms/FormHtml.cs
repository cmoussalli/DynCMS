using System.Net;
using System.Text;

namespace DynCMS.Plugin.Forms;

/// <summary>
/// Turns a form definition into the markup of its partial view. The result is stored as a Liquid partial template, so
/// every text that comes from the editor is HTML-encoded and has its braces escaped: a label such as <c>{{ x }}</c>
/// can never become Liquid.
/// </summary>
public static class FormHtml
{
    public const string SubmitPath = "/api/plugins/forms";

    public static string SubmitUrl(string alias) => $"{SubmitPath}/{alias}/submit";

    /// <summary>The markup of the partial; static HTML plus the data attributes <c>wwwroot/forms.js</c> enhances.</summary>
    public static string Render(FormDefinition form)
    {
        var sb = new StringBuilder();
        var id = "dcf-" + form.Alias;
        sb.Append("<div class=\"dcf dcf-theme-").Append(FormThemes.Normalize(form.Theme)).Append("\" id=\"").Append(Enc(id)).Append("\">\n");

        // Admin CSS, wrapped in the form's id so native CSS nesting scopes it to this form. Validated on save; checked again here.
        if (!string.IsNullOrWhiteSpace(form.CustomCss) && CustomCssError(form.CustomCss) is null)
            sb.Append("  <style>#").Append(id).Append(" {\n").Append(form.CustomCss.Trim()).Append("\n}</style>\n");

        if (!form.IsEnabled)
        {
            sb.Append("  <p class=\"dcf-closed\">This form is currently closed.</p>\n</div>\n");
            return sb.ToString();
        }

        sb.Append("  <form class=\"dcf-form\" method=\"post\" action=\"").Append(Enc(SubmitUrl(form.Alias))).Append("\" data-dcforms")
          .Append(" data-success=\"").Append(Enc(form.SuccessMessage)).Append('"');
        if (!string.IsNullOrWhiteSpace(form.RedirectUrl)) sb.Append(" data-redirect=\"").Append(Enc(form.RedirectUrl)).Append('"');
        sb.Append(">\n");

        // Honeypot: invisible to people, tempting to bots.
        sb.Append("    <div class=\"dcf-hp\" aria-hidden=\"true\" style=\"position:absolute;left:-10000px;width:1px;height:1px;overflow:hidden\"><label>Leave this empty <input type=\"text\" name=\"_hp\" tabindex=\"-1\" autocomplete=\"off\"></label></div>\n");

        foreach (var field in form.GetFields()) AppendField(sb, id, field);

        sb.Append("    <p class=\"dcf-status\" role=\"status\" aria-live=\"polite\" hidden></p>\n");
        sb.Append("    <button type=\"submit\" class=\"dcf-submit\">").Append(Enc(form.SubmitLabel)).Append("</button>\n");
        sb.Append("  </form>\n</div>\n");
        return sb.ToString();
    }

    private static void AppendField(StringBuilder sb, string formId, FormField f)
    {
        var inputId = $"{formId}-{f.Name}";
        var required = f.Required ? " required" : string.Empty;
        var star = f.Required ? " <span class=\"dcf-req\" aria-hidden=\"true\">*</span>" : string.Empty;
        var help = string.IsNullOrWhiteSpace(f.HelpText) ? string.Empty : $"<small class=\"dcf-help\" id=\"{Enc(inputId)}-help\">{Enc(f.HelpText)}</small>";
        var described = help.Length > 0 ? $" aria-describedby=\"{Enc(inputId)}-help\"" : string.Empty;
        var placeholder = string.IsNullOrWhiteSpace(f.Placeholder) ? string.Empty : $" placeholder=\"{Enc(f.Placeholder)}\"";
        var maxlength = $" maxlength=\"{MaxLengthOf(f)}\"";

        sb.Append("    <div class=\"dcf-field dcf-").Append(f.Type.ToString().ToLowerInvariant()).Append("\" data-field=\"").Append(Enc(f.Name)).Append("\">\n");
        switch (f.Type)
        {
            case FieldType.Checkbox:
                sb.Append("      <label class=\"dcf-check\"><input type=\"checkbox\" id=\"").Append(Enc(inputId)).Append("\" name=\"").Append(Enc(f.Name)).Append("\" value=\"on\"")
                  .Append(required).Append(described).Append("> <span>").Append(Enc(f.Label)).Append(star).Append("</span></label>\n");
                break;

            case FieldType.Radio:
            case FieldType.CheckboxGroup:
                var kind = f.Type == FieldType.Radio ? "radio" : "checkbox";
                sb.Append("      <fieldset class=\"dcf-group\"").Append(described).Append("><legend>").Append(Enc(f.Label)).Append(star).Append("</legend>\n");
                for (var i = 0; i < f.Options.Count; i++)
                {
                    // A checkbox group is "required" when at least one is ticked, which the server checks; HTML can only require a radio.
                    var req = f.Type == FieldType.Radio && f.Required ? " required" : string.Empty;
                    sb.Append("        <label class=\"dcf-check\"><input type=\"").Append(kind).Append("\" name=\"").Append(Enc(f.Name)).Append("\" value=\"").Append(Enc(f.Options[i])).Append('"')
                      .Append(req).Append("> <span>").Append(Enc(f.Options[i])).Append("</span></label>\n");
                }
                sb.Append("      </fieldset>\n");
                break;

            case FieldType.Select:
                sb.Append("      <label for=\"").Append(Enc(inputId)).Append("\">").Append(Enc(f.Label)).Append(star).Append("</label>\n");
                sb.Append("      <select id=\"").Append(Enc(inputId)).Append("\" name=\"").Append(Enc(f.Name)).Append('"').Append(required).Append(described).Append(">\n");
                sb.Append("        <option value=\"\">").Append(Enc(string.IsNullOrWhiteSpace(f.Placeholder) ? "Choose…" : f.Placeholder)).Append("</option>\n");
                foreach (var o in f.Options) sb.Append("        <option value=\"").Append(Enc(o)).Append("\">").Append(Enc(o)).Append("</option>\n");
                sb.Append("      </select>\n");
                break;

            case FieldType.TextArea:
                sb.Append("      <label for=\"").Append(Enc(inputId)).Append("\">").Append(Enc(f.Label)).Append(star).Append("</label>\n");
                sb.Append("      <textarea id=\"").Append(Enc(inputId)).Append("\" name=\"").Append(Enc(f.Name)).Append("\" rows=\"5\"")
                  .Append(placeholder).Append(maxlength).Append(required).Append(described).Append("></textarea>\n");
                break;

            default:
                var type = f.Type switch
                {
                    FieldType.Email => "email",
                    FieldType.Phone => "tel",
                    FieldType.Number => "number\" step=\"any",
                    FieldType.Date => "date",
                    _ => "text"
                };
                sb.Append("      <label for=\"").Append(Enc(inputId)).Append("\">").Append(Enc(f.Label)).Append(star).Append("</label>\n");
                sb.Append("      <input type=\"").Append(type).Append("\" id=\"").Append(Enc(inputId)).Append("\" name=\"").Append(Enc(f.Name)).Append('"')
                  .Append(f.Type is FieldType.Number or FieldType.Date ? string.Empty : maxlength)
                  .Append(placeholder).Append(required).Append(described).Append(">\n");
                break;
        }
        if (help.Length > 0) sb.Append("      ").Append(help).Append('\n');
        sb.Append("      <small class=\"dcf-error\" hidden></small>\n");
        sb.Append("    </div>\n");
    }

    /// <summary>Why the CSS cannot be used, or null. The partial is Liquid inside HTML, so markup and Liquid openers are refused.</summary>
    public static string? CustomCssError(string css)
    {
        if (css.Length > 20_000) return "The custom CSS is too long (20,000 characters at most).";
        if (css.Contains('<')) return "The custom CSS must not contain HTML tags ('<').";
        if (css.Contains("{{", StringComparison.Ordinal) || css.Contains("{%", StringComparison.Ordinal)) return "The custom CSS must not contain '{{' or '{%' (put a space between nested braces).";
        if (css.Contains("@import", StringComparison.OrdinalIgnoreCase)) return "@import is not allowed in custom CSS.";
        if (css.Count(c => c == '{') != css.Count(c => c == '}')) return "The custom CSS has unbalanced braces.";
        return null;
    }

    public static int MaxLengthOf(FormField f) => f.MaxLength > 0 ? Math.Min(f.MaxLength, 10_000) : f.Type == FieldType.TextArea ? 4000 : 250;

    /// <summary>HTML-encodes and also neutralises Liquid: braces become entities, so no <c>{{</c> or <c>{%</c> survives.</summary>
    public static string Enc(string? text) =>
        WebUtility.HtmlEncode(text ?? string.Empty).Replace("{", "&#123;", StringComparison.Ordinal).Replace("}", "&#125;", StringComparison.Ordinal);
}
