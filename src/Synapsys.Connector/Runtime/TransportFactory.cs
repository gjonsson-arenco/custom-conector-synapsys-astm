using Microsoft.Extensions.DependencyInjection;
using Synapsys.Connector.Configuration;
using Synapsys.Connector.Transport;

namespace Synapsys.Connector.Runtime;

/// <summary>
/// Crea un transporte nuevo cada vez que se abre el puerto (Server o Client segun
/// settings/communication.json), de modo que abrir/cerrar/reiniciar arranque siempre con un
/// socket limpio y con la configuracion vigente.
/// </summary>
public sealed class TransportFactory
{
    private readonly IServiceProvider _services;

    public TransportFactory(IServiceProvider services) => _services = services;

    public ITransport Create(TransportOptions options) => options.IsClient
        ? ActivatorUtilities.CreateInstance<SocketClientTransport>(_services, options)
        : ActivatorUtilities.CreateInstance<SocketServerTransport>(_services, options);
}
