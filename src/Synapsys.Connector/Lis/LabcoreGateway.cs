using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synapsys.Connector.Configuration;

namespace Synapsys.Connector.Lis;

/// <summary>
/// Implementacion de <see cref="ILisGateway"/> contra labcore-api. Cachea el mapeo de codigos
/// que vive en el LIS (GET /instruments/{id}/tests) y lo aplica en las dos direcciones. Antes de
/// informar un resultado lo traduce con el mapeo de resultados (settings/result-mappings.json); los
/// cultivos, ademas, con los catalogos de microorganismos y antibioticos.
/// </summary>
public sealed class LabcoreGateway : ILisGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LabcoreOptions _options;
    private readonly SettingsFile<InstrumentSettings> _instrument;
    private readonly CodeCatalogs _catalogs;
    private readonly ILogger<LabcoreGateway> _logger;

    private readonly SemaphoreSlim _mappingGate = new(1, 1);
    private Dictionary<string, TestMap> _byIncoming = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _outgoingByTest = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _mappingLoadedAt = DateTimeOffset.MinValue;

    public LabcoreGateway(
        IHttpClientFactory httpClientFactory,
        IOptions<LabcoreOptions> options,
        SettingsFile<InstrumentSettings> instrument,
        CodeCatalogs catalogs,
        ILogger<LabcoreGateway> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _instrument = instrument;
        _catalogs = catalogs;
        _logger = logger;

        // Otro analizador es otro mapeo.
        _instrument.Changed += _ => InvalidateMapping();
    }

    /// <summary>Descarta el mapeo cacheado; la proxima operacion lo vuelve a pedir al LIS.</summary>
    public void InvalidateMapping() => _mappingLoadedAt = DateTimeOffset.MinValue;

    public async Task<SampleOrders?> GetOrdersAsync(string barcode, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient("labcore");

        using var response = await http.GetAsync($"samples/{Uri.EscapeDataString(barcode)}", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("El LIS no conoce el codigo de barras {Barcode}: query negativa.", barcode);
            return null;
        }

        response.EnsureSuccessStatusCode();

        var sample = await response.Content.ReadFromJsonAsync<SampleDto>(Json, cancellationToken);
        var mapping = await GetMappingAsync(cancellationToken);

        var codes = (sample?.Tests ?? [])
            .Where(test => string.Equals(test.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            .Select(test => test.TestCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => mapping.OutgoingByTest.GetValueOrDefault(code!.Trim(), code!.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SampleOrders(barcode, codes);
    }

    public async Task SaveResultsAsync(string barcode, IReadOnlyList<InstrumentResult> results, CancellationToken cancellationToken)
    {
        var mapping = await GetMappingAsync(cancellationToken);
        var payload = new List<SaveTestResultDto>();

        foreach (var result in results)
        {
            if (!mapping.ByIncoming.TryGetValue(result.InstrumentCode.Trim(), out var map))
            {
                _logger.LogWarning("Sin mapeo para el codigo de instrumento {Code}: se descarta ese resultado.", result.InstrumentCode);
                continue;
            }

            payload.Add(new SaveTestResultDto
            {
                TestCode = map.TestCode,
                Result = _catalogs.Results.Translate(result.Value) ?? ApplyFactor(result.Value, map.Factor),
                Status = 2,
                Flags = result.Flags
            });
        }

        if (payload.Count == 0)
        {
            _logger.LogWarning("Ningun resultado del tubo {Barcode} pudo mapearse al LIS.", barcode);
            return;
        }

        var http = _httpClientFactory.CreateClient("labcore");
        var request = new SaveResultsDto
        {
            UserId = _options.UserId,
            InstrumentId = _instrument.Current.InstrumentId,
            Overwrite = _options.OverwriteResults,
            Results = payload
        };

        using var response = await http.PostAsJsonAsync(
            $"samples/{Uri.EscapeDataString(barcode)}/results", request, Json, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("labcore-api rechazo los resultados del tubo {Barcode} ({Status}): {Body}",
                barcode, (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        _logger.LogInformation("Se enviaron {Count} resultados del tubo {Barcode} al LIS.", payload.Count, barcode);
    }

    public async Task SaveCultureAsync(CultureReport culture, CancellationToken cancellationToken)
    {
        var mapping = await GetMappingAsync(cancellationToken);

        if (!mapping.ByIncoming.TryGetValue(culture.TestCode.Trim(), out var map))
        {
            _logger.LogWarning("Sin mapeo para el cultivo {Code}: no se informa el cultivo del tubo {Barcode}.", culture.TestCode, culture.Barcode);
            return;
        }

        var request = new SaveCultureDto
        {
            UserId = _options.UserId,
            InstrumentId = _instrument.Current.InstrumentId,
            // Cada envio es el informe completo: siempre reemplaza al anterior.
            Overwrite = true,
            TestCode = map.TestCode,
            Summary = culture.StatusCode is null ? null : Translate(_catalogs.Results, culture.StatusCode, "resultado", culture),
            Isolates = culture.Isolates
                .Select(isolate => new CultureIsolateDto
                {
                    Number = isolate.Number,
                    Organism = Translate(_catalogs.Organisms, isolate.OrganismCode, "microorganismo", culture),
                    Mechanisms = isolate.Mechanisms,
                    Antibiotics = isolate.Antibiogram
                        .Select(row => new CultureAntibioticDto
                        {
                            Name = Translate(_catalogs.Antibiotics, row.AntibioticCode, "antibiotico", culture),
                            Interpretation = row.Interpretation,
                            Mic = row.Mic
                        })
                        .ToList()
                })
                .ToList()
        };

        var http = _httpClientFactory.CreateClient("labcore");

        using var response = await http.PostAsJsonAsync(
            $"samples/{Uri.EscapeDataString(culture.Barcode)}/cultures", request, Json, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("labcore-api rechazo el cultivo {Test} del tubo {Barcode} ({Status}): {Body}",
                map.TestCode, culture.Barcode, (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        _logger.LogInformation("Se informo el cultivo {Test} del tubo {Barcode} al LIS: {Isolates} aislados.",
            map.TestCode, culture.Barcode, culture.Isolates.Count);
    }

    /// <summary>
    /// Nombre del codigo en el catalogo. Si falta, se informa el codigo tal cual: es preferible un
    /// "PSEAER" en el informe a perder el aislado; queda avisado para completar el catalogo.
    /// </summary>
    private string Translate(CodeCatalog catalog, string code, string kind, CultureReport culture)
    {
        var name = catalog.Translate(code);
        if (name is not null)
        {
            return name;
        }

        _logger.LogWarning("El {Kind} {Code} no esta en el catalogo: se informa el codigo (tubo {Barcode}).", kind, code, culture.Barcode);
        return code;
    }

    private async Task<Mapping> GetMappingAsync(CancellationToken cancellationToken)
    {
        var fresh = DateTimeOffset.UtcNow - _mappingLoadedAt < TimeSpan.FromMinutes(Math.Max(1, _options.MappingRefreshMinutes));
        if (fresh && _mappingLoadedAt != DateTimeOffset.MinValue)
        {
            return new Mapping(_byIncoming, _outgoingByTest);
        }

        await _mappingGate.WaitAsync(cancellationToken);

        try
        {
            fresh = DateTimeOffset.UtcNow - _mappingLoadedAt < TimeSpan.FromMinutes(Math.Max(1, _options.MappingRefreshMinutes));
            if (fresh && _mappingLoadedAt != DateTimeOffset.MinValue)
            {
                return new Mapping(_byIncoming, _outgoingByTest);
            }

            var instrumentId = _instrument.Current.InstrumentId;
            if (instrumentId <= 0)
            {
                throw new InvalidOperationException("Falta configurar el instrumento (Settings > Instrumento).");
            }

            var http = _httpClientFactory.CreateClient("labcore");
            var tests = await http.GetFromJsonAsync<InstrumentTestsDto>(
                $"instruments/{instrumentId}/tests", Json, cancellationToken);

            var byIncoming = new Dictionary<string, TestMap>(StringComparer.OrdinalIgnoreCase);
            var outgoingByTest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var test in tests?.Tests ?? [])
            {
                if (string.IsNullOrWhiteSpace(test.TestCode))
                {
                    continue;
                }

                var testCode = test.TestCode.Trim();

                if (!string.IsNullOrWhiteSpace(test.IncomingCode))
                {
                    byIncoming[test.IncomingCode.Trim()] = new TestMap(testCode, test.Factor ?? 1d);
                }

                if (!string.IsNullOrWhiteSpace(test.OutgoingCode))
                {
                    outgoingByTest[testCode] = test.OutgoingCode.Trim();
                }
            }

            _byIncoming = byIncoming;
            _outgoingByTest = outgoingByTest;
            _mappingLoadedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation("Mapeo del analizador {InstrumentId} cargado: {Count} codigos.", instrumentId, byIncoming.Count);

            return new Mapping(_byIncoming, _outgoingByTest);
        }
        finally
        {
            _mappingGate.Release();
        }
    }

    /// <summary>Aplica el factor de conversion cuando el valor es numerico; si no, lo deja igual.</summary>
    private static string ApplyFactor(string value, double factor)
    {
        if (Math.Abs(factor - 1d) < double.Epsilon || factor == 0d)
        {
            return value;
        }

        var normalized = value.Replace(',', '.');
        if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            return (number * factor).ToString(CultureInfo.InvariantCulture);
        }

        return value;
    }

    private readonly record struct TestMap(string TestCode, double Factor);

    private readonly record struct Mapping(
        IReadOnlyDictionary<string, TestMap> ByIncoming,
        IReadOnlyDictionary<string, string> OutgoingByTest);

    private sealed record SampleDto(IReadOnlyList<SampleTestDto>? Tests);

    private sealed record SampleTestDto(string? TestCode, string? Status);

    private sealed record InstrumentTestsDto(IReadOnlyList<MappedTestDto>? Tests);

    private sealed record MappedTestDto(string? TestCode, string? IncomingCode, string? OutgoingCode, double? Factor);

    private sealed class SaveResultsDto
    {
        public int UserId { get; init; }
        public int InstrumentId { get; init; }
        public bool Overwrite { get; init; }
        public IReadOnlyList<SaveTestResultDto> Results { get; init; } = [];
    }

    private sealed class SaveCultureDto
    {
        public int UserId { get; init; }
        public int InstrumentId { get; init; }
        public bool Overwrite { get; init; }
        public string? TestCode { get; init; }
        public string? Summary { get; init; }
        public IReadOnlyList<CultureIsolateDto> Isolates { get; init; } = [];
    }

    private sealed class CultureIsolateDto
    {
        public int Number { get; init; }
        public string Organism { get; init; } = string.Empty;
        public IReadOnlyList<string> Mechanisms { get; init; } = [];
        public IReadOnlyList<CultureAntibioticDto> Antibiotics { get; init; } = [];
    }

    private sealed class CultureAntibioticDto
    {
        public string Name { get; init; } = string.Empty;
        public string Interpretation { get; init; } = string.Empty;
        public string? Mic { get; init; }
    }

    private sealed class SaveTestResultDto
    {
        public string? TestCode { get; init; }
        public string? Result { get; init; }
        public short Status { get; init; } = 2;
        public IReadOnlyList<string> Flags { get; init; } = [];
    }
}
