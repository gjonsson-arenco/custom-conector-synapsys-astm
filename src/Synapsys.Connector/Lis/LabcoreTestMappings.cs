using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

/// <summary>
/// El mapeo de pruebas vive en el LIS: se lee y se edita a traves de labcore-api para el
/// analizador de settings/instrument.json. Cada escritura invalida el mapeo cacheado por el gateway.
/// </summary>
public sealed class LabcoreTestMappings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SettingsFile<InstrumentSettings> _instrument;
    private readonly LabcoreGateway _gateway;

    public LabcoreTestMappings(IHttpClientFactory httpClientFactory, SettingsFile<InstrumentSettings> instrument, LabcoreGateway gateway)
    {
        _httpClientFactory = httpClientFactory;
        _instrument = instrument;
        _gateway = gateway;
    }

    public async Task<InstrumentTestMappings> GetAsync(CancellationToken cancellationToken)
    {
        var instrumentId = RequireInstrument();
        using var response = await Http.GetAsync($"instruments/{instrumentId}/tests?includeInactive=true", cancellationToken);
        await LisRequestException.ThrowIfFailedAsync(response, cancellationToken);

        var body = await response.Content.ReadFromJsonAsync<InstrumentDto>(Json, cancellationToken);
        return new InstrumentTestMappings(instrumentId, body?.Name, body?.Tests ?? []);
    }

    public Task CreateAsync(TestMapping mapping, CancellationToken cancellationToken) =>
        WriteAsync(http => http.PostAsJsonAsync($"instruments/{RequireInstrument()}/tests", mapping, Json, cancellationToken), cancellationToken);

    public Task UpdateAsync(string incomingCode, TestMapping mapping, CancellationToken cancellationToken) =>
        WriteAsync(http => http.PutAsJsonAsync(
            $"instruments/{RequireInstrument()}/tests/{Uri.EscapeDataString(incomingCode)}", mapping, Json, cancellationToken), cancellationToken);

    public Task DeleteAsync(string incomingCode, CancellationToken cancellationToken) =>
        WriteAsync(http => http.DeleteAsync(
            $"instruments/{RequireInstrument()}/tests/{Uri.EscapeDataString(incomingCode)}", cancellationToken), cancellationToken);

    private HttpClient Http => _httpClientFactory.CreateClient("labcore");

    private async Task WriteAsync(Func<HttpClient, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        using var response = await send(Http);
        await LisRequestException.ThrowIfFailedAsync(response, cancellationToken);
        _gateway.InvalidateMapping();
    }

    private int RequireInstrument()
    {
        var instrumentId = _instrument.Current.InstrumentId;
        return instrumentId > 0
            ? instrumentId
            : throw new LisRequestException(HttpStatusCode.Conflict, "Falta configurar el instrumento (Settings > Instrumento).", null);
    }

    private sealed record InstrumentDto(string? Name, IReadOnlyList<TestMapping>? Tests);
}
