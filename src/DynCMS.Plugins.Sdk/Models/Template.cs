namespace DynCMS.Plugins.Models;

/// <summary>What a stored template is for: a whole page, or a fragment rendered by another template.</summary>
public enum TemplateRole
{
    /// <summary>Renders a complete page. Selectable on document types and content.</summary>
    Page = 0,

    /// <summary>
    /// A reusable fragment (partial view) rendered from another template with
    /// <c>{% render 'alias' %}</c>. Never offered as a page template.
    /// </summary>
    Partial = 1
}

/// <summary>
/// A template whose markup is stored in the database and edited in the back office. Written in Liquid
/// (rendered by Fluid) and selectable on document types and content next to the Blazor component templates
/// registered in code. Partials (<see cref="TemplateRole.Partial"/>) are the same thing, but rendered from
/// another template instead of being assigned to content.
/// </summary>
public class Template
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Alias { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Page template or partial view.</summary>
    public TemplateRole Role { get; set; } = TemplateRole.Page;
    /// <summary>Liquid source.</summary>
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsPartial => Role == TemplateRole.Partial;
}
