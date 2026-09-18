using Microsoft.Extensions.Logging;
using Synapsys.Connector.Astm;
using Synapsys.Connector.Lis;

namespace Synapsys.Connector.Flows;

/// <summary>
/// Resultados: Synapsys manda H/P/O/R/L. Agrupamos los R por tubo (segun el O que los precede)
/// y los guardamos en el LIS. No hay respuesta ASTM: alcanza con los ACK de la recepcion.
/// </summary>
public sealed class ResultsFlow
{
    private readonly ILisGateway _lis;
    private readonly ILogger<ResultsFlow> _logger;

    public ResultsFlow(ILisGateway lis, ILogger<ResultsFlow> logger)
    {
        _lis = lis;
        _logger = logger;
    }

    public async Task HandleAsync(
        IReadOnlyList<AstmRecord> incoming,
        AstmSeparators separators,
        CancellationToken cancellationToken)
    {
        var bySample = new Dictionary<string, List<InstrumentResult>>(StringComparer.OrdinalIgnoreCase);
        string? currentBarcode = null;

        foreach (var record in incoming)
        {
            switch (record.Type)
            {
                case 'O':
                    currentBarcode = AstmValues.FirstMeaningful(record.Field(3), separators.Component);
                    if (!string.IsNullOrEmpty(currentBarcode) && !bySample.ContainsKey(currentBarcode))
                    {
                        bySample[currentBarcode] = [];
                    }

                    break;

                case 'R':
                    if (string.IsNullOrEmpty(currentBarcode))
                    {
                        _logger.LogWarning("Resultado sin un O previo con codigo de barras: {Record}", record);
                        break;
                    }

                    var code = AstmValues.FirstMeaningful(record.Field(3), separators.Component);
                    if (string.IsNullOrEmpty(code))
                    {
                        _logger.LogWarning("Resultado sin codigo de prueba: {Record}", record);
                        break;
                    }

                    bySample[currentBarcode].Add(new InstrumentResult(
                        code,
                        record.Field(4).Trim(),
                        Flags(record.Field(7), separators)));

                    break;
            }
        }

        foreach (var (barcode, results) in bySample)
        {
            if (results.Count == 0)
            {
                continue;
            }

            try
            {
                await _lis.SaveResultsAsync(barcode, results, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudieron guardar los resultados del tubo {Barcode}.", barcode);
            }
        }
    }

    private static IReadOnlyList<string> Flags(string field, AstmSeparators separators)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return [];
        }

        return field
            .Split([separators.Component, separators.Repeat])
            .Select(flag => flag.Trim())
            .Where(flag => flag.Length > 0)
            .ToList();
    }
}
