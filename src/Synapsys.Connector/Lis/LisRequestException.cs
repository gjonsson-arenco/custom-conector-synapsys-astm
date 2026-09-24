using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Synapsys.Connector.Lis;

/// <summary>labcore-api rechazo la operacion; viaja al front con el mismo status y el detalle.</summary>
public sealed class LisRequestException(HttpStatusCode status, string message, IDictionary<string, string[]>? errors)
    : Exception(message)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpStatusCode Status { get; } = status;

    public IDictionary<string, string[]>? Errors { get; } = errors;

    /// <summary>Traduce el ProblemDetails de labcore-api a un error que el front pueda mostrar.</summary>
    public static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ProblemDto? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync<ProblemDto>(Json, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
        }

        var message = problem?.Detail ?? problem?.Title ?? $"labcore-api respondio {(int)response.StatusCode}.";
        throw new LisRequestException(response.StatusCode, message, problem?.Errors);
    }

    private sealed record ProblemDto(string? Title, string? Detail, Dictionary<string, string[]>? Errors);
}
