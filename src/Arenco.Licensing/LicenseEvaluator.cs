using System.Security.Cryptography;

namespace Arenco.Licensing;

/// <summary>
/// Decide si una clave habilita a este conector en esta maquina en una fecha dada. No tiene estado:
/// el vencimiento se recalcula cada vez con la fecha del dia.
/// </summary>
public sealed class LicenseEvaluator
{
    private readonly ECDsa _publicKey;
    private readonly string _product;
    private readonly string _machineCode;
    private readonly int _warningDays;
    private readonly int _graceDays;

    public LicenseEvaluator(ECDsa publicKey, string product, string machineCode, int warningDays, int graceDays)
    {
        _publicKey = publicKey;
        _product = product;
        _machineCode = machineCode;
        _warningDays = warningDays;
        _graceDays = graceDays;
    }

    public string Product => _product;

    public string MachineCode => _machineCode;

    public LicenseStatus Evaluate(string? key, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Status(LicenseState.Missing, null,
                $"El conector no tiene licencia. Pedila con el codigo de maquina {_machineCode}.");
        }

        if (!LicenseKey.TryVerify(key, _publicKey, out var license) || license is null)
        {
            return Status(LicenseState.Invalid, null,
                "La clave de licencia no es valida (esta incompleta, fue modificada o no la emitio Arenco).");
        }

        if (!string.Equals(license.Product, _product, StringComparison.OrdinalIgnoreCase))
        {
            return Status(LicenseState.WrongProduct, license,
                $"La licencia es para otro producto ({license.Product}), no para {_product}.");
        }

        if (Arenco.Licensing.MachineCode.Normalize(license.MachineCode) != Arenco.Licensing.MachineCode.Normalize(_machineCode))
        {
            return Status(LicenseState.WrongMachine, license,
                $"La licencia es para otra maquina ({license.MachineCode}). El codigo de esta maquina es {_machineCode}.");
        }

        var days = license.ExpiresAt.DayNumber - today.DayNumber;
        var graceUntil = license.ExpiresAt.AddDays(_graceDays);
        var expires = license.ExpiresAt.ToString("dd/MM/yyyy");

        if (days < 0)
        {
            var graceLeft = graceUntil.DayNumber - today.DayNumber;
            return graceLeft >= 0
                ? Status(LicenseState.Grace, license,
                    $"La licencia vencio el {expires}. Por tolerancia el conector sigue funcionando hasta el {graceUntil:dd/MM/yyyy} ({Days(graceLeft)}); despues no levanta la conexion.",
                    graceUntil, days)
                : Status(LicenseState.Expired, license,
                    $"La licencia vencio el {expires} y termino la tolerancia el {graceUntil:dd/MM/yyyy}. El conector no levanta la conexion hasta instalar una licencia nueva.",
                    graceUntil, days);
        }

        return days < _warningDays
            ? Status(LicenseState.ExpiringSoon, license,
                $"La licencia vence el {expires} ({Days(days)}). Pedi la renovacion para no cortar el servicio.",
                graceUntil, days)
            : Status(LicenseState.Valid, license,
                $"Licencia vigente hasta el {expires}.",
                graceUntil, days);
    }

    private LicenseStatus Status(LicenseState state, LicensePayload? license, string message, DateOnly? graceUntil = null, int? days = null) =>
        new(state,
            state is LicenseState.Valid or LicenseState.ExpiringSoon or LicenseState.Grace,
            message,
            _product,
            _machineCode,
            license,
            graceUntil,
            days);

    private static string Days(int days) => days switch
    {
        0 => "hoy es el ultimo dia",
        1 => "queda 1 dia",
        _ => $"quedan {days} dias"
    };
}
