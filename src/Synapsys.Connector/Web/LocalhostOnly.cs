using System.Net;

namespace Synapsys.Connector.Web;

/// <summary>
/// Corta con 403 cualquier request HTTP (front, API y websockets de monitoreo) que no venga de la
/// misma maquina. "Urls" ya apunta a localhost, pero eso lo pisa una variable de entorno o --urls;
/// este filtro no depende de la config. El puerto ASTM es un TcpListener aparte y no pasa por aca.
/// </summary>
public static class LocalhostOnly
{
    public static IApplicationBuilder UseLocalhostOnly(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!IsLocal(context.Connection.RemoteIpAddress))
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(LocalhostOnly))
                    .LogWarning("Request rechazado desde {RemoteIp}: la administracion solo acepta conexiones locales.",
                        context.Connection.RemoteIpAddress);

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });

    // Sin IP remota es un transporte en proceso (TestServer), no una conexion de red.
    private static bool IsLocal(IPAddress? address) =>
        address is null
        || IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
