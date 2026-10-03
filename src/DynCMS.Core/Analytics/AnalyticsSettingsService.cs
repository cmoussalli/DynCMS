using DynCMS.Core.Data;
using DynCMS.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynCMS.Core.Analytics;

/// <summary>
/// The analytics settings in force. They are read from the <c>Settings</c> table once the database is ready and
/// kept in memory, because the tracker consults them on every page view.
/// </summary>
public interface IAnalyticsSettingsService
{
    /// <summary>The current settings (a snapshot; do not mutate — copy with <see cref="AnalyticsSettings.Clone"/> and save).</summary>
    AnalyticsSettings Current { get; }

    /// <summary>True when the settings come from the database rather than the configured defaults.</summary>
    bool IsCustomized { get; }

    /// <summary>Re-reads the settings from the database (called after initialisation and after a database switch).</summary>
    Task ReloadAsync(CancellationToken ct = default);

    /// <summary>Validates, stores and applies new settings.</summary>
    Task SaveAsync(AnalyticsSettings settings, CancellationToken ct = default);

    /// <summary>Removes the stored settings so the configured defaults apply again.</summary>
    Task ResetAsync(CancellationToken ct = default);

    /// <summary>Raised after <see cref="SaveAsync"/>, <see cref="ResetAsync"/> or <see cref="ReloadAsync"/> changed <see cref="Current"/>.</summary>
    event Action? Changed;
}

internal sealed class AnalyticsSettingsService(
    ISettingsStore store,
    IOptions<DynCmsOptions> options,
    ILogger<AnalyticsSettingsService> logger) : IAnalyticsSettingsService
{
    public const string SettingsKey = "analytics";

    private AnalyticsSettings _current = options.Value.Analytics.Defaults.Clone();
    private bool _customized;

    public AnalyticsSettings Current => _current;
    public bool IsCustomized => _customized;
    public event Action? Changed;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        try
        {
            var stored = await store.GetAsync<AnalyticsSettings>(SettingsKey, ct);
            _current = stored is null ? options.Value.Analytics.Defaults.Clone() : Normalize(stored);
            _customized = stored is not null;
        }
        catch (DynCmsNotConfiguredException)
        {
            _current = options.Value.Analytics.Defaults.Clone();
            _customized = false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Analytics settings could not be read; using the configured defaults");
            _current = options.Value.Analytics.Defaults.Clone();
            _customized = false;
        }
        Changed?.Invoke();
    }

    public async Task SaveAsync(AnalyticsSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = Normalize(settings.Clone());
        if (normalized.GeoLookupEnabled)
        {
            if (!normalized.GeoLookupUrl.Contains("{ip}", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The geolocation service URL must contain {ip}, which is replaced by the visitor's address.");
            var probe = normalized.GeoLookupUrl.Replace("{ip}", "1.1.1.1", StringComparison.OrdinalIgnoreCase);
            if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("The geolocation service URL must be an absolute http(s) URL.");
        }

        await store.SetAsync(SettingsKey, normalized, ct);
        _current = normalized;
        _customized = true;
        Changed?.Invoke();
    }

    public async Task ResetAsync(CancellationToken ct = default)
    {
        await store.RemoveAsync(SettingsKey, ct);
        _current = options.Value.Analytics.Defaults.Clone();
        _customized = false;
        Changed?.Invoke();
    }

    private static AnalyticsSettings Normalize(AnalyticsSettings s)
    {
        s.RetentionDays = Math.Clamp(s.RetentionDays, 0, 36_500);
        s.GeoLookupUrl = (s.GeoLookupUrl ?? string.Empty).Trim();
        s.ExcludedPaths = (s.ExcludedPaths ?? [])
            .Select(p => (p ?? string.Empty).Trim())
            .Where(p => p.Length > 0)
            .Select(p => p.StartsWith('/') ? p : "/" + p)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return s;
    }
}
