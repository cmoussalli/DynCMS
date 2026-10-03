namespace DynCMS.Plugins;

/// <summary>
/// Role ids used by the back office. They are CMouss.IdentityFramework role ids (the framework uses the
/// role title as its id) and appear as role claims on the signed-in principal.
/// </summary>
public static class CmsRoles
{
    /// <summary>The framework's administrator role, created with its master data.</summary>
    public const string Admin = "Administrators";
    /// <summary>Created by DynCMS: may create, edit and publish content and media.</summary>
    public const string Editor = "Editors";
    public static readonly string[] All = [Admin, Editor];
}
