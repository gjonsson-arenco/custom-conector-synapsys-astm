using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arenco.Licensing;

/// <summary>
/// La licencia instalada en el conector: la lee del archivo, la evalua con la fecha del dia y
/// permite instalar una nueva desde el front. El conector consulta <see cref="Current"/> antes de
/// levantar la conexion y se suscribe a <see cref="Changed"/> para reaccionar a una instalacion.
/// </summary>
public sealed class LicenseManager
{
    private readonly LicenseEvaluator _evaluator;
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly ILogger<LicenseManager> _logger;
    private readonly Lock _lock = new();

    private string? _key;

    public LicenseManager(IOptions<LicenseOptions> options, TimeProvider time, ILogger<LicenseManager> logger)
        : this(options.Value, Arenco.Licensing.MachineCode.Current, time, logger)
    {
    }

    public LicenseManager(LicenseOptions options, string machineCode, TimeProvider time, ILogger<LicenseManager> logger)
    {
        if (string.IsNullOrWhiteSpace(options.Product))
        {
            throw new InvalidOperationException("Falta el producto de la licencia (LicenseOptions.Product).");
        }

        _evaluator = new LicenseEvaluator(
            LicenseKey.ImportPublicKey(options.PublicKey),
            options.Product,
            machineCode,
            options.WarningDays,
            options.GraceDays);
        _path = options.Path;
        _time = time;
        _logger = logger;
        _key = Read();
    }

    /// <summary>Se dispara al instalar una licencia nueva.</summary>
    public event Action<LicenseStatus>? Changed;

    public string MachineCode => _evaluator.MachineCode;

    /// <summary>Estado de la licencia hoy.</summary>
    public LicenseStatus Current
    {
        get
        {
            lock (_lock)
            {
                return _evaluator.Evaluate(_key, Today);
            }
        }
    }

    /// <summary>
    /// Instala una clave. Solo se guarda si habilita al conector (vigente o en tolerancia): una clave
    /// equivocada no pisa a la que funciona. Devuelve como evaluo la clave recibida.
    /// </summary>
    public LicenseStatus Install(string key)
    {
        var compact = new string(key.Where(c => !char.IsWhiteSpace(c)).ToArray());
        LicenseStatus status;

        lock (_lock)
        {
            status = _evaluator.Evaluate(compact, Today);
            if (!status.IsUsable)
            {
                return status;
            }

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, compact + Environment.NewLine);
            _key = compact;
        }

        _logger.LogInformation(
            "Licencia instalada: {Customer}, vence {ExpiresAt:yyyy-MM-dd}.",
            status.License!.Customer,
            status.License.ExpiresAt);

        Changed?.Invoke(status);
        return status;
    }

    private DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    private string? Read()
    {
        try
        {
            return File.Exists(_path) ? File.ReadAllText(_path).Trim() : null;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "No se pudo leer la licencia de {Path}.", _path);
            return null;
        }
    }
}
