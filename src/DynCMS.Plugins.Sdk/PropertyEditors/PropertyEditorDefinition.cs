namespace DynCMS.Plugins.PropertyEditors;

public enum ConfigFieldType
{
    Text,
    MultilineText,
    Number,
    Boolean
}

/// <summary>Describes one configurable option of a property editor (shown in the document type editor).</summary>
public sealed record PropertyEditorConfigField(
    string Key,
    string Label,
    string? Description = null,
    ConfigFieldType Type = ConfigFieldType.Text);

/// <summary>
/// Describes a property editor. The UI layer supplies <see cref="ComponentType"/>, a Blazor
/// component that edits a string value; the core stays UI-framework agnostic.
/// </summary>
public sealed class PropertyEditorDefinition
{
    public required string Alias { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string Icon { get; init; } = "edit";
    public Type? ComponentType { get; set; }
    public IReadOnlyList<PropertyEditorConfigField> ConfigFields { get; init; } = [];
}

/// <summary>Aliases of the property editors shipped with DynCMS.</summary>
public static class PropertyEditorAliases
{
    public const string TextBox = "DynCms.TextBox";
    public const string TextArea = "DynCms.TextArea";
    public const string RichText = "DynCms.RichText";
    public const string Numeric = "DynCms.Numeric";
    public const string Toggle = "DynCms.Toggle";
    public const string DatePicker = "DynCms.DatePicker";
    public const string Dropdown = "DynCms.Dropdown";
    public const string MediaPicker = "DynCms.MediaPicker";
    public const string ContentPicker = "DynCms.ContentPicker";
    public const string Tags = "DynCms.Tags";
    public const string ColorPicker = "DynCms.ColorPicker";
}
