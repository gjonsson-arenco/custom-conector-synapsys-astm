using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Synapsys.Connector.Configuration;
using Synapsys.Connector.Transport;

namespace Synapsys.Connector.Runtime;

/// <summary>
/// Crea un transporte nuevo cada vez que se abre el puerto (Server o Client segun configuracion),
/// de modo que abrir/cerrar/reiniciar arranque siempre con un socket limpio.
/// </summary>
public sealed class TransportFactory
{
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<TransportOptions> _options;

    public TransportFactory(IServiceProvider services, IOptionsMonitor<TransportOptions> options)
    {
        _services = services;
        _options = options;
    }

    public ITransport Create() => _options.CurrentValue.IsClient
        ? ActivatorUtilities.CreateInstance<SocketClientTransport>(_services)
        : ActivatorUtilities.CreateInstance<SocketServerTransport>(_services);
}
