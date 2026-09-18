using Microsoft.Extensions.Logging;
using Synapsys.Connector.Astm;
using Synapsys.Connector.Lis;

namespace Synapsys.Connector.Flows;

/// <summary>
/// Host query: Synapsys pregunta que hacer con un tubo (registro Q). Consultamos el LIS y
/// respondemos con las ordenes (H/P/O/L), o una query negativa (H/L) si el tubo no existe.
/// </summary>
public sealed class QueryFlow
{
    private readonly ILisGateway _lis;
    private readonly ILogger<QueryFlow> _logger;

    public QueryFlow(ILisGateway lis, ILogger<QueryFlow> logger)
    {
        _lis = lis;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AstmRecord>?> HandleAsync(
        IReadOnlyList<AstmRecord> incoming,
        AstmSeparators separators,
        CancellationToken cancellationToken)
    {
        var query = incoming.FirstOrDefault(record => record.Type == 'Q');
        if (query is null)
        {
            return null;
        }

        var barcode = AstmValues.FirstMeaningful(query.Field(3), separators.Component);
        if (string.IsNullOrEmpty(barcode))
        {
            _logger.LogWarning("Query sin codigo de barras reconocible: {Query}", query);
            return NegativeQuery(separators);
        }

        _logger.LogInformation("Host query para el tubo {Barcode}.", barcode);

        var orders = await _lis.GetOrdersAsync(barcode, cancellationToken);

        if (orders is null || orders.InstrumentCodes.Count == 0)
        {
            _logger.LogInformation("Sin ordenes pendientes para {Barcode}: se responde query negativa.", barcode);
            return NegativeQuery(separators);
        }

        var order = AstmRecord.Create('O', separators)
            .Set(2, "1")
            .Set(3, barcode)
            .Set(5, string.Join(separators.Repeat, orders.InstrumentCodes.Select(code => $"{separators.Component}{separators.Component}{separators.Component}{code}")))
            .Set(6, "R");

        return
        [
            AstmValues.Header(separators),
            AstmRecord.Create('P', separators).Set(2, "1"),
            order,
            AstmValues.Terminator(separators)
        ];
    }

    private static IReadOnlyList<AstmRecord> NegativeQuery(AstmSeparators separators) =>
    [
        AstmValues.Header(separators),
        AstmValues.Terminator(separators)
    ];
}
