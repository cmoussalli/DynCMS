using System.Collections.Concurrent;

namespace DynCMS.Core.Templates;

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

    public void Register(TemplateDefinition definition)
    {
        _components[definition.Alias] = definition;
        Changed?.Invoke();
    }

    public bool Unregister(string alias)
    {
        var removed = _components.TryRemove(alias, out _);
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void SetStored(IEnumerable<Template> templates)
    {
        _stored = templates.ToDictionary(t => t.Alias, t => t, StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke();
    }

    private static TemplateDefinition ToDefinition(Template t) => new(t.Alias, t.Name, null, t.Id, t.Role, t.Description);
}
