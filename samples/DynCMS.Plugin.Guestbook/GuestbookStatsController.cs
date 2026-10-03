using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DynCMS.Plugin.Guestbook;

/// <summary>
/// An MVC API controller in a plugin. DynCMS registers the plugin assembly as an application part when the plugin
/// starts and removes it when the plugin stops, so the route exists exactly while the plugin runs.
/// </summary>
[ApiController]
[Route("api/plugins/guestbook/stats")]
[AllowAnonymous]
public sealed class GuestbookStatsController(GuestbookService guestbook) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<GuestbookStats>> Get(CancellationToken ct) => await guestbook.GetStatsAsync(ct);
}
