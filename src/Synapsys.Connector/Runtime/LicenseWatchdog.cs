using Arenco.Licensing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Synapsys.Connector.Monitoring;

namespace Synapsys.Connector.Runtime;

/// <summary>
/// Sigue la licencia mientras el conector corre. Cada hora (y en el acto al instalar una licencia
/// desde el front):
/// <list type="bullet">
/// <item>si la licencia dejo de servir (vencio la tolerancia) y el puerto esta abierto, lo cierra;</item>
/// <item>si el puerto estaba frenado por la licencia y ahora sirve, lo abre;</item>
/// <item>si vence pronto o esta en tolerancia, avisa una vez por dia en el log y en los eventos.</item>
/// </list>
/// </summary>
public sealed class LicenseWatchdog : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

    private readonly LicenseManager _license;
    private readonly ConnectorController _controller;
    private readonly IConnectorMonitor _monitor;
    private readonly TimeProvider _time;
    private readonly ILogger<LicenseWatchdog> _logger;
    private readonly SemaphoreSlim _wake = new(0);

    private DateOnly? _lastWarning;

    public LicenseWatchdog(
        LicenseManager license,
        ConnectorController controller,
        IConnectorMonitor monitor,
        TimeProvider time,
        ILogger<LicenseWatchdog> logger)
    {
        _license = license;
        _controller = controller;
        _monitor = monitor;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _license.Changed += OnChanged;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Fallo el control de la licencia.");
                }

                await _wake.WaitAsync(CheckEvery, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _license.Changed -= OnChanged;
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        var status = _license.Current;

        if (!status.IsUsable)
        {
            if (_controller.IsOpen)
            {
                _logger.LogError("Se cierra el puerto por la licencia: {Message}", status.Message);
                await _controller.CloseAsync();
                // Reabrir deja el puerto frenado con el motivo a la vista en el front.
                await _controller.OpenAsync(cancellationToken);
            }

            return;
        }

        if (_controller.BlockedByLicense)
        {
            _monitor.PublishEvent(ConnectorEvent.Info("license.ok", "Licencia instalada: se abre el puerto."));
            await _controller.OpenAsync(cancellationToken);
        }

        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        if (status.State is LicenseState.ExpiringSoon or LicenseState.Grace && _lastWarning != today)
        {
            _lastWarning = today;
            _logger.LogWarning("{Message}", status.Message);
            _monitor.PublishEvent(ConnectorEvent.Warn("license.warning", status.Message));
        }
    }

    private void OnChanged(LicenseStatus status)
    {
        // Una licencia nueva cambia lo que haya que avisar: se vuelve a avisar hoy si corresponde.
        _lastWarning = null;
        _wake.Release();
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
