using DynCMS.Core.Plugins;

namespace DynCMS.Core.Api;

public sealed partial class CmsManagement
{
    #region Plugins

    public IReadOnlyList<PluginDto> GetPlugins()
    {
        access.RequireAdmin(ApiScopes.PluginsRead);
        return plugins.Plugins.Select(MapPlugin).ToList();
    }

    public PluginDto GetPlugin(string id)
    {
        access.RequireAdmin(ApiScopes.PluginsRead);
        return MapPlugin(plugins.Find(id) ?? throw CmsApiException.NotFound($"Plugin '{id}'"));
    }

    public async Task<PluginDto> StartPluginAsync(string id, CancellationToken ct)
    {
        access.RequireAdmin(ApiScopes.PluginsManage);
        RequirePlugins();
        return MapPlugin(await Guard(() => plugins.StartAsync(id, ct)));
    }

    public async Task<PluginDto> StopPluginAsync(string id, CancellationToken ct)
    {
        access.RequireAdmin(ApiScopes.PluginsManage);
        RequirePlugins();
        return MapPlugin(await Guard(() => plugins.StopAsync(id, ct)));
    }

    public async Task<PluginDto> ReloadPluginAsync(string id, CancellationToken ct)
    {
        access.RequireAdmin(ApiScopes.PluginsManage);
        RequirePlugins();
        return MapPlugin(await Guard(() => plugins.ReloadAsync(id, ct)));
    }

    public async Task<PluginDto> InstallPluginAsync(string fileName, Stream package, CancellationToken ct)
    {
        access.RequireAdmin(ApiScopes.PluginsManage);
        RequirePlugins();
        if (!plugins.AllowUpload) throw CmsApiException.Forbidden("Plugin upload is turned off (DynCms:Plugins:AllowUpload).");
        if (string.IsNullOrWhiteSpace(fileName)) throw CmsApiException.BadRequest("fileName is required.");
        return MapPlugin(await Guard(() => plugins.InstallAsync(package, fileName, ct)));
    }

    public async Task UninstallPluginAsync(string id, bool deleteData, CancellationToken ct)
    {
        access.RequireAdmin(ApiScopes.PluginsManage);
        RequirePlugins();
        await Guard(() => plugins.UninstallAsync(id, deleteData, ct));
    }

    private void RequirePlugins()
    {
        if (!plugins.IsEnabled) throw new CmsApiException(503, "Plugins are turned off (DynCms:Plugins:Enabled).");
    }

    private static PluginDto MapPlugin(PluginInfo p) => new(
        p.Id, p.Name, p.Description, p.Version, p.Author,
        p.Status.ToString().ToLowerInvariant(), p.Enabled, p.Error, p.AssemblyName, p.InstalledAt, p.StartedAt,
        p.Pages.Where(x => x.IsAdmin).Select(x => x.Template).ToList(),
        p.Pages.Where(x => !x.IsAdmin).Select(x => x.Template).ToList(),
        p.MenuItems.Select(m => new PluginMenuItemDto(m.Title, m.Url, m.Icon, m.Placement.ToString().ToLowerInvariant(), m.Roles)).ToList(),
        p.EndpointCount, p.ServiceCount, p.HasControllers, p.HasStaticAssets, p.DataSizeBytes,
        p.MinimumCmsVersion, char.ToLowerInvariant(p.Compatibility.ToString()[0]) + p.Compatibility.ToString()[1..]);

    #endregion
}
