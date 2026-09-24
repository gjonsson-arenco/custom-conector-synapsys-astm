using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Synapsys.Connector.Configuration;
using Synapsys.Connector.Lis;
using Synapsys.Connector.Runtime;

namespace Synapsys.Connector.Web;

/// <summary>
/// Settings editables desde el front. Instrumento, comunicacion, peticiones y catalogos (resultados,
/// microorganismos, antibioticos) se guardan en settings/*.json; el mapeo de pruebas vive en el
/// LIS y se edita a traves de labcore-api.
/// </summary>
/// <remarks>
/// Los codigos viajan por query string y no en la ruta: los de resultados traen puntos, barras
/// y simbolos (".", "&lt;0.5", "&gt;=1") que no sobreviven como segmento de URL.
/// </remarks>
public static class SettingsEndpoints
{
    public static void MapSettingsApi(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/settings");

        settings.MapGet("/instrument", (SettingsFile<InstrumentSettings> file) => Results.Ok(file.Current));

        settings.MapPut("/instrument", async (InstrumentSettings value, SettingsFile<InstrumentSettings> file, CancellationToken cancellationToken) =>
        {
            var errors = value.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            await file.SaveAsync(value, cancellationToken);
            return Results.Ok(value);
        });

        settings.MapGet("/communication", (SettingsFile<CommunicationSettings> file) => Results.Ok(file.Current));

        // Se guarda y, si el puerto estaba abierto, se reinicia para que tome la configuracion nueva.
        settings.MapPut("/communication", async (
            CommunicationSettings value,
            SettingsFile<CommunicationSettings> file,
            ConnectorController controller,
            CancellationToken cancellationToken) =>
        {
            var errors = value.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            await file.SaveAsync(value, cancellationToken);

            var restarted = controller.IsOpen;
            if (restarted)
            {
                await controller.RestartAsync();
            }

            return Results.Ok(new { settings = value, restarted });
        });

        settings.MapGet("/petitions", (SettingsFile<PetitionSettings> file) => Results.Ok(file.Current));

        // Aplica en el acto: el pulling lee el setting en cada silencio de la linea.
        settings.MapPut("/petitions", async (PetitionSettings value, SettingsFile<PetitionSettings> file, CancellationToken cancellationToken) =>
        {
            var errors = value.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            await file.SaveAsync(value, cancellationToken);
            return Results.Ok(value);
        });

        MapTestMappings(settings.MapGroup("/test-mappings"));
        MapCatalogs(settings.MapGroup("/catalogs/{catalog}"));
    }

    private static void MapTestMappings(RouteGroupBuilder group)
    {
        group.MapGet("/", (LabcoreTestMappings catalog, CancellationToken cancellationToken) =>
            CallLisAsync(async () => Results.Ok(await catalog.GetAsync(cancellationToken))));

        group.MapPost("/", (TestMapping mapping, LabcoreTestMappings catalog, CancellationToken cancellationToken) =>
            CallLisAsync(async () =>
            {
                await catalog.CreateAsync(mapping, cancellationToken);
                return Results.NoContent();
            }));

        group.MapPut("/", (string incomingCode, TestMapping mapping, LabcoreTestMappings catalog, CancellationToken cancellationToken) =>
            CallLisAsync(async () =>
            {
                await catalog.UpdateAsync(incomingCode, mapping, cancellationToken);
                return Results.NoContent();
            }));

        group.MapDelete("/", (string incomingCode, LabcoreTestMappings catalog, CancellationToken cancellationToken) =>
            CallLisAsync(async () =>
            {
                await catalog.DeleteAsync(incomingCode, cancellationToken);
                return Results.NoContent();
            }));
    }

    /// <summary>
    /// Catalogos codigo => descripcion. <c>{catalog}</c> es results, organisms o antibiotics
    /// (ver <see cref="CodeCatalogs.Find"/>); todos se administran igual.
    /// </summary>
    private static void MapCatalogs(RouteGroupBuilder group)
    {
        group.MapGet("/", (string catalog, CodeCatalogs catalogs) =>
            With(catalogs, catalog, found => Task.FromResult(Results.Ok(found.All))));

        // Alta (sin code) o edicion del codigo indicado; si el body trae otro codigo, se renombra.
        group.MapPut("/", (string catalog, string? code, CodeMapping mapping, CodeCatalogs catalogs, CancellationToken cancellationToken) =>
            With(catalogs, catalog, async found =>
            {
                var error = await found.UpsertAsync(code, mapping, cancellationToken);
                return error is null ? Results.Ok(found.All) : Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);
            }));

        group.MapDelete("/", (string catalog, string code, CodeCatalogs catalogs, CancellationToken cancellationToken) =>
            With(catalogs, catalog, async found =>
                await found.DeleteAsync(code, cancellationToken)
                    ? Results.Ok(found.All)
                    : Results.Problem($"No existe el codigo {code}.", statusCode: StatusCodes.Status404NotFound)));

        group.MapPost("/clear", (string catalog, CodeCatalogs catalogs, CancellationToken cancellationToken) =>
            With(catalogs, catalog, async found =>
            {
                await found.ClearAsync(cancellationToken);
                return Results.Ok(found.All);
            }));

        // Body: el CSV tal cual (codigo;descripcion por linea). replace=true reemplaza todo el catalogo.
        group.MapPost("/import", (string catalog, HttpRequest request, bool? replace, CodeCatalogs catalogs, CancellationToken cancellationToken) =>
            With(catalogs, catalog, async found =>
            {
                using var reader = new StreamReader(request.Body, Encoding.UTF8);
                var csv = await reader.ReadToEndAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(csv))
                {
                    return Results.Problem("El archivo esta vacio.", statusCode: StatusCodes.Status400BadRequest);
                }

                return Results.Ok(await found.ImportCsvAsync(csv, replace ?? false, cancellationToken));
            }));
    }

    private static async Task<IResult> With(CodeCatalogs catalogs, string name, Func<CodeCatalog, Task<IResult>> action) =>
        catalogs.Find(name) is { } catalog
            ? await action(catalog)
            : Results.Problem($"No existe el catalogo {name}.", statusCode: StatusCodes.Status404NotFound);

    /// <summary>Devuelve al front el mismo error que dio labcore-api, o 502 si no se pudo llegar.</summary>
    internal static async Task<IResult> CallLisAsync(Func<Task<IResult>> call)
    {
        try
        {
            return await call();
        }
        catch (LisRequestException ex) when (ex.Errors is { Count: > 0 })
        {
            return Results.ValidationProblem(ex.Errors, ex.Message, statusCode: (int)ex.Status);
        }
        catch (LisRequestException ex)
        {
            return Results.Problem(ex.Message, statusCode: (int)ex.Status);
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem($"No se pudo llegar a labcore-api: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
