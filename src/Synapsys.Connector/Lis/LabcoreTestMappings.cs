using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synapsys.Connector.Configuration;

namespace Synapsys.Connector.Lis;

/// <summary>Una fila del mapeo de pruebas entre el instrumento y el LIS (AnalizadoresDet).</summary>
public sealed record TestMapping
{
    /// <summary>Codigo con el que el equipo manda el resultado. Identifica la fila.</summary>
    public string? IncomingCode { get; init; }

    /// <summary>Codigo de la prueba en el LIS.</summary>
    public string? TestCode { get; init; }

    public string? TestName { get; init; }

    /// <summary>Nombre del test tal como lo llama el equipo.</summary>
    public string? Name { get; init; }

    /// <summary>Codigo con el que se le pide el test al equipo.</summary>
    public string? OutgoingCode { get; init; }

    public double? Factor { get; init; }

    public bool Active { get; init; } = true;

    public string? Suffix { get; init; }

    public string? ResultIndicator { get; init; }

    public string? SampleIndicator { get; init; }

    public bool AutovalidationEnabled { get; init; }

    public string? Units { get; init; }
}

/// <summary>El analizador configurado con sus pruebas mapeadas.</summary>
public sealed record InstrumentTestMappings(int InstrumentId, string? InstrumentName, IReadOnlyList<TestMapping> Tests);

/// <summary>Lo que el gateway necesita de una prueba mapeada activa.</summary>
public readonly record struct TestMap(string TestCode, double Factor, bool AutovalidationEnabled);

/// <summary>Las pruebas activas indexadas para traducir en las dos direcciones.</summary>
public sealed record TestMapIndex(
    IReadOnlyDictionary<string, TestMap> ByIncoming,
    IReadOnlyDictionary<string, string> OutgoingByTest);

