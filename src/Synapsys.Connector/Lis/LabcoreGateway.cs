using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synapsys.Connector.Configuration;
using Synapsys.Connector.Monitoring;

namespace Synapsys.Connector.Lis;

/// <summary>
/// Implementacion de <see cref="ILisGateway"/> contra labcore-api. Cachea el mapeo de codigos
/// que vive en el LIS (GET /instruments/{id}/tests) y lo aplica en las dos direcciones. Antes de
/// informar un resultado lo traduce con el mapeo de resultados (settings/result-mappings.json); los
/// cultivos, ademas, con los catalogos de microorganismos y antibioticos. Si una regla de
/// autovalidacion (settings/autovalidation.json) acepta el resultado, va ya validado.
/// </summary>
public sealed class LabcoreGateway : ILisGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LabcoreOptions _options;
    private readonly SettingsFile<InstrumentSettings> _instrument;
    private readonly CodeCatalogs _catalogs;
    private readonly SettingsFile<AutoValidationSettings> _autoValidation;
    private readonly IConnectorMonitor _monitor;
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
        SettingsFile<AutoValidationSettings> autoValidation,
        IConnectorMonitor monitor,
        ILogger<LabcoreGateway> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _instrument = instrument;
        _catalogs = catalogs;
        _autoValidation = autoValidation;
        _monitor = monitor;
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
        var sampleType = new SampleTypeLookup(this, barcode, cancellationToken);

        foreach (var result in results)
        {
            if (!mapping.ByIncoming.TryGetValue(result.InstrumentCode.Trim(), out var map))
            {
                _logger.LogWarning("Sin mapeo para el codigo de instrumento {Code}: se descarta ese resultado.", result.InstrumentCode);
                continue;
            }

            var rule = await FindAutoValidationAsync(barcode, map, result.Value, result.Flags, sampleType);

            payload.Add(new SaveTestResultDto
            {
                TestCode = map.TestCode,
                Result = _catalogs.Results.Translate(result.Value) ?? ApplyFactor(result.Value, map.Factor),
                Status = rule is null ? LoadedStatus : ValidatedStatus,
                AutoValidation = rule?.Describe(),
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
        PublishAutoValidated(barcode, payload.Where(row => row.AutoValidation is not null).Select(row => row.AutoValidation!));
    }

    public async Task SaveCultureAsync(CultureReport culture, CancellationToken cancellationToken)
    {
        var mapping = await GetMappingAsync(cancellationToken);

        if (!mapping.ByIncoming.TryGetValue(culture.TestCode.Trim(), out var map))
        {
            _logger.LogWarning("Sin mapeo para el cultivo {Code}: no se informa el cultivo del tubo {Barcode}.", culture.TestCode, culture.Barcode);
            return;
        }

        // Solo el estado: un cultivo con aislados (antibiograma) no es un resultado simple.
        var rule = culture.StatusCode is not null && culture.Isolates.Count == 0
            ? await FindAutoValidationAsync(
                culture.Barcode, map, culture.StatusCode, [],
                new SampleTypeLookup(this, culture.Barcode, cancellationToken))
            : null;

        var request = new SaveCultureDto
        {
            UserId = _options.UserId,
            InstrumentId = _instrument.Current.InstrumentId,
            // Cada envio es el informe completo: siempre reemplaza al anterior.
            Overwrite = true,
            TestCode = map.TestCode,
            Status = rule is null ? LoadedStatus : ValidatedStatus,
            AutoValidation = rule?.Describe(),
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

        if (rule is not null)
        {
            PublishAutoValidated(culture.Barcode, [rule.Describe()]);
        }
    }

    /// <summary>
    /// La regla de autovalidacion que acepta el resultado, o <c>null</c> si se guarda como cargado.
    /// Hace falta que la prueba tenga la autovalidacion habilitada en el mapeo del LIS y que una
    /// regla la acepte. El tipo de muestra se le pide al LIS solo si alguna regla candidata lo exige.
    /// </summary>
    private async Task<AutoValidationRule?> FindAutoValidationAsync(
        string barcode,
        TestMap map,
        string value,
        IReadOnlyList<string> flags,
        SampleTypeLookup sampleType)
    {
        var settings = _autoValidation.Current;
        if (!settings.Enabled)
        {
            return null;
        }

        var testCode = map.TestCode;
        var candidates = settings.Rules.Where(rule => rule.Matches(testCode, value)).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        // El LIS tiene la ultima palabra: sin la autovalidacion habilitada en el mapeo, la regla no alcanza.
        if (!map.AutovalidationEnabled)
        {
            _logger.LogInformation("{Test} = {Value} del tubo {Barcode} no se autovalida: la prueba no tiene la autovalidacion habilitada en el mapeo del LIS.",
                testCode, value, barcode);
            return null;
        }

        // Con flags del equipo el resultado ya no es "simple": lo mira una persona.
        if (flags.Any(flag => !string.IsNullOrWhiteSpace(flag)))
        {
            _logger.LogInformation("{Test} = {Value} del tubo {Barcode} no se autovalida: el equipo lo informo con flags ({Flags}).",
                testCode, value, barcode, string.Join(",", flags));
            return null;
        }

        var type = candidates.Any(rule => rule.NeedsSampleType) ? await sampleType.GetAsync() : null;

        return candidates.FirstOrDefault(rule => rule.AcceptsSample(type));
    }

    private void PublishAutoValidated(string barcode, IEnumerable<string> rules)
    {
        foreach (var rule in rules)
        {
            _logger.LogInformation("Tubo {Barcode}: autovalidado por la regla {Rule}.", barcode, rule);
            _monitor.PublishEvent(ConnectorEvent.Info("result.autovalidated", $"Tubo {barcode}: autovalidado ({rule})."));
        }
    }

    /// <summary>Tipo de muestra del tubo segun el LIS. Si no se puede saber, no se autovalida por tipo.</summary>
    private async Task<string?> GetSampleTypeAsync(string barcode, CancellationToken cancellationToken)
    {
        try
        {
            var http = _httpClientFactory.CreateClient("labcore");
            using var response = await http.GetAsync($"samples/{Uri.EscapeDataString(barcode)}", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();

            var sample = await response.Content.ReadFromJsonAsync<SampleDto>(Json, cancellationToken);
            return sample?.SampleTypeCode?.Trim();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogWarning(ex, "No se pudo leer el tipo de muestra del tubo {Barcode}: no se autovalida por tipo de muestra.", barcode);
            return null;
        }
    }

    /// <summary>El tipo de muestra se pide una sola vez por tubo, y solo si hace falta.</summary>
    private sealed class SampleTypeLookup(LabcoreGateway gateway, string barcode, CancellationToken cancellationToken)
    {
        private Task<string?>? _sampleType;

        public Task<string?> GetAsync() => _sampleType ??= gateway.GetSampleTypeAsync(barcode, cancellationToken);
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
                    byIncoming[test.IncomingCode.Trim()] = new TestMap(testCode, test.Factor ?? 1d, test.AutovalidationEnabled ?? false);
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

    /// <summary>Laboratorios.l_estado: cargado, pendiente de validacion.</summary>
    private const short LoadedStatus = 2;

    /// <summary>Laboratorios.l_estado: validado. labcore-api sella la fecha y el usuario de validacion.</summary>
    private const short ValidatedStatus = 4;

    private readonly record struct TestMap(string TestCode, double Factor, bool AutovalidationEnabled);

    private readonly record struct Mapping(
        IReadOnlyDictionary<string, TestMap> ByIncoming,
        IReadOnlyDictionary<string, string> OutgoingByTest);

    private sealed record SampleDto(string? SampleTypeCode, IReadOnlyList<SampleTestDto>? Tests);

    private sealed record SampleTestDto(string? TestCode, string? Status);

    private sealed record InstrumentTestsDto(IReadOnlyList<MappedTestDto>? Tests);

    private sealed record MappedTestDto(string? TestCode, string? IncomingCode, string? OutgoingCode, double? Factor, bool? AutovalidationEnabled);

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
        public short Status { get; init; } = LoadedStatus;
        public string? AutoValidation { get; init; }
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
        public short Status { get; init; } = LoadedStatus;
        public string? AutoValidation { get; init; }
        public IReadOnlyList<string> Flags { get; init; } = [];
    }
}
