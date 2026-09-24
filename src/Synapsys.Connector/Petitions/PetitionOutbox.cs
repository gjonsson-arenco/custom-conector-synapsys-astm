using Microsoft.Extensions.Logging;
using Synapsys.Connector.Astm;
using Synapsys.Connector.Configuration;
using Synapsys.Connector.Flows;
using Synapsys.Connector.Lis;
using Synapsys.Connector.Monitoring;

namespace Synapsys.Connector.Petitions;

/// <summary>Las peticiones pendientes de una misma muestra: se mandan una vez y se marcan juntas.</summary>
public sealed record PetitionGroup(long SampleId, string? Barcode, IReadOnlyList<long> Ids);

/// <summary>Una muestra lista para bajar al equipo.</summary>
public sealed record OutgoingPetition(PetitionGroup Group, IReadOnlyList<AstmRecord> Records);

/// <summary>Foto del pulling de peticiones para el front.</summary>
public sealed record PetitionOutboxStatus(
    bool Enabled,
    int PollSeconds,
    DateTimeOffset? LastPollAt,
    DateTimeOffset? LastSentAt,
    long Sent,
    string? LastError);

/// <summary>
/// Pulling de peticiones: toma de InstrumentPetitionQueue (via labcore-api) las muestras que el
/// LIS pide mandar al equipo, arma el mensaje de ordenes y, cuando el equipo lo acepto, marca la
/// peticion como procesada.
/// </summary>
/// <remarks>
/// <para>
/// No hay cola propia ni servicio aparte. La tabla es la cola y la sesion ASTM es la que da el
/// turno: cuando la linea queda en silencio llama a <see cref="PrepareAsync"/>, manda lo que
/// devuelve si el equipo sigue callado, y avisa como termino con <see cref="CompleteAsync"/>. El
/// equipo siempre tiene prioridad.
/// </para>
/// <para>
/// El estado se cambia recien despues del envio: un reinicio en el medio vuelve a mandar la
/// muestra (el equipo la recibe dos veces) pero nunca la pierde.
/// </para>
/// <para>
/// Lo que se lee de la tabla se guarda en memoria solo para no consultarla en cada silencio; se
/// descarta al reprocesar, al cambiar la configuracion y al empezar cada sesion.
/// </para>
/// </remarks>
public sealed class PetitionOutbox
{
    /// <summary>Peticiones que se leen por consulta.</summary>
    public const int BatchSize = 50;

    /// <summary>Intentos (armado o envio) antes de dejar la peticion en Error y seguir con la siguiente.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Espera antes de reintentar una muestra que el equipo no acepto.</summary>
    public static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(5);

    private readonly ILisPetitions _petitions;
    private readonly ILisGateway _lis;
    private readonly SettingsFile<PetitionSettings> _settings;
    private readonly SettingsFile<InstrumentSettings> _instrument;
    private readonly IConnectorMonitor _monitor;
    private readonly TimeProvider _time;
    private readonly ILogger<PetitionOutbox> _logger;

    private readonly List<PetitionGroup> _batch = [];
    private readonly Dictionary<long, int> _attempts = [];

    // Mandada pero sin marcar (labcore-api no contesto): se reintenta la marca, no el envio.
    private PetitionGroup? _unmarked;
    private DateTimeOffset _nextPollAt = DateTimeOffset.MinValue;
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;
    private volatile bool _resetRequested;

    private DateTimeOffset? _lastPollAt;
    private DateTimeOffset? _lastSentAt;
    private long _sent;
    private string? _lastError;

    public PetitionOutbox(
        ILisPetitions petitions,
        ILisGateway lis,
        SettingsFile<PetitionSettings> settings,
        SettingsFile<InstrumentSettings> instrument,
        IConnectorMonitor monitor,
        TimeProvider time,
        ILogger<PetitionOutbox> logger)
    {
        _petitions = petitions;
        _lis = lis;
        _settings = settings;
        _instrument = instrument;
        _monitor = monitor;
        _time = time;
        _logger = logger;

        _settings.Changed += _ => Reset();
        _instrument.Changed += _ => Reset();
    }

