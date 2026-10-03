using DynCMS.Core.Services;
using Microsoft.Extensions.Hosting;

namespace DynCMS.Host;

/// <summary>
/// Runs <see cref="IDynCmsRuntime.TryInitializeAsync"/> when the host starts: loads <c>dyncms.database.json</c>,
/// creates the schema and runs the startup tasks. Without the file nothing happens and the setup page takes over.
/// </summary>
internal sealed class DynCmsInitializationService(IDynCmsRuntime runtime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => runtime.TryInitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
