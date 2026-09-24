using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Synapsys.Connector.Lis;
using Synapsys.Connector.Petitions;
using Synapsys.Connector.Runtime;

namespace Synapsys.Connector.Web;

/// <summary>
/// Pulling de peticiones visto desde el front: estado del envio, cuantas hay en cada estado, el
/// listado y el reproceso. Las peticiones viven en el LIS; aca solo se consultan via labcore-api.
/// El prendido/apagado es un setting (<c>/api/settings/petitions</c>).
/// </summary>
public static class PetitionEndpoints
{
    public static void MapPetitionsApi(this IEndpointRouteBuilder app)
    {
        var petitions = app.MapGroup("/api/petitions");

        // El resumen del LIS puede fallar (labcore-api caida) y el estado del envio igual se muestra.
        petitions.MapGet("/status", async (
            PetitionOutbox outbox,
            ConnectorController controller,
            ILisPetitions lis,
            CancellationToken cancellationToken) =>
        {
            PetitionSummary? summary = null;
            string? summaryError = null;

            try
            {
                summary = await lis.GetSummaryAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is LisRequestException or HttpRequestException)
            {
                summaryError = ex.Message;
            }

            return Results.Ok(new
            {
                outbox = outbox.Status,
                connected = controller.Status.State == PortState.Connected,
                summary,
                summaryError
            });
        });

        petitions.MapGet("/", (string? status, string? barcode, long? beforeId, int? top, ILisPetitions lis, CancellationToken cancellationToken) =>
            SettingsEndpoints.CallLisAsync(async () =>
                Results.Ok(await lis.ListAsync(new PetitionQuery(status, barcode, beforeId, top), cancellationToken))));

        // Vuelve las peticiones a pendientes: el conector las manda en el proximo silencio de la linea.
        petitions.MapPost("/reprocess", (ReprocessRequest request, ILisPetitions lis, PetitionOutbox outbox, CancellationToken cancellationToken) =>
            SettingsEndpoints.CallLisAsync(async () =>
            {
                if (request.Ids is not { Count: > 0 })
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["ids"] = ["Falta al menos una peticion."] });
                }

                await lis.SetStatusAsync(request.Ids, PetitionStatus.Pending, null, cancellationToken);
                outbox.Reset();
                return Results.NoContent();
            }));
    }

    private sealed record ReprocessRequest(IReadOnlyList<long>? Ids);
}