    public PetitionOutboxStatus Status => new(
        _settings.Current.Enabled,
        _settings.Current.PollSeconds,
        _lastPollAt,
        _lastSentAt,
        Interlocked.Read(ref _sent),
        _lastError);

    /// <summary>
    /// Olvida lo leido de la tabla: la proxima vez que la linea este libre se vuelve a consultar.
    /// Se puede llamar desde cualquier hilo; se aplica en el proximo turno.
    /// </summary>
    public void Reset() => _resetRequested = true;

    /// <summary>
    /// La proxima muestra a mandar, o <c>null</c> si no hay nada (o todavia no toca). No usa la
    /// linea: solo consulta el LIS y arma el mensaje. Las peticiones sin nada que mandar las
    /// descarta en el camino.
    /// </summary>
    public async Task<OutgoingPetition?> PrepareAsync(AstmSeparators separators, CancellationToken cancellationToken)
    {
        ApplyReset();

        var settings = _settings.Current;
        var now = _time.GetUtcNow();

        if (!settings.Enabled || !_instrument.Current.IsConfigured || now < _notBefore)
        {
            return null;
        }

        try
        {
            if (_unmarked is { } sent)
            {
                await _petitions.SetStatusAsync(sent.Ids, PetitionStatus.Processed, null, cancellationToken);
                _unmarked = null;
            }

            while (true)
            {
                if (_batch.Count == 0 && !await PollAsync(settings, now, cancellationToken))
                {
                    return null;
                }

                var outgoing = await BuildAsync(_batch[0], separators, cancellationToken);
                if (outgoing is not null)
                {
                    return outgoing;
                }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // labcore-api no contesta o fallo: nada cambia de estado, se reintenta en el proximo poll.
            _notBefore = now + TimeSpan.FromSeconds(settings.PollSeconds);
            Fail("petition.lis", $"Peticiones: no se pudo hablar con labcore-api ({ex.Message}).", ex);
            return null;
        }
    }

    /// <summary>Como termino el envio de <paramref name="outgoing"/>. Si el equipo la acepto, se marca procesada.</summary>
    public async Task CompleteAsync(OutgoingPetition outgoing, SendOutcome outcome, CancellationToken cancellationToken)
    {
        var group = outgoing.Group;
        var now = _time.GetUtcNow();

        switch (outcome)
        {
            case SendOutcome.Sent:
                Remove(group);
                Interlocked.Increment(ref _sent);
                _lastSentAt = now;
                _lastError = null;
                _monitor.PublishEvent(ConnectorEvent.Info("petition.sent", $"Muestra {group.Barcode} enviada al equipo ({Describe(group)})."));

                try
                {
                    await _petitions.SetStatusAsync(group.Ids, PetitionStatus.Processed, null, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _unmarked = group;
                    _notBefore = now + RetryBackoff;
                    Fail("petition.lis", $"La muestra {group.Barcode} se envio pero no se pudo marcar la peticion ({ex.Message}). Se reintenta.", ex);
                }

                break;

            case SendOutcome.Contention:
                // El equipo pidio la linea: se lo atiende y la peticion sigue primera en la fila, para el
                // proximo silencio. No cuenta como intento.
                _logger.LogInformation("Colision al mandar la muestra {Barcode}: se atiende al equipo y se reintenta despues.", group.Barcode);
                break;

            default:
                if (CountAttempt(group) < MaxAttempts)
                {
                    _notBefore = now + RetryBackoff;
                    _logger.LogWarning("El equipo no acepto la muestra {Barcode}: se reintenta.", group.Barcode);
                    break;
                }

                try
                {
                    await MarkAsync(group, PetitionStatus.Error, $"El equipo no acepto la muestra en {MaxAttempts} intentos.", cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _notBefore = now + RetryBackoff;
                    Fail("petition.lis", $"No se pudo marcar con error la peticion de la muestra {group.Barcode} ({ex.Message}).", ex);
                }

                break;
        }
    }

    private async Task<bool> PollAsync(PetitionSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (now < _nextPollAt)
        {
            return false;
        }

        _nextPollAt = now + TimeSpan.FromSeconds(settings.PollSeconds);
        _lastPollAt = now;

        var pending = await _petitions.GetPendingAsync(BatchSize, cancellationToken);
        _lastError = null;

        // Varias peticiones de la misma muestra (se tocaron varias veces en el LIS) son un solo envio:
        // el mensaje se arma con el estado actual de la muestra, que las cubre a todas.
        _batch.AddRange(pending
            .GroupBy(petition => petition.SampleId)
            .Select(group => new PetitionGroup(
                group.Key,
                group.Select(petition => petition.Barcode).FirstOrDefault(barcode => !string.IsNullOrWhiteSpace(barcode))?.Trim(),
                group.Select(petition => petition.Id).ToArray())));

        if (_batch.Count > 0)
        {
            _logger.LogInformation("Peticiones: {Count} muestras pendientes de mandar al equipo.", _batch.Count);
        }

        return _batch.Count > 0;
    }

    private async Task<OutgoingPetition?> BuildAsync(PetitionGroup group, AstmSeparators separators, CancellationToken cancellationToken)
    {
        if (group.Barcode is null)
        {
            await MarkAsync(group, PetitionStatus.Discarded, "La muestra ya no existe en el LIS.", cancellationToken);
            return null;
        }

        SampleOrders? orders;

        try
        {
            orders = await _lis.GetOrdersAsync(group.Barcode, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (CountAttempt(group) < MaxAttempts)
            {
                throw;
            }

            // Una muestra que siempre falla no puede frenar a las que vienen detras.
            await MarkAsync(group, PetitionStatus.Error, $"No se pudieron leer las pruebas de la muestra: {ex.Message}", cancellationToken);
            return null;
        }

        if (orders is null)
        {
            await MarkAsync(group, PetitionStatus.Discarded, $"El LIS no tiene la muestra {group.Barcode}.", cancellationToken);
            return null;
        }

        if (orders.InstrumentCodes.Count == 0)
        {
            await MarkAsync(group, PetitionStatus.Discarded, "La muestra no tiene pruebas pendientes.", cancellationToken);
            return null;
        }

        return new OutgoingPetition(group, AstmValues.Orders(orders, separators));
    }

    /// <summary>Deja las peticiones en Discarded o Error y sigue con la proxima muestra.</summary>
    private async Task MarkAsync(PetitionGroup group, string status, string reason, CancellationToken cancellationToken)
    {
        await _petitions.SetStatusAsync(group.Ids, status, reason, cancellationToken);
        Remove(group);

        var message = $"Muestra {group.Barcode ?? group.SampleId.ToString()} ({Describe(group)}): {reason}";

        if (status == PetitionStatus.Error)
        {
            _logger.LogError("Peticion con error. {Message}", message);
            _monitor.PublishEvent(ConnectorEvent.Error("petition.error", message));
        }
        else
        {
            _logger.LogWarning("Peticion descartada. {Message}", message);
            _monitor.PublishEvent(ConnectorEvent.Warn("petition.discarded", message));
        }
    }

    private void ApplyReset()
    {
        if (!_resetRequested)
        {
            return;
        }

        _resetRequested = false;
        _batch.Clear();
        _attempts.Clear();
        _nextPollAt = DateTimeOffset.MinValue;
        _notBefore = DateTimeOffset.MinValue;
    }

    private int CountAttempt(PetitionGroup group) =>
        _attempts[group.SampleId] = _attempts.GetValueOrDefault(group.SampleId) + 1;

    private void Remove(PetitionGroup group)
    {
        _batch.RemoveAll(item => item.SampleId == group.SampleId);
        _attempts.Remove(group.SampleId);
    }

    private void Fail(string kind, string message, Exception ex)
    {
        _lastError = message;
        _logger.LogWarning(ex, "{Message}", message);
        _monitor.PublishEvent(ConnectorEvent.Warn(kind, message));
    }

    private static string Describe(PetitionGroup group) =>
        group.Ids.Count == 1 ? $"peticion {group.Ids[0]}" : $"peticiones {string.Join(", ", group.Ids)}";
}
