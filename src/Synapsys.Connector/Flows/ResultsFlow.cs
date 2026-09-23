using Microsoft.Extensions.Logging;
using Synapsys.Connector.Astm;
using Synapsys.Connector.Lis;
using Synapsys.Connector.Microbiology;

namespace Synapsys.Connector.Flows;

/// <summary>
/// Resultados: Synapsys manda H/P/O/R/L. Los resultados simples se guardan por tubo tal cual; los
/// de cultivo (estado GND y aislados) se acumulan en el <see cref="CultureStore"/> y al LIS va el
/// informe completo del cultivo. No hay respuesta ASTM: alcanza con los ACK de la recepcion.
/// </summary>
public sealed class ResultsFlow
{
    private readonly ILisGateway _lis;
    private readonly CultureStore _cultures;
    private readonly ILogger<ResultsFlow> _logger;

    public ResultsFlow(ILisGateway lis, CultureStore cultures, ILogger<ResultsFlow> logger)
    {
        _lis = lis;
        _cultures = cultures;
        _logger = logger;
    }

    public async Task HandleAsync(
        IReadOnlyList<AstmRecord> incoming,
        AstmSeparators separators,
        CancellationToken cancellationToken)
    {
        var parsed = ResultsParser.Parse(incoming, separators);

        foreach (var warning in parsed.Warnings)
        {
            _logger.LogWarning("{Warning}", warning);
        }

        foreach (var (barcode, results) in parsed.Results)
        {
            try
            {
                await _lis.SaveResultsAsync(barcode, results, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudieron guardar los resultados del tubo {Barcode}.", barcode);
            }
        }

        var barcodes = parsed.CultureStatuses.Select(status => status.Barcode)
            .Concat(parsed.Isolates.Select(isolate => isolate.Barcode))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var barcode in barcodes)
        {
            await SaveCulturesAsync(barcode, parsed, cancellationToken);
        }
    }

    /// <summary>
    /// Primero se registra lo recibido y despues se informa. Si el LIS falla, el cultivo queda
    /// igual registrado y el proximo mensaje de ese tubo manda el informe completo, con esto incluido.
    /// </summary>
    private async Task SaveCulturesAsync(string barcode, ParsedResults parsed, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        var reports = await _cultures.ApplyAsync(
            barcode,
            parsed.CultureStatuses.Where(status => Same(status.Barcode, barcode)).ToList(),
            parsed.Isolates.Where(isolate => Same(isolate.Barcode, barcode)).ToList(),
            warnings,
            cancellationToken);

        foreach (var warning in warnings)
        {
            _logger.LogWarning("{Warning}", warning);
        }

        foreach (var report in reports)
        {
            try
            {
                await _lis.SaveCultureAsync(report, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo guardar el cultivo {Test} del tubo {Barcode}.", report.TestCode, barcode);
            }
        }
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
