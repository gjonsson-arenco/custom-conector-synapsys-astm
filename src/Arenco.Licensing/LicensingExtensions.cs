using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Arenco.Licensing;

/// <summary>Alta del licenciamiento en un conector y sus endpoints para el front.</summary>
public static class LicensingExtensions
{
    /// <summary>
    /// Registra <see cref="LicenseManager"/> para <paramref name="product"/>. La seccion
    /// <c>Licensing</c> del appsettings puede cambiar el archivo y los dias de aviso/tolerancia.
    /// </summary>
    public static IServiceCollection AddArencoLicensing(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        string product)
    {
        services.Configure<LicenseOptions>(configuration.GetSection(LicenseOptions.SectionName));
        services.PostConfigure<LicenseOptions>(options =>
        {
            options.Product = product;
            options.Path = Path.Combine(environment.ContentRootPath, options.Path);
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<LicenseManager>();
        return services;
    }

    /// <summary>
    /// <c>GET /api/license</c>: estado y codigo de maquina. <c>PUT /api/license</c> con
    /// <c>{ "key": "..." }</c>: instala una clave (400 con el motivo si no sirve).
    /// </summary>
    public static void MapLicenseApi(this IEndpointRouteBuilder app)
    {
        var license = app.MapGroup("/api/license");

        license.MapGet("/", (LicenseManager manager) => Results.Ok(manager.Current));

        license.MapPut("/", (InstallLicenseRequest request, LicenseManager manager) =>
        {
            if (string.IsNullOrWhiteSpace(request.Key))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["key"] = ["Falta la clave de licencia."] });
            }

            var status = manager.Install(request.Key);
            return status.IsUsable
                ? Results.Ok(status)
                : Results.Problem(status.Message, statusCode: StatusCodes.Status400BadRequest, title: "Licencia no instalada");
        });
    }

    public sealed record InstallLicenseRequest(string? Key);
}
