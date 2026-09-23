using Synapsys.Connector.Astm;
using Synapsys.Connector.Lis;

namespace Synapsys.Connector.Flows;

/// <summary>
/// Decodifica una transmision de resultados de Synapsys. El R no dice que prueba es sino de que
/// categoria es (componente 4 del campo 3), y el sentido depende del O que lo precede:
/// <list type="bullet">
/// <item><c>O ^^^RTO</c> + <c>R ^^^OTHER</c>: resultado simple de la prueba del O; el valor es un codigo de resultado.</item>
/// <item><c>O ^^^GC</c> + <c>R ^^^GND</c>: estado del cultivo GC (positivo, negativo...).</item>
/// <item><c>O barcode^n^organismo ... ^^^ISOLATE RESULT</c> + <c>R ^^^ID</c> / <c>R ^^^AST^^droga</c>:
/// el aislado n del cultivo que indica el campo 14 del O (GC), con su antibiograma.</item>
/// </list>
/// Un R de otra categoria bajo un O comun se toma como resultado de su propio codigo, que es
/// lo que hace cualquier otro equipo ASTM.
/// </summary>
public static class ResultsParser
{
    public const string IsolateOrder = "ISOLATE RESULT";

    private const string Other = "OTHER";
    private const string Growth = "GND";
    private const string Identification = "ID";
    private const string Antibiogram = "AST";

    public static ParsedResults Parse(IReadOnlyList<AstmRecord> records, AstmSeparators separators)
    {
        var parsed = new ParsedResults();
        OrderContext? order = null;
        IsolateBuilder? isolate = null;

        void CloseIsolate()
        {
            if (isolate is not null)
            {
                parsed.Isolates.Add(isolate.Build());
                isolate = null;
            }
        }

        foreach (var record in records)
        {
            switch (record.Type)
            {
                case 'O':
                    CloseIsolate();
                    order = ReadOrder(record, separators, parsed.Warnings);
                    if (order is { IsIsolate: true })
                    {
                        isolate = order.NewIsolate(parsed.Warnings);
                    }

                    break;

                case 'R':
                    if (order is null)
                    {
                        parsed.Warnings.Add($"Resultado sin un O previo con codigo de barras: {record}");
                        break;
                    }

                    if (order.IsIsolate)
                    {
                        isolate?.Add(record, parsed.Warnings);
                    }
                    else
                    {
                        ReadTestResult(order, record, separators, parsed);
                    }

                    break;

                case 'L':
                    CloseIsolate();
                    order = null;
                    break;
            }
        }

        CloseIsolate();
        return parsed;
    }

    private static OrderContext? ReadOrder(AstmRecord order, AstmSeparators separators, List<string> warnings)
    {
        var barcode = order.Component(3, 1).Trim();
        if (barcode.Length == 0)
        {
            warnings.Add($"Orden sin codigo de barras: {order}");
            return null;
        }

        var test = AstmValues.FirstMeaningful(order.Field(5), separators.Component);

        // Campo 14: el estudio del que sale el aislado. Epicenter lo manda con repeticiones
        // (HCI\PLUSAEF^fecha); Synapsys, solo el codigo (GC).
        var source = order.Field(14).Split(separators.Repeat)[0].Split(separators.Component)[0].Trim();

        return new OrderContext(
            barcode,
            test,
            string.Equals(test, IsolateOrder, StringComparison.OrdinalIgnoreCase),
            order.Component(3, 2).Trim(),
            order.Component(3, 3).Trim(),
            source.Length > 0 ? source : null);
    }

