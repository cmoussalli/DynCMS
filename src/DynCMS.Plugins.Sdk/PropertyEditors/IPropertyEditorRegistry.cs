namespace DynCMS.Plugins.PropertyEditors;

public interface IPropertyEditorRegistry
{
    IReadOnlyList<PropertyEditorDefinition> All { get; }
    PropertyEditorDefinition? Get(string alias);
    void Register(PropertyEditorDefinition definition);
    /// <summary>Removes an editor (a plugin that stops removes its own). Returns false when the alias is unknown.</summary>
    bool Unregister(string alias);
}
