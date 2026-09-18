using Microsoft.Extensions.Logging;
using Synapsys.Connector.Astm;

namespace Synapsys.Connector.Flows;

/// <summary>
/// Mira una transmision entrante y decide el flow: si trae Q es una consulta (devuelve la
/// respuesta a enviar); si trae R son resultados (los guarda y no responde).
/// </summary>
public sealed class TransmissionRouter
{
    private readonly QueryFlow _queryFlow;
    private readonly ResultsFlow _resultsFlow;
    private readonly ILogger<TransmissionRouter> _logger;

    public TransmissionRouter(QueryFlow queryFlow, ResultsFlow resultsFlow, ILogger<TransmissionRouter> logger)
    {
        _queryFlow = queryFlow;
        _resultsFlow = resultsFlow;
        _logger = logger;
    }

    /// <summary>Procesa la transmision. Devuelve registros a enviar de vuelta, o <c>null</c>.</summary>
    public async Task<IReadOnlyList<AstmRecord>?> RouteAsync(
        IReadOnlyList<AstmRecord> incoming,
        AstmSeparators separators,
        CancellationToken cancellationToken)
    {
        if (incoming.Any(record => record.Type == 'Q'))
        {
            return await _queryFlow.HandleAsync(incoming, separators, cancellationToken);
        }

        if (incoming.Any(record => record.Type == 'R'))
        {
            await _resultsFlow.HandleAsync(incoming, separators, cancellationToken);
            return null;
        }

        _logger.LogInformation("Transmision sin Q ni R ({Count} registros): nada que hacer.", incoming.Count);
        return null;
    }
}
