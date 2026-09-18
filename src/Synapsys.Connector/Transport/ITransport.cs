using System.Net.Sockets;
using Synapsys.Connector.Astm;

namespace Synapsys.Connector.Transport;

/// <summary>
/// Provee conexiones ASTM ya establecidas, una por vez. En modo Server acepta clientes; en
/// modo Client conecta (y reconecta) contra el extremo remoto. El worker consume esta secuencia.
/// </summary>
public interface ITransport
{
    IAsyncEnumerable<IAstmConnection> ConnectionsAsync(CancellationToken cancellationToken);
}

/// <summary>Conexion TCP vista como flujo de bytes ASTM.</summary>
public sealed class TcpConnection : IAstmConnection
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly byte[] _one = new byte[1];

    public string Remote { get; }

    public TcpConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        Remote = client.Client.RemoteEndPoint?.ToString() ?? "desconocido";
    }

    public async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        var read = await _stream.ReadAsync(_one.AsMemory(0, 1), cancellationToken);
        return read == 0 ? -1 : _one[0];
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        _stream.WriteAsync(data, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _client.Dispose();
    }
}
