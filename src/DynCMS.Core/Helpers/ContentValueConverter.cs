using System.Globalization;
using System.Text.Json;

namespace DynCMS.Core.Helpers;

/// <summary>Converts the string values stored by property editors to typed values.</summary>
public static class ContentValueConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static T? Convert<T>(string? raw, T? fallback = default)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        try
        {
            object? result = target switch
            {
                _ when target == typeof(string) => raw,
                _ when target == typeof(bool) => raw.Trim().ToLowerInvariant() is "true" or "1" or "on" or "yes",
                _ when target == typeof(int) => int.Parse(raw, CultureInfo.InvariantCulture),
                _ when target == typeof(long) => long.Parse(raw, CultureInfo.InvariantCulture),
                _ when target == typeof(double) => double.Parse(raw, CultureInfo.InvariantCulture),
                _ when target == typeof(decimal) => decimal.Parse(raw, CultureInfo.InvariantCulture),
                _ when target == typeof(float) => float.Parse(raw, CultureInfo.InvariantCulture),
                _ when target == typeof(Guid) => Guid.Parse(raw),
                _ when target == typeof(DateTime) => DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                _ when target == typeof(DateOnly) => DateOnly.Parse(raw.Length >= 10 ? raw[..10] : raw, CultureInfo.InvariantCulture),
                _ when target == typeof(DateTimeOffset) => DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture),
                _ when target.IsEnum => Enum.Parse(target, raw, ignoreCase: true),
                _ => JsonSerializer.Deserialize(raw, target, JsonOptions)
            };
            return result is T typed ? typed : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public static string? ToStorage<T>(T? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        DateTime d => d.ToString("o", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value, JsonOptions)
    };
}
