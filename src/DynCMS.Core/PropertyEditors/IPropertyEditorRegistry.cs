using System.Collections.Concurrent;

namespace DynCMS.Core.PropertyEditors;

public sealed class PropertyEditorRegistry : IPropertyEditorRegistry
{
    private readonly ConcurrentDictionary<string, PropertyEditorDefinition> _editors = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PropertyEditorDefinition> All => _editors.Values.OrderBy(e => e.Name).ToList();

    public PropertyEditorDefinition? Get(string alias) =>
        !string.IsNullOrWhiteSpace(alias) && _editors.TryGetValue(alias, out var d) ? d : null;

    public void Register(PropertyEditorDefinition definition) => _editors[definition.Alias] = definition;

    public bool Unregister(string alias) => _editors.TryRemove(alias, out _);
}
