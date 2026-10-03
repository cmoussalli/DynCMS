using System.Text.Json;
using DynCMS.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace DynCMS.Core.Services;

/// <summary>One named value in the <c>Settings</c> table, stored as JSON.</summary>
public class SettingEntry
{
    public const int MaxKeyLength = 100;

    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Site settings that administrators change at runtime (unlike <see cref="DynCmsOptions"/>, which comes from
/// configuration). Each key holds one JSON document; callers cache what they read.
/// </summary>
public interface ISettingsStore
{
    /// <summary>The value stored under <paramref name="key"/>, or null when there is none.</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class;

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>, replacing what was there.</summary>
    Task SetAsync<T>(string key, T value, CancellationToken ct = default) where T : class;

    Task RemoveAsync(string key, CancellationToken ct = default);
}

internal sealed class SettingsStore(IDbContextFactory<DynCmsDbContext> factory) : ISettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entry = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (entry is null || string.IsNullOrWhiteSpace(entry.Value)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(entry.Value, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > SettingEntry.MaxKeyLength) throw new ArgumentException($"Setting keys are at most {SettingEntry.MaxKeyLength} characters.", nameof(key));

        await using var db = await factory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(value, Json);
        var entry = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (entry is null)
        {
            db.Settings.Add(new SettingEntry { Key = key, Value = json, UpdatedAt = DateTime.UtcNow });
        }
        else
        {
            entry.Value = json;
            entry.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Settings.Where(s => s.Key == key).ExecuteDeleteAsync(ct);
    }
}
