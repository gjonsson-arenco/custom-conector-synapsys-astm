using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Synapsys.Connector.Astm;
using Synapsys.Connector.Configuration;
using Synapsys.Connector.Lis;
using Synapsys.Connector.Monitoring;
using Synapsys.Connector.Petitions;

namespace Synapsys.Connector.Tests;

/// <summary>
/// La tabla del LIS es la cola: una peticion cambia de estado recien cuando el equipo acepto la
/// muestra. Estos tests fijan que se manda, que se descarta y que queda pendiente en cada caso.
/// </summary>
public sealed class PetitionOutboxTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "synapsys-petitions-" + Guid.NewGuid().ToString("N"));
    private readonly ILisPetitions _petitions = Substitute.For<ILisPetitions>();
    private readonly ILisGateway _lis = Substitute.For<ILisGateway>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero));
    private readonly SettingsFile<PetitionSettings> _settings;
    private readonly PetitionOutbox _outbox;

    public PetitionOutboxTests()
    {
        _settings = new SettingsFile<PetitionSettings>(
            Path.Combine(_directory, "petitions.json"),
            () => new PetitionSettings { Enabled = true, PollSeconds = 10 },
            NullLogger.Instance);

        var instrument = new SettingsFile<InstrumentSettings>(
            Path.Combine(_directory, "instrument.json"),
            () => new InstrumentSettings { InstrumentId = 7 },
            NullLogger.Instance);

        _outbox = new PetitionOutbox(
            _petitions, _lis, _settings, instrument, new ConnectorMonitor(), _time, NullLogger<PetitionOutbox>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // La primera consulta trae las peticiones; las siguientes, la tabla ya sin pendientes.
    private void Pending(params Petition[] petitions) =>
        _petitions.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(petitions, Array.Empty<Petition>());

    private void Orders(string barcode, params string[] codes) =>
        _lis.GetOrdersAsync(barcode, Arg.Any<CancellationToken>()).Returns(new SampleOrders(barcode, codes));

    private static Petition Petition(long id, long sampleId, string? barcode) => new() { Id = id, SampleId = sampleId, Barcode = barcode };

    private Task<OutgoingPetition?> Prepare() => _outbox.PrepareAsync(AstmSeparators.Default, CancellationToken.None);

    private Task Received(long[] ids, string status) =>
        _petitions.Received(1).SetStatusAsync(
            Arg.Is<IReadOnlyCollection<long>>(sent => sent.SequenceEqual(ids)), status, Arg.Any<string?>(), Arg.Any<CancellationToken>());

    [Fact]
    public async Task Apagado_no_consulta_la_tabla()
    {
        await _settings.SaveAsync(new PetitionSettings { Enabled = false });

        (await Prepare()).ShouldBeNull();

        await _petitions.DidNotReceiveWithAnyArgs().GetPendingAsync(default, default);
    }

    [Fact]
    public async Task Varias_peticiones_de_la_misma_muestra_son_un_envio_y_se_marcan_juntas_recien_al_aceptarse()
    {
        Pending(Petition(1, 100, "B100"), Petition(2, 100, "B100"));
        Orders("B100", "GLU", "UREA");

        var outgoing = (await Prepare()).ShouldNotBeNull();

        outgoing.Group.Ids.ShouldBe([1L, 2L]);
        outgoing.Records.Select(record => record.Type).ShouldBe(['H', 'P', 'O', 'L']);
        outgoing.Records[2].ToString().ShouldBe(@"O|1|B100||^^^GLU\^^^UREA|R");

        // Armado pero todavia no mandado: nada cambio de estado.
        await _petitions.DidNotReceiveWithAnyArgs().SetStatusAsync(default!, default!, default, default);

        await _outbox.CompleteAsync(outgoing, SendOutcome.Sent, CancellationToken.None);

        await Received([1, 2], PetitionStatus.Processed);
        _outbox.Status.Sent.ShouldBe(1);
    }

    [Fact]
    public async Task Lo_que_no_tiene_nada_que_mandar_se_descarta_y_se_sigue_con_la_siguiente()
    {
        Pending(Petition(1, 100, null), Petition(2, 200, "B200"), Petition(3, 300, "B300"), Petition(4, 400, "B400"));
        _lis.GetOrdersAsync("B200", Arg.Any<CancellationToken>()).Returns((SampleOrders?)null);
        Orders("B300");
        Orders("B400", "GLU");

        var outgoing = (await Prepare()).ShouldNotBeNull();

        outgoing.Group.Barcode.ShouldBe("B400");
        await Received([1], PetitionStatus.Discarded);
        await Received([2], PetitionStatus.Discarded);
        await Received([3], PetitionStatus.Discarded);
    }

    [Fact]
    public async Task Si_el_equipo_pide_la_linea_la_peticion_se_vuelve_a_mandar_en_el_proximo_silencio()
    {
        Pending(Petition(1, 100, "B100"));
        Orders("B100", "GLU");

        var first = (await Prepare()).ShouldNotBeNull();
        await _outbox.CompleteAsync(first, SendOutcome.Contention, CancellationToken.None);

        // Atendido el equipo, sin esperas ni intentos gastados.
        var second = (await Prepare()).ShouldNotBeNull();

        second.Group.Ids.ShouldBe([1L]);
        await _petitions.DidNotReceiveWithAnyArgs().SetStatusAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Una_muestra_que_el_equipo_rechaza_queda_en_error_y_no_frena_la_cola()
    {
        Pending(Petition(1, 100, "B100"));
        Orders("B100", "GLU");

        for (var attempt = 1; attempt <= PetitionOutbox.MaxAttempts; attempt++)
        {
            var outgoing = (await Prepare()).ShouldNotBeNull();
            await _outbox.CompleteAsync(outgoing, SendOutcome.Failed, CancellationToken.None);
            _time.Advance(PetitionOutbox.RetryBackoff);
        }

        await Received([1], PetitionStatus.Error);
    }

    [Fact]
    public async Task Con_labcore_api_caida_no_cambia_nada_y_se_reintenta_en_el_proximo_poll()
    {
        _petitions.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Petition>>(_ => throw new HttpRequestException("connection refused"), _ => [Petition(1, 100, "B100")]);
        Orders("B100", "GLU");

        (await Prepare()).ShouldBeNull();
        _outbox.Status.LastError.ShouldNotBeNull().ShouldContain("connection refused");

        _time.Advance(TimeSpan.FromSeconds(10));

        (await Prepare()).ShouldNotBeNull();
        _outbox.Status.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Si_no_se_pudo_marcar_despues_de_mandar_se_reintenta_la_marca_y_no_el_envio()
    {
        Pending(Petition(1, 100, "B100"));
        Orders("B100", "GLU");
        _petitions.SetStatusAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(_ => throw new HttpRequestException("timeout"), _ => Task.CompletedTask);

        var outgoing = (await Prepare()).ShouldNotBeNull();
        await _outbox.CompleteAsync(outgoing, SendOutcome.Sent, CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(10));
        (await Prepare()).ShouldBeNull();

        await _petitions.ReceivedWithAnyArgs(2).SetStatusAsync(default!, default!, default, default);
        await _lis.Received(1).GetOrdersAsync("B100", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reprocesar_vuelve_a_leer_la_tabla_sin_esperar_el_poll()
    {
        _petitions.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Petition>(), new[] { Petition(9, 900, "B900") });
        Orders("B900", "GLU");

        (await Prepare()).ShouldBeNull();

        _outbox.Reset();

        (await Prepare()).ShouldNotBeNull().Group.Ids.ShouldBe([9L]);
    }
}
