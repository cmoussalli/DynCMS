namespace DynCMS.Core;

/// <summary>Configuration for the DynCMS runtime.</summary>
public sealed class DynCmsOptions
{
    /// <summary>Base directory used to resolve relative paths. Defaults to the host content root.</summary>
    public string? BasePath { get; set; }

    /// <summary>
    /// The database configuration file, relative to <see cref="BasePath"/>. When the file is missing the
    /// application shows the <c>/setup</c> page instead of the site; the setup page writes it.
    /// </summary>
    public string DatabaseConfigFile { get; set; } = "dyncms.database.json";

    /// <summary>Folder that stores uploaded media files.</summary>
    public string MediaRootPath { get; set; } = Path.Combine("App_Data", "media");

    /// <summary>Request path under which media files are served.</summary>
    public string MediaRequestPath { get; set; } = "/media";

    /// <summary>
    /// Folder that stores database backups made from the back office (SQLite copies, plus the manifest of
    /// SQL Server backups, which are written by the server itself). Relative to <see cref="BasePath"/>.
    /// </summary>
    public string BackupRootPath { get; set; } = Path.Combine("App_Data", "backups");

    /// <summary>
    /// The language created on first start when the database has none (Settings → Languages). A culture name
    /// such as <c>en</c>, <c>en-US</c> or <c>de-DE</c>. Changing it later has no effect: manage languages in the back office.
    /// </summary>
    public string DefaultCulture { get; set; } = "en";

    /// <summary>Maximum accepted upload size in bytes.</summary>
    public long MaxUploadBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>Largest file or ZIP archive (in bytes) the back office Files tab accepts as an upload.</summary>
    public long MaxFilesUploadBytes { get; set; } = 200L * 1024 * 1024;

    /// <summary>Most bytes a ZIP archive may expand to when extracted in the Files tab (guards against zip bombs).</summary>
    public long MaxZipExtractBytes { get; set; } = 500L * 1024 * 1024;

    /// <summary>File extensions (with leading dot) that may be uploaded to the media library.</summary>
    public HashSet<string> AllowedUploadExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".ico",
        ".pdf", ".txt", ".md", ".csv", ".docx", ".xlsx", ".pptx", ".zip",
        ".mp4", ".webm", ".mp3", ".wav"
    };

    /// <summary>Back-office security, backed by CMouss.IdentityFramework.</summary>
    public DynCmsIdentityOptions Identity { get; set; } = new();

    /// <summary>The management REST API and MCP server used by integrations and AI agents.</summary>
    public Api.DynCmsApiOptions Api { get; set; } = new();

    /// <summary>Visitor analytics: defaults for the back-office settings, session timeout, geolocation cache and map tiles.</summary>
    public Analytics.DynCmsAnalyticsOptions Analytics { get; set; } = new();

    /// <summary>Plugins: Razor class libraries loaded from <c>App_Data/plugins</c> at runtime (folder, upload switch, controllers).</summary>
    public Plugins.DynCmsPluginOptions Plugins { get; set; } = new();

    /// <summary>The in-memory cache the public site is served from (document types, content, languages, media lookups).</summary>
    public DynCmsCacheOptions Cache { get; set; } = new();
}

/// <summary>
/// Settings for the data cache (<see cref="Services.ContentCache"/>). Pages are resolved and rendered from memory;
/// every change made through DynCMS (back office, management API, plugins using the services) refreshes the cache
/// at once, so the defaults need no tuning on a single server.
/// </summary>
public sealed class DynCmsCacheOptions
{
    /// <summary>
    /// Serve document types, content, languages and media lookups from memory. Turn off only to rule the cache out
    /// while diagnosing: every lookup then reads the database again.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Reload the cache when it is older than this, to pick up changes made outside this process: another web server
    /// on the same database, or rows edited by hand. Null (the default) keeps it until something changes.
    /// </summary>
    public TimeSpan? RefreshInterval { get; set; }
}

/// <summary>Settings for the CMouss.IdentityFramework instance that secures the back office.</summary>
public sealed class DynCmsIdentityOptions
{
    /// <summary>AES key used by the identity framework to encrypt user tokens. Change it per environment.</summary>
    public string TokenEncryptionKey { get; set; } = "change-this-dyncms-token-key";

    /// <summary>Username of the administrator account created on first start.</summary>
    public string AdminUserName { get; set; } = "admin";

    /// <summary>Password of the administrator account created on first start.</summary>
    public string AdminPassword { get; set; } = "Admin123!";

    /// <summary>How long a sign-in token stays valid.</summary>
    public int TokenLifetimeDays { get; set; } = 30;

    /// <summary>Allow the same user to be signed in from several browsers at once.</summary>
    public bool AllowMultipleSessions { get; set; } = true;

    /// <summary>Path of the back-office login page, used when an unauthenticated request is challenged.</summary>
    public string LoginPath { get; set; } = "/admin/login";

    /// <summary>
    /// Name of the cookie that carries the identity framework token. Must match the cookie written by
    /// CMouss.IdentityFramework.BlazorUI's CookieAuthService.
    /// </summary>
    public string TokenCookieName { get; set; } = "IDF_AuthToken";
}
