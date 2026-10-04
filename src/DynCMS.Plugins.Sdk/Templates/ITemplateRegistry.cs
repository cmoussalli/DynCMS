using DynCMS.Plugins.Models;

namespace DynCMS.Plugins.Templates;

public enum TemplateKind
{
    /// <summary>A Blazor component registered in code with <c>AddTemplate&lt;T&gt;()</c>.</summary>
    Component,
    /// <summary>A Liquid template stored in the database and edited in the back office.</summary>
    Stored
}

/// <summary>
/// A template maps an alias to what renders content of that template: a Blazor component
/// (<see cref="ComponentType"/>) or a stored Liquid template (<see cref="StoredId"/>).
/// </summary>
public sealed record TemplateDefinition(
    string Alias,
    string Name,
    Type? ComponentType = null,
    Guid? StoredId = null,
    TemplateRole Role = TemplateRole.Page,
    string? Description = null)
{
    public TemplateKind Kind => StoredId is null ? TemplateKind.Component : TemplateKind.Stored;
    public bool IsStored => Kind == TemplateKind.Stored;
    /// <summary>A fragment rendered from another template, not a page template.</summary>
    public bool IsPartial => Role == TemplateRole.Partial;
}

/// <summary>
/// The templates content can be rendered with. Component templates are registered at startup; stored templates
/// are loaded from the database when DynCMS initialises and kept in sync by <c>ITemplateService</c>.
/// A stored template with the same alias as a component template takes precedence, so a template can be
/// overridden from the back office without redeploying.
/// </summary>
public interface ITemplateRegistry
{
    /// <summary>Every template, pages and partials; stored templates shadow component templates with the same alias.</summary>
    IReadOnlyList<TemplateDefinition> All { get; }
    /// <summary>The templates content can be rendered with: <see cref="All"/> without the partials.</summary>
    IReadOnlyList<TemplateDefinition> Pages { get; }
    /// <summary>Reusable fragments, rendered from other templates with <c>{% render 'alias' %}</c>.</summary>
    IReadOnlyList<TemplateDefinition> Partials { get; }
    /// <summary>Templates registered in code.</summary>
    IReadOnlyList<TemplateDefinition> Components { get; }
    /// <summary>Templates stored in the database.</summary>
    IReadOnlyList<TemplateDefinition> Stored { get; }

    TemplateDefinition? Get(string? alias);
    TemplateDefinition? GetComponent(string? alias);
    /// <summary>The stored template (with its Liquid source) for an alias, if one exists.</summary>
    Template? GetStored(string? alias);
    /// <summary>Every stored template with its source, for tooling that inspects them (reference scanning, export).</summary>
    IReadOnlyList<Template> StoredTemplates { get; }

    void Register(TemplateDefinition definition);
    /// <summary>Removes a component template (a plugin that stops removes its own). Returns false when the alias is unknown.</summary>
    bool Unregister(string alias);
    /// <summary>Replaces the set of stored templates (called after loading from, or writing to, the database).</summary>
    void SetStored(IEnumerable<Template> templates);

    /// <summary>Raised when the stored templates change.</summary>
    event Action? Changed;
}