/// <summary>
/// El mapeo de pruebas vive en el LIS: se lee y se edita a traves de labcore-api para el
/// analizador de settings/instrument.json. Se guarda en memoria y de aca lo leen el gateway y el
/// front. Se le vuelve a pedir al LIS cuando cambia el analizador, cada
/// <see cref="LabcoreOptions.MappingRefreshMinutes"/> (por si se edito desde el LIS), cuando el
/// front pide refrescar y despues de cada edicion hecha desde el front.
/// </summary>
public sealed class LabcoreTestMappings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LabcoreOptions _options;
    private readonly SettingsFile<InstrumentSettings> _instrument;
    private readonly ILogger<LabcoreTestMappings> _logger;

    // Las lecturas al LIS y las ediciones pasan de a una: una lectura vieja no pisa una edicion.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot? _snapshot;

    public LabcoreTestMappings(
        IHttpClientFactory httpClientFactory,
        IOptions<LabcoreOptions> options,
        SettingsFile<InstrumentSettings> instrument,
        ILogger<LabcoreTestMappings> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _instrument = instrument;
        _logger = logger;
    }

    /// <summary>Todas las pruebas mapeadas, activas o no. Con <paramref name="refresh"/> se releen del LIS.</summary>
    public async Task<InstrumentTestMappings> GetAsync(bool refresh, CancellationToken cancellationToken) =>
        (await LoadAsync(refresh, cancellationToken)).Mappings;

    public async Task<TestMapIndex> GetIndexAsync(CancellationToken cancellationToken) =>
        (await LoadAsync(refresh: false, cancellationToken)).Index;

    public Task CreateAsync(TestMapping mapping, CancellationToken cancellationToken) =>
        WriteAsync((http, instrumentId) => http.PostAsJsonAsync($"instruments/{instrumentId}/tests", mapping, Json, cancellationToken), cancellationToken);

    public Task UpdateAsync(string incomingCode, TestMapping mapping, CancellationToken cancellationToken) =>
        WriteAsync((http, instrumentId) => http.PutAsJsonAsync(
            $"instruments/{instrumentId}/tests/{Uri.EscapeDataString(incomingCode)}", mapping, Json, cancellationToken), cancellationToken);

    public Task DeleteAsync(string incomingCode, CancellationToken cancellationToken) =>
        WriteAsync((http, instrumentId) => http.DeleteAsync(
            $"instruments/{instrumentId}/tests/{Uri.EscapeDataString(incomingCode)}", cancellationToken), cancellationToken);

    private HttpClient Http => _httpClientFactory.CreateClient("labcore");

    private async Task<Snapshot> LoadAsync(bool refresh, CancellationToken cancellationToken)
    {
        var instrumentId = RequireInstrument();

        if (!refresh && Fresh(instrumentId) is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return !refresh && Fresh(instrumentId) is { } loaded
                ? loaded
                : await FetchAsync(instrumentId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// labcore-api no devuelve la fila guardada (y el nombre y las unidades salen de la prueba del
    /// LIS), asi que despues de la edicion se relee el mapeo. La edicion ya quedo hecha: si esa
    /// relectura falla, el cache queda vacio y se vuelve a pedir en el proximo uso.
    /// </summary>
    private async Task WriteAsync(Func<HttpClient, int, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        var instrumentId = RequireInstrument();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using (var response = await send(Http, instrumentId))
            {
                await LisRequestException.ThrowIfFailedAsync(response, cancellationToken);
            }

            Volatile.Write(ref _snapshot, null);

            try
            {
                await FetchAsync(instrumentId, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or LisRequestException or JsonException or OperationCanceledException)
            {
                _logger.LogWarning(ex, "No se pudo releer el mapeo del analizador {InstrumentId} despues de editarlo: se pide en el proximo uso.", instrumentId);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Snapshot? Fresh(int instrumentId)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var maxAge = TimeSpan.FromMinutes(Math.Max(1, _options.MappingRefreshMinutes));

        // Otro analizador es otro mapeo.
        return snapshot is not null
            && snapshot.Mappings.InstrumentId == instrumentId
            && DateTimeOffset.UtcNow - snapshot.LoadedAt < maxAge
                ? snapshot
                : null;
    }

    private async Task<Snapshot> FetchAsync(int instrumentId, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync($"instruments/{instrumentId}/tests?includeInactive=true", cancellationToken);
        await LisRequestException.ThrowIfFailedAsync(response, cancellationToken);

        var body = await response.Content.ReadFromJsonAsync<InstrumentDto>(Json, cancellationToken);
        var mappings = new InstrumentTestMappings(instrumentId, body?.Name, body?.Tests ?? []);
        var snapshot = new Snapshot(mappings, Index(mappings.Tests), DateTimeOffset.UtcNow);

        Volatile.Write(ref _snapshot, snapshot);
        _logger.LogInformation("Mapeo del analizador {InstrumentId} cargado: {Count} codigos activos.", instrumentId, snapshot.Index.ByIncoming.Count);

        return snapshot;
    }

    private static TestMapIndex Index(IEnumerable<TestMapping> tests)
    {
        var byIncoming = new Dictionary<string, TestMap>(StringComparer.OrdinalIgnoreCase);
        var outgoingByTest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var test in tests)
        {
            if (!test.Active || string.IsNullOrWhiteSpace(test.TestCode))
            {
                continue;
            }

            var testCode = test.TestCode.Trim();

            if (!string.IsNullOrWhiteSpace(test.IncomingCode))
            {
                byIncoming[test.IncomingCode.Trim()] = new TestMap(testCode, test.Factor ?? 1d, test.AutovalidationEnabled);
            }

            if (!string.IsNullOrWhiteSpace(test.OutgoingCode))
            {
                outgoingByTest[testCode] = test.OutgoingCode.Trim();
            }
        }

        return new TestMapIndex(byIncoming, outgoingByTest);
    }

    private int RequireInstrument()
    {
        var instrumentId = _instrument.Current.InstrumentId;
        return instrumentId > 0
            ? instrumentId
            : throw new LisRequestException(HttpStatusCode.Conflict, "Falta configurar el instrumento (Settings > Instrumento).", null);
    }

    private sealed record Snapshot(InstrumentTestMappings Mappings, TestMapIndex Index, DateTimeOffset LoadedAt);

    private sealed record InstrumentDto(string? Name, IReadOnlyList<TestMapping>? Tests);
}
