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

    /// <summary>
    /// Guarda el informe completo de un cultivo (estado + aislados) en la prueba del cultivo.
    /// Es una foto: reemplaza lo que hubiera cargado.
    /// </summary>
    Task SaveCultureAsync(CultureReport culture, CancellationToken cancellationToken);
}

/// <summary>Pruebas a realizar sobre un tubo, en codigos del instrumento.</summary>
public sealed record SampleOrders(string Barcode, IReadOnlyList<string> InstrumentCodes);

/// <summary>Un resultado tal como lo informo el instrumento, antes de mapear al LIS.</summary>
public sealed record InstrumentResult(string InstrumentCode, string Value, IReadOnlyList<string> Flags);

/// <summary>
/// Todo lo que se sabe de un cultivo de un tubo, en codigos del instrumento. Lo arma el
/// <see cref="Microbiology.CultureStore"/> juntando lo que Synapsys manda por partes.
/// </summary>
/// <param name="TestCode">Codigo del estudio del cultivo en el instrumento (GC), el mismo del O del GND.</param>
/// <param name="StatusCode">Codigo del estado del desarrollo (C3, NEGB...). Null si todavia no llego.</param>
public sealed record CultureReport(string Barcode, string TestCode, string? StatusCode, IReadOnlyList<IsolateReport> Isolates);

/// <summary>Un aislado: microorganismo, mecanismos de resistencia y antibiograma, en codigos del instrumento.</summary>
public sealed record IsolateReport(
    int Number,
    string OrganismCode,
    IReadOnlyList<string> Mechanisms,
    IReadOnlyList<Susceptibility> Antibiogram);

/// <summary>Un renglon del antibiograma.</summary>
/// <param name="Interpretation">S, I o R.</param>
public sealed record Susceptibility(string AntibioticCode, string Interpretation, string? Mic);
