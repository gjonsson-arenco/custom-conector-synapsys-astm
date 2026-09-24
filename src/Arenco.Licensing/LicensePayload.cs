namespace Arenco.Licensing;

/// <summary>
/// Lo que certifica una licencia: que producto, para que cliente, en que maquina y hasta cuando.
/// Viaja en claro dentro de la clave; lo que la hace infalsificable es la firma.
/// </summary>
/// <param name="Version">Version del formato, para poder cambiarlo sin romper claves emitidas.</param>
/// <param name="Product">Id del conector (por ejemplo <c>synapsys-connector</c>).</param>
/// <param name="Customer">Cliente/instalacion, solo informativo.</param>
/// <param name="MachineCode">Codigo de maquina (request code) al que queda atada.</param>
/// <param name="IssuedAt">Fecha de emision.</param>
/// <param name="ExpiresAt">Ultimo dia de vigencia (inclusive).</param>
public sealed record LicensePayload(
    int Version,
    string Product,
    string Customer,
    string MachineCode,
    DateOnly IssuedAt,
    DateOnly ExpiresAt)
{
    public const int CurrentVersion = 1;
}
