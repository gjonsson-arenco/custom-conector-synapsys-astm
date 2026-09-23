using Microsoft.Extensions.Logging;
using Synapsys.Connector.Astm;
using Synapsys.Connector.Configuration;
using Synapsys.Connector.Flows;
using Synapsys.Connector.Monitoring;
using Synapsys.Connector.Transport;

namespace Synapsys.Connector.Runtime;

/// <summary>
/// Maneja el ciclo de vida del puerto ASTM: abrir, cerrar, reiniciar y reportar estado. Corre el
/// bucle de sesiones (recibir -> rutear -> responder) que antes vivia en el worker, ahora
/// arrancable/parable desde el front. Cada transmision y cada byte alimentan el monitor.
/// </summary>
public sealed class ConnectorController : IAsyncDisposable
{
    private readonly TransportFactory _transportFactory;
    private readonly AstmChannelFactory _channelFactory;
    private readonly TransmissionRouter _router;
    private readonly IConnectorMonitor _monitor;
    private readonly SettingsFile<CommunicationSettings> _communication;
    private readonly ILogger<ConnectorController> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private volatile PortState _state = PortState.Stopped;

    // Configuracion con la que se abrio el puerto; los cambios guardados despues aplican al reabrir.
    private CommunicationSettings? _active;
    private string? _remote;
    private DateTimeOffset? _startedAt;
    private long _received;
    private long _sent;
    private string? _lastError;

    public ConnectorController(
        TransportFactory transportFactory,
        AstmChannelFactory channelFactory,
        TransmissionRouter router,
        IConnectorMonitor monitor,
        SettingsFile<CommunicationSettings> communication,
        ILogger<ConnectorController> logger)
    {
        _transportFactory = transportFactory;
        _channelFactory = channelFactory;
        _router = router;
        _monitor = monitor;
        _communication = communication;
        _logger = logger;
    }

    public ConnectorStatus Status
    {
        get
        {
            var options = (_active ?? _communication.Current).Transport;
            return new ConnectorStatus(
                _state,
                options.Mode,
                options.Host,
                options.Port,
                _remote,
                _startedAt,
                Interlocked.Read(ref _received),
                Interlocked.Read(ref _sent),
                _lastError);
        }
    }

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loop is { IsCompleted: false })
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _active = _communication.Current;
            _lastError = null;
            _startedAt = DateTimeOffset.UtcNow;
            SetState(PortState.Starting);
            var settings = _active;
            _loop = Task.Run(() => RunAsync(settings, _cts.Token), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;

        await _gate.WaitAsync();
        try
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }
        finally
        {
            _gate.Release();
        }

        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync();

        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();
        _remote = null;
        _startedAt = null;
        _active = null;
        SetState(PortState.Stopped);
        _monitor.PublishEvent(ConnectorEvent.Info("port.closed", "Puerto cerrado."));
    }

    public async Task RestartAsync()
    {
        await CloseAsync();
        await OpenAsync();
    }

    /// <summary>Si el puerto esta abierto (o intentando abrirse).</summary>
    public bool IsOpen => _loop is { IsCompleted: false };

    private async Task RunAsync(CommunicationSettings settings, CancellationToken cancellationToken)
    {
        var options = settings.Transport;

        try
        {
            SetState(PortState.Listening);
            _monitor.PublishEvent(ConnectorEvent.Info(
                "port.open",
                $"Puerto abierto en modo {options.Mode} ({options.Host}:{options.Port})."));

            var transport = _transportFactory.Create(options);

            await foreach (var connection in transport.ConnectionsAsync(cancellationToken))
            {
                _remote = connection.Remote;
                SetState(PortState.Connected);
                _monitor.PublishEvent(ConnectorEvent.Info("connection.open", $"Synapsys conectado desde {connection.Remote}."));

                var monitored = new MonitoredConnection(connection, _monitor);

                try
                {
                    await RunSessionAsync(monitored, settings.Astm, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    _logger.LogError(ex, "La sesion con {Remote} termino con error.", connection.Remote);
                    _monitor.PublishEvent(ConnectorEvent.Error("connection.error", $"Sesion con {connection.Remote}: {ex.Message}"));
                }
                finally
                {
                    await monitored.DisposeAsync();
                    _remote = null;
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        SetState(PortState.Listening);
                        _monitor.PublishEvent(ConnectorEvent.Info("connection.close", "Conexion cerrada."));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            SetState(PortState.Faulted);
            _logger.LogError(ex, "El puerto se cayo.");
            _monitor.PublishEvent(ConnectorEvent.Error("port.faulted", $"El puerto se cayo: {ex.Message}"));
        }
    }

    private async Task RunSessionAsync(IAstmConnection connection, AstmOptions astm, CancellationToken cancellationToken)
    {
        var separators = AstmChannelFactory.SeparatorsFor(astm);
        var channel = _channelFactory.Create(connection, astm, separators);

        while (!cancellationToken.IsCancellationRequested)
        {
            var incoming = await channel.ReceiveAsync(cancellationToken);
            if (incoming is null)
            {
                return;
            }

            if (incoming.Count == 0)
            {
                continue;
            }

            Interlocked.Increment(ref _received);
            _monitor.PublishEvent(DescribeTransmission(incoming));

            var response = await _router.RouteAsync(incoming, separators, cancellationToken);

            if (response is { Count: > 0 })
            {
                await channel.SendAsync(response, cancellationToken);
                Interlocked.Increment(ref _sent);
                _monitor.PublishEvent(ConnectorEvent.Info("astm.sent", $"Respuesta enviada ({response.Count} registros)."));
            }
        }
    }

    private static ConnectorEvent DescribeTransmission(IReadOnlyList<AstmRecord> records)
    {
        var barcode = records
            .FirstOrDefault(record => record.Type is 'Q' or 'O')?
            .Field(3)
            .Split('^', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        var suffix = string.IsNullOrEmpty(barcode) ? string.Empty : $" (tubo {barcode})";

        if (records.Any(record => record.Type == 'Q'))
        {
            return ConnectorEvent.Info("astm.query", $"Consulta recibida{suffix}.");
        }

        if (records.Any(record => record.Type == 'R'))
        {
            return ConnectorEvent.Info("astm.results", $"Resultados recibidos{suffix}.");
        }

        return ConnectorEvent.Info("astm.received", $"Transmision recibida ({records.Count} registros).");
    }

    private void SetState(PortState state) => _state = state;

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _gate.Dispose();
    }
}
