using DynCMS.Core.Security;

namespace DynCMS.Core.Api;

/// <summary>One scope an API key can be granted.</summary>
public sealed record ApiScopeDefinition(string Id, string Title, string Description, bool AdminOnly = false);

/// <summary>
/// Scopes narrow what an API key may do. They are checked <em>before</em> the owner's roles and permissions, so a
/// key can only ever be a subset of its owner: an editor's key with <c>*</c> still cannot edit document types.
/// </summary>
public static class ApiScopes
{
    /// <summary>Everything the owner is allowed to do.</summary>
    public const string All = "*";

    public const string ContentRead = "content:read";
    public const string ContentWrite = "content:write";
    public const string ContentPublish = "content:publish";
    public const string ContentDelete = "content:delete";

    public const string MediaRead = "media:read";
    public const string MediaWrite = "media:write";
    public const string MediaDelete = "media:delete";

    public const string DocumentTypesRead = "document-types:read";
    public const string DocumentTypesWrite = "document-types:write";

    public const string TemplatesRead = "templates:read";
    public const string TemplatesWrite = "templates:write";

    public const string DictionaryRead = "dictionary:read";
    public const string DictionaryWrite = "dictionary:write";

    public const string UsersRead = "users:read";
    public const string UsersWrite = "users:write";

    public const string SystemRead = "system:read";
    public const string SystemManage = "system:manage";

    public const string ApiKeysManage = "api-keys:manage";

    public const string AnalyticsRead = "analytics:read";
    public const string AnalyticsManage = "analytics:manage";

    public const string PluginsRead = "plugins:read";
    public const string PluginsManage = "plugins:manage";

    public static readonly IReadOnlyList<ApiScopeDefinition> Definitions =
    [
        new(ContentRead, "Read content", "Browse the content tree, read drafts and published content."),
        new(ContentWrite, "Write content", "Create content, edit names, URL segments, templates and property values, reorder."),
        new(ContentPublish, "Publish content", "Publish and unpublish content."),
        new(ContentDelete, "Delete content", "Delete content and everything below it."),
        new(MediaRead, "Read media", "Browse the media library."),
        new(MediaWrite, "Write media", "Upload files, create folders, rename items."),
        new(MediaDelete, "Delete media", "Delete files and folders."),
        new(DocumentTypesRead, "Read document types", "Read document types and the available property editors."),
        new(DocumentTypesWrite, "Write document types", "Create, change and delete document types and their properties."),
        new(TemplatesRead, "Read templates", "Read templates, including Liquid source."),
        new(TemplatesWrite, "Write templates", "Create, change, validate, preview and delete templates."),
        new(DictionaryRead, "Read dictionary", "Read dictionary items and their translations."),
        new(DictionaryWrite, "Write dictionary", "Create, translate, move and delete dictionary items."),
        new(UsersRead, "Read users", "List back-office users and roles.", AdminOnly: true),
        new(UsersWrite, "Write users", "Create back-office users.", AdminOnly: true),
        new(SystemRead, "Read system", "Database information and the list of backups.", AdminOnly: true),
        new(SystemManage, "Manage system", "Create database backups.", AdminOnly: true),
        new(ApiKeysManage, "Manage API keys", "List and revoke every API key and create new ones.", AdminOnly: true),
        new(AnalyticsRead, "Read analytics", "Visitor reports: page views, visitors, sessions, pages, referrers, countries, browsers and the page view log."),
        new(AnalyticsManage, "Manage analytics", "Change the analytics settings and delete recorded page views.", AdminOnly: true),
        new(PluginsRead, "Read plugins", "List installed plugins with their status, pages, endpoints and menu items.", AdminOnly: true),
        new(PluginsManage, "Manage plugins", "Start, stop, reload, install and uninstall plugins. Installing runs uploaded code.", AdminOnly: true)
    ];

    public static bool IsKnown(string scope) =>
        scope == All || Definitions.Any(d => string.Equals(d.Id, scope, StringComparison.OrdinalIgnoreCase));

    public static bool IsAdminOnly(string scope) =>
        Definitions.FirstOrDefault(d => string.Equals(d.Id, scope, StringComparison.OrdinalIgnoreCase))?.AdminOnly == true;

    /// <summary>The scope that gates <paramref name="action"/> on <paramref name="entity"/> (see <see cref="CmsPermissions"/>).</summary>
    public static string For(string entity, string action)
    {
        return entity switch
        {
            CmsPermissions.Entities.Content => action switch
            {
                CmsPermissions.Actions.Read => ContentRead,
                CmsPermissions.Actions.Publish => ContentPublish,
                CmsPermissions.Actions.Delete => ContentDelete,
                _ => ContentWrite
            },
            CmsPermissions.Entities.Media => action switch
            {
                CmsPermissions.Actions.Read => MediaRead,
                CmsPermissions.Actions.Delete => MediaDelete,
                _ => MediaWrite
            },
            CmsPermissions.Entities.DocumentTypes => action == CmsPermissions.Actions.Read ? DocumentTypesRead : DocumentTypesWrite,
            CmsPermissions.Entities.Templates => action == CmsPermissions.Actions.Read ? TemplatesRead : TemplatesWrite,
            CmsPermissions.Entities.Dictionary => action == CmsPermissions.Actions.Read ? DictionaryRead : DictionaryWrite,
            _ => action == CmsPermissions.Actions.Read ? SystemRead : SystemManage
        };
    }

    /// <summary>True when <paramref name="granted"/> covers <paramref name="required"/>.</summary>
    public static bool Covers(IEnumerable<string> granted, string required) =>
        granted.Any(g => g == All || string.Equals(g, required, StringComparison.OrdinalIgnoreCase));
}
