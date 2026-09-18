namespace Synapsys.Connector.Lis;

/// <summary>
/// Frontera con el LIS. Los flows ASTM hablan en terminos de codigo de barras y codigos de
/// instrumento; el mapeo a codigos del LIS, el factor de conversion y el HTTP viven detras
/// de esta interfaz.
/// </summary>
public interface ILisGateway
{
    /// <summary>
    /// Ordenes pendientes para un tubo, con los codigos ya traducidos a los que espera el
    /// instrumento. <c>null</c> si el LIS no conoce ese codigo de barras (query negativa).
    /// </summary>
    Task<SampleOrders?> GetOrdersAsync(string barcode, CancellationToken cancellationToken);

    /// <summary>Guarda en el LIS los resultados que mando el instrumento para un tubo.</summary>
    Task SaveResultsAsync(string barcode, IReadOnlyList<InstrumentResult> results, CancellationToken cancellationToken);
}

/// <summary>Pruebas a realizar sobre un tubo, en codigos del instrumento.</summary>
public sealed record SampleOrders(string Barcode, IReadOnlyList<string> InstrumentCodes);

/// <summary>Un resultado tal como lo informo el instrumento, antes de mapear al LIS.</summary>
public sealed record InstrumentResult(string InstrumentCode, string Value, IReadOnlyList<string> Flags);
