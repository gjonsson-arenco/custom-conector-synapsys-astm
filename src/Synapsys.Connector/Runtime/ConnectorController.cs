using Arenco.Licensing;
using Microsoft.Extensions.Logging;
using Synapsys.Connector.Astm;
using Synapsys.Connector.Configuration;
using Synapsys.Connector.Flows;
using Synapsys.Connector.Monitoring;
using Synapsys.Connector.Petitions;
using Synapsys.Connector.Transport;

namespace Synapsys.Connector.Runtime;

/// <summary>
/// Maneja el ciclo de vida del puerto ASTM: abrir, cerrar, reiniciar y reportar estado. Corre el
/// bucle de sesiones (recibir -> rutear -> responder) que antes vivia en el worker, ahora
/// arrancable/parable desde el front. Cada transmision y cada byte alimentan el monitor.
/// </summary>
/// <remarks>
/// <para>Sin licencia utilizable el puerto no se abre: <see cref="OpenAsync"/> lo rechaza y deja el
/// motivo en <see cref="ConnectorStatus.LastError"/>. <see cref="LicenseWatchdog"/> lo reabre al
/// instalar una licencia y lo cierra si vence mientras corre.</para>
/// Con la linea en silencio la sesion le da el turno al <see cref="PetitionOutbox"/> para bajar
/// las muestras que pide el LIS. El equipo tiene prioridad: si empieza a hablar, se lo atiende.
/// </remarks>
public sealed class ConnectorController : IAsyncDisposable
{
    /// <summary>Silencio en la linea a partir del cual se considera desocupada.</summary>
    private static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(2);

    private readonly TransportFactory _transportFactory;
    private readonly AstmChannelFactory _channelFactory;
    private readonly TransmissionRouter _router;
    private readonly IConnectorMonitor _monitor;
    private readonly PetitionOutbox _petitions;
    private readonly SettingsFile<CommunicationSettings> _communication;
    private readonly LicenseManager _license;
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
    private volatile bool _blockedByLicense;

    public ConnectorController(
        TransportFactory transportFactory,
        AstmChannelFactory channelFactory,
        TransmissionRouter router,
        IConnectorMonitor monitor,
        PetitionOutbox petitions,
        SettingsFile<CommunicationSettings> communication,
        LicenseManager license,
        ILogger<ConnectorController> logger)
    {
        _transportFactory = transportFactory;
        _channelFactory = channelFactory;
        _router = router;
        _monitor = monitor;
        _petitions = petitions;
        _communication = communication;
        _license = license;
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
                _lastError,
                _blockedByLicense);
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

            var license = _license.Current;
            _blockedByLicense = !license.IsUsable;
            if (_blockedByLicense)
            {
                _lastError = license.Message;
                SetState(PortState.Stopped);
                _logger.LogError("No se abre el puerto por la licencia: {Message}", license.Message);
                _monitor.PublishEvent(ConnectorEvent.Error("license.blocked", $"No se abre el puerto: {license.Message}"));
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

    /// <summary>Si el ultimo intento de abrir el puerto lo freno la licencia.</summary>
    public bool BlockedByLicense => _blockedByLicense;

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
        var line = new IdleAwareConnection(connection, cancellationToken);
        var channel = _channelFactory.Create(line, astm, separators);

        // Sesion nueva: lo que se habia leido de la tabla se vuelve a leer.
        _petitions.Reset();

        while (!cancellationToken.IsCancellationRequested)
        {
            // Linea libre: turno de las peticiones. Si Synapsys pidio la linea al mismo tiempo
            // (colision de ENQ), tiene prioridad: se lo recibe ya y la peticion espera al proximo silencio.
            if (!await line.WaitForDataAsync(IdleWindow, cancellationToken) &&
                await SendPetitionAsync(line, channel, separators, cancellationToken) != SendOutcome.Contention)
            {
                continue;
            }

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

    /// <summary>
    /// Turno de las peticiones con la linea libre. Armar el mensaje lleva idas al LIS; si mientras
    /// tanto el equipo empezo a hablar, no se manda: queda para el proximo silencio.
    /// </summary>
    /// <returns>Como termino el envio; <c>null</c> si no se mando nada.</returns>
    private async Task<SendOutcome?> SendPetitionAsync(IdleAwareConnection line, IAstmChannel channel, AstmSeparators separators, CancellationToken cancellationToken)
    {
        var outgoing = await _petitions.PrepareAsync(separators, cancellationToken);

        if (outgoing is null || line.HasData)
        {
            return null;
        }

        var outcome = await channel.SendAsync(outgoing.Records, cancellationToken);
        await _petitions.CompleteAsync(outgoing, outcome, cancellationToken);
        return outcome;
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
