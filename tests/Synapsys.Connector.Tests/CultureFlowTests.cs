using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Synapsys.Connector.Astm;
using Synapsys.Connector.Flows;
using Synapsys.Connector.Lis;
using Synapsys.Connector.Microbiology;

namespace Synapsys.Connector.Tests;

/// <summary>
/// El cultivo llega por partes y al LIS tiene que ir siempre completo. Estos tests pasan los
/// mensajes reales por el flow y miran que informe recibe el LIS en cada paso.
/// </summary>
public sealed class CultureFlowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "synapsys-cultures-" + Guid.NewGuid().ToString("N"));
    private readonly ILisGateway _lis = Substitute.For<ILisGateway>();
    private readonly List<CultureReport> _sent = [];

    public CultureFlowTests()
    {
        _lis.SaveCultureAsync(Arg.Do<CultureReport>(_sent.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private CultureStore Store() => new(
        Options.Create(new CultureStoreOptions { Directory = _directory }),
        new FakeTimeProvider(new DateTimeOffset(2026, 9, 23, 8, 33, 0, TimeSpan.Zero)),
        NullLogger<CultureStore>.Instance);

    // Un flow nuevo por mensaje, como despues de reiniciar el conector: el estado vive en disco.
    private Task Receive(string[] lines) =>
        new ResultsFlow(_lis, Store(), NullLogger<ResultsFlow>.Instance)
            .HandleAsync(Messages.Records(lines), AstmSeparators.Default, CancellationToken.None);

    [Fact]
    public async Task El_estado_y_despues_el_aislado_llegan_al_LIS_como_un_solo_informe()
    {
        await Receive(Messages.CultureGrowth);
        await Receive(Messages.CultureIsolate);

        _sent.Count.ShouldBe(2);

        _sent[0].ShouldBe(new CultureReport("2609017846B0", "GC", "C3", []), CultureReportComparer.Instance);

        var complete = _sent[1];
        complete.TestCode.ShouldBe("GC");
        complete.StatusCode.ShouldBe("C3");
        var isolate = complete.Isolates.ShouldHaveSingleItem();
        isolate.OrganismCode.ShouldBe("PSEAER");
        isolate.Antibiogram.Count.ShouldBe(8);

        await _lis.DidNotReceiveWithAnyArgs().SaveResultsAsync(default!, default!, default);
    }

    [Fact]
    public async Task Un_segundo_aislado_se_suma_y_reenviar_uno_no_lo_duplica()
    {
        await Receive(Messages.CultureGrowth);
        await Receive(Messages.CultureIsolate);
        await Receive(
        [
            "O|1|2609017846B0^2^KLEPNE^E||^^^ISOLATE RESULT|||||||||GC",
            "R|1|^^^ID|^KLEPNE|||||F",
            "R|2|^^^AST^^MEM^,|^^R^^R^bnf|||||F",
            "L|1|N"
        ]);
        await Receive(Messages.CultureIsolate);

        var last = _sent[^1];
        last.StatusCode.ShouldBe("C3");
        last.Isolates.Select(isolate => (isolate.Number, isolate.OrganismCode)).ShouldBe([(1, "PSEAER"), (2, "KLEPNE")]);
    }

    [Fact]
    public async Task Un_aislado_sin_estado_previo_se_informa_igual()
    {
        await Receive(Messages.CultureIsolate);

        var report = _sent.ShouldHaveSingleItem();
        report.StatusCode.ShouldBeNull();
        report.Isolates.ShouldHaveSingleItem().Number.ShouldBe(1);
    }

    [Fact]
    public async Task Si_el_LIS_falla_el_proximo_mensaje_manda_tambien_lo_que_no_llego()
    {
        _lis.SaveCultureAsync(Arg.Any<CultureReport>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("labcore-api caida")), Task.CompletedTask);

        await Receive(Messages.CultureGrowth);
        await Receive(Messages.CultureIsolate);

        var delivered = (CultureReport)_lis.ReceivedCalls()
            .Last(call => call.GetMethodInfo().Name == nameof(ILisGateway.SaveCultureAsync))
            .GetArguments()[0]!;
        delivered.StatusCode.ShouldBe("C3");
        delivered.Isolates.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Los_resultados_simples_siguen_por_el_camino_de_siempre()
    {
        await Receive(Messages.SimpleResult);

        await _lis.Received(1).SaveResultsAsync(
            "350542823B8",
            Arg.Is<IReadOnlyList<InstrumentResult>>(results => results.Single().InstrumentCode == "RTO" && results.Single().Value == "--"),
            Arg.Any<CancellationToken>());
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Un_aislado_sin_cultivo_de_origen_va_al_unico_cultivo_del_tubo()
    {
        await Receive(Messages.CultureGrowth);
        await Receive(
        [
            "O|1|2609017846B0^1^PSEAER||^^^ISOLATE RESULT",
            "R|1|^^^AST^^MEM^,|^^S^^S^bnf|||||F",
            "L|1|N"
        ]);

        _sent[^1].TestCode.ShouldBe("GC");
        _sent[^1].Isolates.ShouldHaveSingleItem();
    }

    /// <summary>Los records con listas no se comparan por contenido: comparo lo que importa.</summary>
    private sealed class CultureReportComparer : IEqualityComparer<CultureReport>
    {
        public static readonly CultureReportComparer Instance = new();

        public bool Equals(CultureReport? x, CultureReport? y) =>
            x is not null && y is not null &&
            x.Barcode == y.Barcode && x.TestCode == y.TestCode && x.StatusCode == y.StatusCode &&
            x.Isolates.Count == y.Isolates.Count;

        public int GetHashCode(CultureReport obj) => obj.TestCode.GetHashCode();
    }
}
