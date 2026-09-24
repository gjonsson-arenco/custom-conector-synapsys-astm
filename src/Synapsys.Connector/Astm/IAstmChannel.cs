namespace Synapsys.Connector.Astm;

/// <summary>Un socket ya conectado, visto como flujo de bytes. Lo provee el transporte.</summary>
public interface IAstmConnection : IAsyncDisposable
{
    /// <summary>Descripcion del extremo remoto, para el log.</summary>
    string Remote { get; }

    /// <summary>Lee un byte. Devuelve -1 si el otro extremo cerro la conexion.</summary>
    ValueTask<int> ReadByteAsync(CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}

/// <summary>
/// Una conversacion ASTM sobre una conexion. Recibe una transmision completa como registros,
/// o envia registros como una transmision. El nivel (low/high) queda encapsulado aca.
/// </summary>
public interface IAstmChannel
{
    /// <summary>
    /// Espera y devuelve la proxima transmision del otro extremo. <c>null</c> si la conexion
    /// se cerro o la transmision fue invalida.
    /// </summary>
    Task<IReadOnlyList<AstmRecord>?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Envia una transmision y dice como termino.</summary>
    Task<SendOutcome> SendAsync(IReadOnlyList<AstmRecord> records, CancellationToken cancellationToken);
}

/// <summary>Como termino un envio.</summary>
public enum SendOutcome
{
    /// <summary>El otro extremo acepto la transmision completa.</summary>
    Sent,

    /// <summary>
    /// El otro extremo pidio la linea al mismo tiempo (colision de ENQ) y se le cedio. No se
    /// mando nada: el proximo <see cref="IAstmChannel.ReceiveAsync"/> le contesta ACK y recibe lo
    /// suyo; lo nuestro se reintenta despues.
    /// </summary>
    Contention,

    /// <summary>El otro extremo no acepto el establecimiento o algun frame, o no contesto a tiempo.</summary>
    Failed
}
