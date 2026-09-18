using System.Runtime.InteropServices;
using Synapsys.Connector.Astm;

namespace Synapsys.Connector.Monitoring;

/// <summary>
/// Decora una <see cref="IAstmConnection"/> para publicar en el monitor lo que entra y sale del
/// socket. Los bytes recibidos (byte a byte) se acumulan y se descargan al cerrar un frame
/// (LF/control) o al llegar al limite, para no inundar el stream.
/// </summary>
public sealed class MonitoredConnection : IAstmConnection
{
    private const int FlushThreshold = 512;

    private readonly IAstmConnection _inner;
    private readonly IConnectorMonitor _monitor;
    private readonly List<byte> _rxBuffer = new(256);

    public MonitoredConnection(IAstmConnection inner, IConnectorMonitor monitor)
    {
        _inner = inner;
        _monitor = monitor;
    }

    public string Remote => _inner.Remote;

    public async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        var value = await _inner.ReadByteAsync(cancellationToken);

        if (value < 0)
        {
            FlushRx();
            return value;
        }

        _rxBuffer.Add((byte)value);
        if (IsFrameBoundary((byte)value) || _rxBuffer.Count >= FlushThreshold)
        {
            FlushRx();
        }

        return value;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (!data.IsEmpty)
        {
            _monitor.PublishComms(CommsMessage.Tx(data.Span));
        }

        return _inner.WriteAsync(data, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        FlushRx();
        await _inner.DisposeAsync();
    }

    private void FlushRx()
    {
        if (_rxBuffer.Count == 0)
        {
            return;
        }

        _monitor.PublishComms(CommsMessage.Rx(CollectionsMarshal.AsSpan(_rxBuffer)));
        _rxBuffer.Clear();
    }

    private static bool IsFrameBoundary(byte b) => b is
        ControlChars.LF or ControlChars.ENQ or ControlChars.ACK or ControlChars.NAK or
        ControlChars.EOT or ControlChars.ETX or ControlChars.ETB or ControlChars.FS;
}
