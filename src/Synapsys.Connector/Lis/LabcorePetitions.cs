using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Synapsys.Connector.Configuration;

namespace Synapsys.Connector.Lis;

/// <summary>
/// Una peticion del LIS (InstrumentPetitionQueue): una muestra que hay que mandar al equipo.
/// </summary>
public sealed record Petition
{
    public long Id { get; init; }

    /// <summary>Muestra a enviar (MuestrasOrden.mo_id).</summary>
    public long SampleId { get; init; }

    /// <summary>Codigo de barras. <c>null</c> si la muestra ya no existe en el LIS.</summary>
    public string? Barcode { get; init; }

    public string? OrderNumber { get; init; }

    public DateTime? CreatedAt { get; init; }

    /// <summary>Ver <see cref="PetitionStatus"/>.</summary>
    public string Status { get; init; } = PetitionStatus.Pending;

    /// <summary>Motivo del error o del descarte.</summary>
    public string? Error { get; init; }

    public string? PetitionType { get; init; }
}

/// <summary>Estados de una peticion, tal como los entiende labcore-api.</summary>
public static class PetitionStatus
{
    public const string Pending = "Pending";
    public const string Processed = "Processed";
    public const string Discarded = "Discarded";
    public const string Error = "Error";
}

/// <summary>Cuantas peticiones tiene el analizador en cada estado.</summary>
public sealed record PetitionSummary(int Pending, int Processed, int Discarded, int Error, DateTime? OldestPendingAt);

/// <summary>Filtro del listado de peticiones para el front.</summary>
public sealed record PetitionQuery(string? Status, string? Barcode, long? BeforeId, int? Top);

/// <summary>
/// La cola de peticiones del LIS para el analizador de settings/instrument.json. Es la unica
/// cola: el conector no guarda peticiones propias, lee la tabla y le cambia el estado.
/// </summary>
public interface ILisPetitions
{
    /// <summary>Pendientes, de la mas vieja a la mas nueva.</summary>
    Task<IReadOnlyList<Petition>> GetPendingAsync(int top, CancellationToken cancellationToken);

    /// <summary>Peticiones de cualquier estado, de la mas nueva a la mas vieja.</summary>
    Task<IReadOnlyList<Petition>> ListAsync(PetitionQuery query, CancellationToken cancellationToken);

    Task<PetitionSummary> GetSummaryAsync(CancellationToken cancellationToken);

    /// <summary><see cref="PetitionStatus.Pending"/> las vuelve a la cola (reprocesar).</summary>
    Task SetStatusAsync(IReadOnlyCollection<long> ids, string status, string? error, CancellationToken cancellationToken);
}

/// <summary>Implementacion de <see cref="ILisPetitions"/> contra labcore-api (/instruments/{id}/petitions).</summary>
public sealed class LabcorePetitions : ILisPetitions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SettingsFile<InstrumentSettings> _instrument;

    public LabcorePetitions(IHttpClientFactory httpClientFactory, SettingsFile<InstrumentSettings> instrument)
    {
        _httpClientFactory = httpClientFactory;
        _instrument = instrument;
    }

    public Task<IReadOnlyList<Petition>> GetPendingAsync(int top, CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<Petition>>($"{Base()}/pending?top={top}", cancellationToken);

    public Task<IReadOnlyList<Petition>> ListAsync(PetitionQuery query, CancellationToken cancellationToken)
    {
        var parameters = new List<string>();

        void Add(string name, object? value)
        {
            if (value is not null && value.ToString() is { Length: > 0 } text)
            {
                parameters.Add($"{name}={Uri.EscapeDataString(text)}");
            }
        }

        Add("status", query.Status);
        Add("barcode", query.Barcode?.Trim());
        Add("beforeId", query.BeforeId);
        Add("top", query.Top);

        var queryString = parameters.Count == 0 ? string.Empty : "?" + string.Join('&', parameters);
        return GetAsync<IReadOnlyList<Petition>>($"{Base()}{queryString}", cancellationToken);
    }

    public Task<PetitionSummary> GetSummaryAsync(CancellationToken cancellationToken) =>
        GetAsync<PetitionSummary>($"{Base()}/summary", cancellationToken);

    public async Task SetStatusAsync(IReadOnlyCollection<long> ids, string status, string? error, CancellationToken cancellationToken)
    {
        using var response = await Http.PutAsJsonAsync($"{Base()}/status", new { ids, status, error }, Json, cancellationToken);
        await LisRequestException.ThrowIfFailedAsync(response, cancellationToken);
    }

    private HttpClient Http => _httpClientFactory.CreateClient("labcore");

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, cancellationToken);
        await LisRequestException.ThrowIfFailedAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new LisRequestException(HttpStatusCode.BadGateway, "labcore-api respondio sin cuerpo.", null);
    }

    private string Base()
    {
        var instrumentId = _instrument.Current.InstrumentId;
        return instrumentId > 0
            ? $"instruments/{instrumentId}/petitions"
            : throw new LisRequestException(HttpStatusCode.Conflict, "Falta configurar el instrumento (Settings > Instrumento).", null);
    }
}
