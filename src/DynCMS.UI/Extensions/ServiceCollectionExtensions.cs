using CMouss.IdentityFramework.BlazorUI;
using DynCMS.Core;
using DynCMS.Core.PropertyEditors;
using DynCMS.Core.Security;
using DynCMS.UI.Admin.PropertyEditors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DynCMS.UI;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the admin UI services, the built-in property editors and the
    /// CMouss.IdentityFramework Blazor UI (cookie token service and identity management screens).
    /// Call after <c>AddDynCms()</c>.
    /// </summary>
    public static IDynCmsBuilder AddDynCmsUI(this IDynCmsBuilder builder)
    {
        builder.Services.TryAddScoped<ToastService>();
        builder.Services.AddCascadingAuthenticationState();

        // CMouss.IdentityFramework.BlazorUI: CookieAuthService + admin parts.
        builder.Services.AddIdentityFrameworkBlazorUI();
        IDFBlazorUIConfig.HomeURL = "/";
        IDFBlazorUIConfig.AuthHomeURL = DynCmsUi.AdminBasePath;
        IDFBlazorUIConfig.LoginRedirectURL = DynCmsUi.LoginPath;
        IDFBlazorUIConfig.AfterLogoutRedirectURL = DynCmsUi.LoginPath;
        IDFBlazorUIAdminConfig.AdminRoleIds = [CmsRoles.Admin];
        IDFBlazorUIAdminConfig.AllowDeleteOperations = true;

        RegisterBuiltInEditors(builder);
        return builder;
    }

    private static void RegisterBuiltInEditors(IDynCmsBuilder builder)
    {
        builder.AddPropertyEditor(new PropertyEditorDefinition
        {
            Alias = PropertyEditorAliases.TextBox, Name = "Textbox", Icon = "type",
            Description = "Single line of text.",
            ComponentType = typeof(TextBoxEditor),
            ConfigFields =
            [
                new("placeholder", "Placeholder"),
                new("maxLength", "Max length", Type: ConfigFieldType.Number)
            ]
        });
        builder.AddPropertyEditor(new PropertyEditorDefinition
        {
            Alias = PropertyEditorAliases.TextArea, Name = "Textarea", Icon = "align-left",
            Description = "Multiple lines of plain text.",
            ComponentType = typeof(TextAreaEditor),
            ConfigFields = [new("rows", "Rows", Type: ConfigFieldType.Number), new("maxLength", "Max length", Type: ConfigFieldType.Number)]
        });
        builder.AddPropertyEditor(new PropertyEditorDefinition
        {
            Alias = PropertyEditorAliases.RichText, Name = "Rich text editor", Icon = "edit",
            Description = "Formatted HTML content with headings, lists, links and images.",
            ComponentType = typeof(RichTextEditor)
        });
        builder.AddPropertyEditor(new PropertyEditorDefinition
        {
            Alias = PropertyEditorAliases.Numeric, Name = "Numeric", Icon = "hash",
            Description = "A number.",
            ComponentType = typeof(NumericEditor),
            ConfigFields =
            [
                new("min", "Minimum", Type: ConfigFieldType.Number),
                new("max", "Maximum", Type: ConfigFieldType.Number),
                new("step", "Step", "Use 0.01 for decimals.", ConfigFieldType.Number)
            ]
        });
        builder.AddPropertyEditor(new PropertyEditorDefinition
        {
            Alias = PropertyEditorAliases.Toggle, Name = "Toggle", Icon = "toggle",
            Description = "A true/false switch.",
            ComponentType = typeof(ToggleEditor),
            ConfigFields = [new("label", "Label", "Text shown next to the switch.")]
        });
        builder.AddPropertyEditor(new PropertyEditorDefinition
        {
            Alias = PropertyEditorAliases.DatePicker, Name = "Date picker", Icon = "calendar",
            Description = "A date, optionally with a time.",
            ComponentType = typeof(DatePickerEditor),
            ConfigFields = [new("includeTime", "Include time", Type: ConfigFieldType.Boolean)]
        });
        builder.AddPropertyEditor(new PropertyEditorDefinition
        {
            Alias = PropertyEditorAliases.Dropdown, Name = "Dropdown", Icon = "list",
            Description = "Choose one option from a list.",
            ComponentType = typeof(DropdownEditor),
            ConfigFields = [new("items", "Options", "One option per line.", ConfigFieldType.MultilineText)]
        });
        builder.AddPropertyEditor(new PropertyEditorDefinition
        {
            Alias = PropertyEditorAliases.MediaPicker, Name = "Media picker", Icon = "image",
            Description = "Pick an image or file from the media library.",
            ComponentType = typeof(MediaPickerEditor),
            ConfigFields = [new("imagesOnly", "Images only", Type: ConfigFieldType.Boolean)]
        });
        builder.AddPropertyEditor(new PropertyEditorDefinition
        {
            Alias = PropertyEditorAliases.ContentPicker, Name = "Content picker", Icon = "link",
            Description = "Link to another content item.",
            ComponentType = typeof(ContentPickerEditor)
        });
        builder.AddPropertyEditor(new PropertyEditorDefinition
        {
            Alias = PropertyEditorAliases.Tags, Name = "Tags", Icon = "tag",
            Description = "A list of keywords.",
            ComponentType = typeof(TagsEditor)
        });
        builder.AddPropertyEditor(new PropertyEditorDefinition
        {
            Alias = PropertyEditorAliases.ColorPicker, Name = "Color picker", Icon = "droplet",
            Description = "A hex color value.",
            ComponentType = typeof(ColorPickerEditor)
        });
    }
}
