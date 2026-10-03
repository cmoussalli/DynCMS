using System.Reflection;
using DynCMS.Core.Services;

namespace DynCMS.Core;

/// <summary>
/// The version of the running DynCMS application layer and of the database schema it expects. Plugins declare the
/// lowest application version they support (<see cref="Plugins.IDynCmsPlugin.MinimumCmsVersion"/>); the plugin
/// manager refuses to attach one that needs a newer DynCMS than this.
/// </summary>
public static class CmsVersion
{
    /// <summary>
    /// The schema version this build of DynCMS expects. Raise it by one whenever a release changes the database in a
    /// way that older code cannot work with (a new table or column is additive and does not need a bump on its own,
    /// but it is the number plugins and the System page use to tell whether a database was prepared by this build).
    /// </summary>
    public const int DatabaseSchema = 1;

    /// <summary>The application version as shown to people, for example <c>0.1.0</c> (or <c>0.2.0-beta.1</c>).</summary>
    public static string Application { get; } = ReadDisplayVersion();

    /// <summary>The numeric part of <see cref="Application"/>, used for comparisons.</summary>
    public static Version Current { get; } = TryParse(Application, out var parsed) ? parsed : new Version(0, 0, 0);

    /// <summary>
    /// Parses <c>1</c>, <c>1.2</c>, <c>1.2.3</c> or <c>1.2.3.4</c>, optionally with a leading <c>v</c> and a
    /// <c>-prerelease</c> or <c>+build</c> suffix, which is ignored. Missing parts count as zero.
    /// </summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var core = text.Trim().TrimStart('v', 'V');
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0) core = core[..cut];

        var parts = core.Split('.');
        if (parts.Length is < 1 or > 4 || parts.Any(p => !int.TryParse(p, out var n) || n < 0)) return false;

        var numbers = parts.Select(int.Parse).ToArray();
        version = numbers.Length switch
        {
            1 => new Version(numbers[0], 0, 0),
            2 => new Version(numbers[0], numbers[1], 0),
            3 => new Version(numbers[0], numbers[1], numbers[2]),
            _ => new Version(numbers[0], numbers[1], numbers[2], numbers[3])
        };
        return true;
    }

    /// <summary>Whether this site can run a plugin that declares <paramref name="minimumVersion"/>.</summary>
    public static PluginCompatibility CheckPlugin(string? minimumVersion)
    {
        if (string.IsNullOrWhiteSpace(minimumVersion)) return PluginCompatibility.Undeclared;
        if (!TryParse(minimumVersion, out var minimum)) return PluginCompatibility.Invalid;
        return Current >= minimum ? PluginCompatibility.Compatible : PluginCompatibility.NeedsNewerCms;
    }

    private static string ReadDisplayVersion()
    {
        var assembly = typeof(CmsVersion).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
    }
}

public enum PluginCompatibility
{
    Compatible,
    /// <summary>The plugin needs a newer DynCMS than the one running.</summary>
    NeedsNewerCms,
    /// <summary>The plugin does not say which DynCMS version it supports.</summary>
    Undeclared,
    /// <summary>The declared minimum is not a version number.</summary>
    Invalid
}

/// <summary>How the database relates to what the running application expects.</summary>
public enum DatabaseVersionState
{
    /// <summary>The database was prepared by this schema version.</summary>
    Current,
    /// <summary>The database was last prepared by an older schema version; startup brought it up to date.</summary>
    Upgraded,
    /// <summary>The database was prepared by a newer DynCMS than the one running: update the application.</summary>
    NewerThanApplication
}

/// <summary>What the System page and the API report about versions.</summary>
public sealed record SystemVersionInfo(
    string ApplicationVersion,
    int ExpectedSchemaVersion,
    int? DatabaseSchemaVersion,
    string? DatabaseApplicationVersion,
    DatabaseVersionState State,
    string Runtime,
    string OperatingSystem);

/// <summary>The version record kept in the <c>Settings</c> table (key <c>system.version</c>).</summary>
public sealed class DatabaseVersionRecord
{
    public int SchemaVersion { get; set; }

    /// <summary>The application version that last started against this database.</summary>
    public string ApplicationVersion { get; set; } = string.Empty;
}

public interface ISystemVersionService
{
    /// <summary>Compares the stored database version with the schema version of this build and records the result.</summary>
    Task<SystemVersionInfo> EnsureRecordedAsync(CancellationToken ct = default);

    /// <summary>The application and database versions as stored now.</summary>
    Task<SystemVersionInfo> GetAsync(CancellationToken ct = default);
}

internal sealed class SystemVersionService(ISettingsStore settings) : ISystemVersionService
{
    internal const string SettingKey = "system.version";

    public async Task<SystemVersionInfo> EnsureRecordedAsync(CancellationToken ct = default)
    {
        var stored = await settings.GetAsync<DatabaseVersionRecord>(SettingKey, ct);
        var state = stored is null || stored.SchemaVersion == CmsVersion.DatabaseSchema
            ? DatabaseVersionState.Current
            : stored.SchemaVersion < CmsVersion.DatabaseSchema ? DatabaseVersionState.Upgraded : DatabaseVersionState.NewerThanApplication;

        // A database prepared by a newer DynCMS keeps its higher number: we must not claim to have downgraded it.
        if (state != DatabaseVersionState.NewerThanApplication)
        {
            await settings.SetAsync(SettingKey, new DatabaseVersionRecord
            {
                SchemaVersion = CmsVersion.DatabaseSchema,
                ApplicationVersion = CmsVersion.Application
            }, ct);
        }
        var info = await GetAsync(ct);
        return info with { State = state };
    }

    public async Task<SystemVersionInfo> GetAsync(CancellationToken ct = default)
    {
        var stored = await settings.GetAsync<DatabaseVersionRecord>(SettingKey, ct);
        var state = stored is null || stored.SchemaVersion == CmsVersion.DatabaseSchema ? DatabaseVersionState.Current
            : stored.SchemaVersion < CmsVersion.DatabaseSchema ? DatabaseVersionState.Upgraded : DatabaseVersionState.NewerThanApplication;
        return new SystemVersionInfo(
            CmsVersion.Application,
            CmsVersion.DatabaseSchema,
            stored?.SchemaVersion,
            stored?.ApplicationVersion,
            state,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription);
    }
}
