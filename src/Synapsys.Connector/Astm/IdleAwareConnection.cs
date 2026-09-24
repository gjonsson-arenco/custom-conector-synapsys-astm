namespace Synapsys.Connector.Astm;

/// <summary>
/// Deja una sola lectura en curso contra el socket. Asi se puede preguntar si el otro extremo
/// empezo a hablar sin consumir el byte (<see cref="WaitForDataAsync"/>, <see cref="HasData"/>),
/// que es lo que hace falta para mandar algo solo con la linea desocupada.
/// </summary>
/// <remarks>
/// Los timeouts de los canales dejan de cancelar lecturas del socket: se deja de esperar, pero la
/// lectura sigue en curso y el byte que llegue lo recibe la proxima llamada.
/// </remarks>
public sealed class IdleAwareConnection : IAstmConnection
{
    private readonly IAstmConnection _inner;
    private readonly CancellationToken _sessionToken;
    private Task<int>? _pending;

    /// <param name="sessionToken">Corta la lectura en curso: el de la sesion, no el de cada espera.</param>
    public IdleAwareConnection(IAstmConnection inner, CancellationToken sessionToken)
    {
        _inner = inner;
        _sessionToken = sessionToken;
    }

    public string Remote => _inner.Remote;

    /// <summary>Llego un byte (o se cerro la conexion) y todavia nadie lo leyo.</summary>
    public bool HasData => _pending is { IsCompleted: true };

    public async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        var value = await Pending().WaitAsync(cancellationToken);
        _pending = null;
        return value;
    }

    /// <summary>
    /// Espera a que el otro extremo mande algo, como mucho <paramref name="timeout"/>. <c>false</c>
    /// si la linea siguio en silencio. No consume el byte: lo devuelve el proximo <see cref="ReadByteAsync"/>.
    /// </summary>
    public async Task<bool> WaitForDataAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await Pending().WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        _inner.WriteAsync(data, cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private Task<int> Pending() => _pending ??= _inner.ReadByteAsync(_sessionToken).AsTask();
}
