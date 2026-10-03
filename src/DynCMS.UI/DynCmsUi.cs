using System.Reflection;

namespace DynCMS.UI;

/// <summary>Marker used to reference the UI assembly (for example for router discovery).</summary>
public static class DynCmsUi
{
    public static Assembly Assembly => typeof(DynCmsUi).Assembly;

    /// <summary>Path of the admin stylesheet (add to the host page or let the admin layout inject it).</summary>
    public const string AdminStylesheet = "_content/DynCMS.UI/dyncms-admin.css";

    public const string AdminBasePath = "/admin";
    public const string LoginPath = "/admin/login";
}
