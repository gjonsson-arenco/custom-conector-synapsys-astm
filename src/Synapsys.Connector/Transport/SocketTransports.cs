using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Synapsys.Connector.Astm;
using Synapsys.Connector.Configuration;

namespace Synapsys.Connector.Transport;

/// <summary>Modo Server: escucha en un puerto y entrega cada cliente que se conecta.</summary>
public sealed class SocketServerTransport : ITransport
{
    private readonly TransportOptions _options;
    private readonly ILogger<SocketServerTransport> _logger;

    public SocketServerTransport(TransportOptions options, ILogger<SocketServerTransport> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async IAsyncEnumerable<IAstmConnection> ConnectionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var address = IPAddress.TryParse(_options.Host, out var parsed) ? parsed : IPAddress.Any;
        var listener = new TcpListener(address, _options.Port);
        listener.Start();
        _logger.LogInformation("Escuchando ASTM en {Address}:{Port}.", address, _options.Port);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient? client = null;

                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                catch (SocketException ex)
                {
                    _logger.LogWarning(ex, "Fallo aceptando una conexion. Reintentando.");
                    await Delay(cancellationToken);
                }

                if (client is not null)
                {
                    _logger.LogInformation("Synapsys conectado desde {Remote}.", client.Client.RemoteEndPoint);
                    yield return new TcpConnection(client);
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task Delay(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectSeconds)), cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>Modo Client: conecta contra el extremo remoto y reconecta cuando se cae.</summary>
public sealed class SocketClientTransport : ITransport
{
    private readonly TransportOptions _options;
    private readonly ILogger<SocketClientTransport> _logger;

    public SocketClientTransport(TransportOptions options, ILogger<SocketClientTransport> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async IAsyncEnumerable<IAstmConnection> ConnectionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;

            try
            {
                client = new TcpClient();
                await client.ConnectAsync(_options.Host, _options.Port, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                yield break;
            }
            catch (SocketException ex)
            {
                client?.Dispose();
                _logger.LogWarning("No se pudo conectar a {Host}:{Port} ({Message}). Reintentando.",
                    _options.Host, _options.Port, ex.Message);
                await Delay(cancellationToken);
                continue;
            }

            _logger.LogInformation("Conectado a Synapsys en {Host}:{Port}.", _options.Host, _options.Port);
            yield return new TcpConnection(client);
        }
    }

    private async Task Delay(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectSeconds)), cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
