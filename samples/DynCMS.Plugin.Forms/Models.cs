using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace DynCMS.Plugin.Forms;

/// <summary>The kinds of input a form field can be.</summary>
public enum FieldType
{
    Text,
    Email,
    Phone,
    Number,
    Date,
    TextArea,
    Select,
    Radio,
    Checkbox,
    CheckboxGroup
}

/// <summary>One input of a form. Stored as part of the form (JSON column).</summary>
public sealed class FormField
{
    /// <summary>Key of the value in a submission and the HTML <c>name</c>: letters, digits and underscores.</summary>
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public FieldType Type { get; set; } = FieldType.Text;
    public bool Required { get; set; }
    public string? Placeholder { get; set; }
    public string? HelpText { get; set; }
    /// <summary>Choices of select, radio and checkbox-group fields.</summary>
    public List<string> Options { get; set; } = [];
    /// <summary>Longest accepted text; 0 means the default for the type.</summary>
    public int MaxLength { get; set; }

    public bool HasOptions => Type is FieldType.Select or FieldType.Radio or FieldType.CheckboxGroup;

    public FormField Clone() => new()
    {
        Name = Name, Label = Label, Type = Type, Required = Required, Placeholder = Placeholder, HelpText = HelpText,
        Options = [.. Options], MaxLength = MaxLength
    };
}

public sealed class FormDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    /// <summary>URL-safe key; the partial template is <c>form-{Alias}</c>.</summary>
    public string Alias { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string SubmitLabel { get; set; } = "Send";
    public string SuccessMessage { get; set; } = "Thank you! Your message has been sent.";
    /// <summary>Optional page to go to after a successful submission (a relative path or an absolute URL).</summary>
    public string? RedirectUrl { get; set; }
    /// <summary>A disabled form still renders but says it is closed and accepts nothing.</summary>
    public bool IsEnabled { get; set; } = true;
    public string FieldsJson { get; set; } = "[]";
    /// <summary>Key of one of the built-in <see cref="FormThemes"/>; unknown keys fall back to the default.</summary>
    public string Theme { get; set; } = FormThemes.DefaultKey;
    /// <summary>Optional CSS written by the admin; rendered inside a rule for the form's own id, so it is scoped to this form (CSS nesting).</summary>
    public string? CustomCss { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The partial template alias to use in <c>{% render '…' %}</c>.</summary>
    public string PartialAlias => PartialAliasFor(Alias);

    public static string PartialAliasFor(string alias) => "form-" + alias;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public List<FormField> GetFields()
    {
        try { return JsonSerializer.Deserialize<List<FormField>>(FieldsJson, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public void SetFields(IEnumerable<FormField> fields) => FieldsJson = JsonSerializer.Serialize(fields.ToList(), Json);
}

/// <summary>A built-in look for rendered forms; its CSS lives in <c>wwwroot/forms.css</c> as <c>.dcf-theme-{Key}</c>.</summary>
public sealed record FormTheme(string Key, string Name, string Description, string Accent, string Background, string Text);

public static class FormThemes
{
    public const string DefaultKey = "default";

    public static readonly IReadOnlyList<FormTheme> All =
    [
        new(DefaultKey, "Default", "Plain fields that blend into your page, indigo button.", "#4f46e5", "#ffffff", "#111827"),
        new("clean", "Clean", "Neutral and compact: thin grey lines, black button.", "#111827", "#ffffff", "#111827"),
        new("card", "Card", "A white card with a soft shadow and a full-width button.", "#2563eb", "#ffffff", "#1f2937"),
        new("dark", "Dark", "Light text on a dark panel with a violet accent.", "#8b5cf6", "#111827", "#e5e7eb"),
        new("underline", "Underline", "Material-style inputs with only a bottom line.", "#0d9488", "#ffffff", "#111827"),
        new("soft", "Soft", "Pastel pink panel, rounded inputs and a pill button.", "#ec4899", "#fdf2f8", "#4a044e"),
        new("classic", "Classic", "Serif type, square corners and a framed look.", "#7c2d12", "#fffbeb", "#292524"),
        new("google", "Google Forms", "White card with a purple top bar and underlined inputs.", "#673ab7", "#ffffff", "#202124"),
        new("microsoft", "Microsoft Forms", "Fluent look: grey outlines, blue accent, Segoe UI.", "#0f6cbd", "#ffffff", "#242424"),
        new("bold", "Bold", "Thick outlines, hard shadow and a yellow button.", "#facc15", "#ffffff", "#111827")
    ];

    public static string Normalize(string? key) =>
        All.Any(t => t.Key == key) ? key! : DefaultKey;
}

public sealed class FormSubmission
{
    public int Id { get; set; }
    public Guid FormId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>The page the form was sent from (the Referer), for context.</summary>
    public string? PageUrl { get; set; }
    /// <summary>Field name → value (checkbox groups are joined with ", ").</summary>
    public string DataJson { get; set; } = "{}";

    public Dictionary<string, string> GetData()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(DataJson) ?? []; }
        catch (JsonException) { return []; }
    }
}

public sealed class FormsDbContext(DbContextOptions<FormsDbContext> options) : DbContext(options)
{
    public DbSet<FormDefinition> Forms => Set<FormDefinition>();
    public DbSet<FormSubmission> Submissions => Set<FormSubmission>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FormDefinition>(e =>
        {
            e.ToTable("Forms");
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Alias).HasMaxLength(60).IsRequired();
            e.HasIndex(x => x.Alias).IsUnique();
        });
        modelBuilder.Entity<FormSubmission>(e =>
        {
            e.ToTable("Submissions");
            e.HasIndex(x => new { x.FormId, x.CreatedAt });
        });
    }
}

public sealed class FormsOptions
{
    /// <summary>Submissions one visitor (IP address) may send per minute, over all forms.</summary>
    public int MaxSubmissionsPerMinute { get; set; } = 6;
}
