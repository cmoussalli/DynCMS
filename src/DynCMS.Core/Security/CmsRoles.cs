namespace DynCMS.Core.Security;

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

/// <summary>Entities and actions registered in the identity framework's entity-action permission model.</summary>
public static class CmsPermissions
{
    public static class Entities
    {
        public const string Content = "Content";
        public const string Media = "Media";
        public const string DocumentTypes = "DocumentType";
        public const string Templates = "Template";
        public const string Dictionary = "Dictionary";
        public static readonly (string Id, string Title)[] All =
        [
            (Content, "Content"), (Media, "Media"), (DocumentTypes, "Document type"), (Templates, "Template"), (Dictionary, "Dictionary")
        ];
    }

    public static class Actions
    {
        // Create, Update and Delete already exist in the framework's master data; Read and Publish are added by DynCMS.
        public const string Read = "Read";
        public const string Create = "Create";
        public const string Update = "Update";
        public const string Delete = "Delete";
        public const string Publish = "Publish";
        public static readonly (string Id, string Title)[] All =
        [
            (Read, "Read"), (Create, "Create"), (Update, "Update"), (Delete, "Delete"), (Publish, "Publish")
        ];
    }
}

/// <summary>Constants for the ASP.NET Core authentication scheme that validates identity framework tokens.</summary>
public static class DynCmsAuthDefaults
{
    public const string Scheme = "DynCmsIdentity";
}
