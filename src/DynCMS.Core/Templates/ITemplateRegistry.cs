using System.Collections.Concurrent;
using DynCMS.Core.Models;

namespace DynCMS.Core.Templates;

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
    /// <summary>Replaces the set of stored templates (called after loading from, or writing to, the database).</summary>
    void SetStored(IEnumerable<Template> templates);

    /// <summary>Raised when the stored templates change.</summary>
    event Action? Changed;
}

public sealed class TemplateRegistry : ITemplateRegistry
{
    private readonly ConcurrentDictionary<string, TemplateDefinition> _components = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, Template> _stored = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public IReadOnlyList<TemplateDefinition> Components => _components.Values.OrderBy(t => t.Name).ToList();

    public IReadOnlyList<TemplateDefinition> Stored => _stored.Values.Select(ToDefinition).OrderBy(t => t.Name).ToList();

    public IReadOnlyList<Template> StoredTemplates => _stored.Values.OrderBy(t => t.Name).ToList();

    public IReadOnlyList<TemplateDefinition> All
    {
        get
        {
            var stored = _stored;
            return stored.Values.Select(ToDefinition)
                .Concat(_components.Values.Where(c => !stored.ContainsKey(c.Alias)))
                .OrderBy(t => t.Name)
                .ToList();
        }
    }

    public IReadOnlyList<TemplateDefinition> Pages => All.Where(t => !t.IsPartial).ToList();

    public IReadOnlyList<TemplateDefinition> Partials => All.Where(t => t.IsPartial).ToList();

    public TemplateDefinition? Get(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;
        if (_stored.TryGetValue(alias, out var stored)) return ToDefinition(stored);
        return _components.TryGetValue(alias, out var c) ? c : null;
    }

    public TemplateDefinition? GetComponent(string? alias) =>
        !string.IsNullOrWhiteSpace(alias) && _components.TryGetValue(alias, out var t) ? t : null;

    public Template? GetStored(string? alias) =>
        !string.IsNullOrWhiteSpace(alias) && _stored.TryGetValue(alias, out var t) ? t : null;

    public void Register(TemplateDefinition definition) => _components[definition.Alias] = definition;

    public void SetStored(IEnumerable<Template> templates)
    {
        _stored = templates.ToDictionary(t => t.Alias, t => t, StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke();
    }

    private static TemplateDefinition ToDefinition(Template t) => new(t.Alias, t.Name, null, t.Id, t.Role, t.Description);
}
