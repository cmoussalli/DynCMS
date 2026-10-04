using System.Globalization;
using DynCMS.Plugins.Models;
using Microsoft.AspNetCore.Components;

namespace DynCMS.Plugins.PropertyEditors;

/// <summary>
/// Base class for property editor components. An editor receives the stored string value and
/// reports changes through <see cref="ValueChanged"/>. Editor configuration comes from the
/// property definition on the document type.
/// </summary>
public abstract class PropertyEditorBase : ComponentBase
{
    [Parameter] public string? Value { get; set; }
    [Parameter] public EventCallback<string?> ValueChanged { get; set; }
    [Parameter] public PropertyType Property { get; set; } = new();
    [Parameter] public bool Invalid { get; set; }

    protected string InputId => $"dc-prop-{Property.Alias}";

    protected string InputClass => Invalid ? "dc-input dc-invalid" : "dc-input";

    protected string? Config(string key) => Property.GetConfig(key);

    protected int? ConfigInt(string key) =>
        int.TryParse(Config(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    protected double? ConfigDouble(string key) =>
        double.TryParse(Config(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    protected bool ConfigBool(string key) =>
        Config(key)?.Trim().ToLowerInvariant() is "true" or "1" or "on" or "yes";

    protected Task SetValueAsync(string? value)
    {
        if (string.Equals(value, Value, StringComparison.Ordinal)) return Task.CompletedTask;
        Value = value;
        return ValueChanged.InvokeAsync(value);
    }
}