    private static void ReadTestResult(OrderContext order, AstmRecord result, AstmSeparators separators, ParsedResults parsed)
    {
        var category = result.Component(3, 4).Trim();
        var value = result.Field(4).Trim();

        if (string.Equals(category, Growth, StringComparison.OrdinalIgnoreCase))
        {
            var status = AstmValues.FirstMeaningful(value, separators.Component);
            if (status.Length == 0 || order.Test.Length == 0)
            {
                parsed.Warnings.Add($"Estado de cultivo sin valor o sin estudio en el O: {result}");
                return;
            }

            parsed.CultureStatuses.Add(new CultureStatus(order.Barcode, order.Test, status));
            return;
        }

        // OTHER: la prueba es la del O. Cualquier otra categoria: el R trae su propio codigo.
        var code = string.Equals(category, Other, StringComparison.OrdinalIgnoreCase)
            ? order.Test
            : AstmValues.FirstMeaningful(result.Field(3), separators.Component);

        if (code.Length == 0)
        {
            parsed.Warnings.Add($"Resultado sin codigo de prueba: {result}");
            return;
        }

        if (!parsed.Results.TryGetValue(order.Barcode, out var results))
        {
            parsed.Results[order.Barcode] = results = [];
        }

        results.Add(new InstrumentResult(code, value, Flags(result.Field(7), separators)));
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

    private sealed record OrderContext(
        string Barcode,
        string Test,
        bool IsIsolate,
        string IsolateNumber,
        string Organism,
        string? SourceTest)
    {
        public IsolateBuilder? NewIsolate(List<string> warnings)
        {
            if (!int.TryParse(IsolateNumber, out var number) || number <= 0)
            {
                warnings.Add($"Aislado del tubo {Barcode} sin numero valido ('{IsolateNumber}'): se descarta.");
                return null;
            }

            return new IsolateBuilder(Barcode, SourceTest, number, Organism);
        }
    }

    private sealed class IsolateBuilder(string barcode, string? sourceTest, int number, string organism)
    {
        private readonly List<string> _mechanisms = [];
        private readonly Dictionary<string, Susceptibility> _antibiogram = new(StringComparer.OrdinalIgnoreCase);
        private string _organism = organism;

        public void Add(AstmRecord result, List<string> warnings)
        {
            var category = result.Component(3, 4).Trim();

            if (string.Equals(category, Identification, StringComparison.OrdinalIgnoreCase))
            {
                // ^PSEAER / ^EC^^^^^^^MBT_ID: el organismo en el componente 2. Pisa al del O.
                var identified = result.Component(4, 2).Trim();
                if (identified.Length > 0)
                {
                    _organism = identified;
                }

                return;
            }

            if (string.Equals(category, Antibiogram, StringComparison.OrdinalIgnoreCase))
            {
                AddSusceptibility(result, warnings);
                return;
            }

            warnings.Add($"Aislado {number} del tubo {barcode}: categoria '{category}' no soportada, se ignora: {result}");
        }

        public IsolateUpdate Build() =>
            new(barcode, sourceTest, new IsolateReport(number, _organism, _mechanisms, _antibiogram.Values.ToList()));

        /// <summary>
        /// <c>R|n|^^^AST^^ATM^,|^^S^^S^bnf</c>: droga en el componente 6 del codigo. En el valor, la CIM
        /// va en los componentes 1-2, la interpretacion en 3-5 (Synapsys: 3 y 5, Epicenter: 3 y 4) y el
        /// grupo de puntos de corte en el 6. Se toma la ultima interpretacion informada: la final pisa
        /// a la del instrumento.
        /// </summary>
        private void AddSusceptibility(AstmRecord result, List<string> warnings)
        {
            var antibiotic = result.Component(3, 6).Trim();
            if (antibiotic.Length == 0)
            {
                warnings.Add($"Aislado {number} del tubo {barcode}: antibiograma sin droga: {result}");
                return;
            }

            var interpretation = new[] { result.Component(4, 5), result.Component(4, 4), result.Component(4, 3) }
                .Select(value => value.Trim())
                .FirstOrDefault(value => value.Length > 0);

            if (interpretation is null)
            {
                warnings.Add($"Aislado {number} del tubo {barcode}: {antibiotic} sin interpretacion, se omite.");
                return;
            }

            var mic = new[] { result.Component(4, 1), result.Component(4, 2) }
                .Select(value => value.Trim())
                .FirstOrDefault(value => value.Length > 0);

            _antibiogram[antibiotic] = new Susceptibility(antibiotic, interpretation, mic);
        }
    }
}

/// <summary>Lo que trajo una transmision de resultados, ya separado por tipo.</summary>
public sealed class ParsedResults
{
    /// <summary>Resultados simples por codigo de barras, en codigos del instrumento.</summary>
    public Dictionary<string, List<InstrumentResult>> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<CultureStatus> CultureStatuses { get; } = [];

    public List<IsolateUpdate> Isolates { get; } = [];

    /// <summary>Registros que no se pudieron interpretar. No frenan al resto.</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>Estado del cultivo <paramref name="TestCode"/> de un tubo (R GND).</summary>
public sealed record CultureStatus(string Barcode, string TestCode, string StatusCode);

/// <summary>Un aislado recibido. <paramref name="SourceTest"/> es el cultivo al que pertenece, si el O lo dice.</summary>
public sealed record IsolateUpdate(string Barcode, string? SourceTest, IsolateReport Isolate);
