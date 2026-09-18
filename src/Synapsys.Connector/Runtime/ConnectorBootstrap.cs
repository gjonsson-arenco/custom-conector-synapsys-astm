using Microsoft.Extensions.Hosting;

namespace Synapsys.Connector.Runtime;

/// <summary>
/// Abre el puerto ASTM al iniciar el servicio (comportamiento historico) y lo cierra al apagarse.
/// A partir de ahi el puerto se controla desde el front via <see cref="ConnectorController"/>.
/// </summary>
public sealed class ConnectorBootstrap : IHostedService
{
    private readonly ConnectorController _controller;

    public ConnectorBootstrap(ConnectorController controller) => _controller = controller;

    public Task StartAsync(CancellationToken cancellationToken) => _controller.OpenAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => _controller.CloseAsync();
}
