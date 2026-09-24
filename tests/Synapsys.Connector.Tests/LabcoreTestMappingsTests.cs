using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Synapsys.Connector.Configuration;
using Synapsys.Connector.Lis;

namespace Synapsys.Connector.Tests;

/// <summary>
/// El mapeo del LIS se pide una vez y queda en memoria: las lecturas del front y del gateway no
/// vuelven a labcore-api, y lo que se edita desde el front queda en el cache.
/// </summary>
public sealed class LabcoreTestMappingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "synapsys-mappings-" + Guid.NewGuid().ToString("N"));
    private readonly StubLabcore _labcore = new();
    private readonly SettingsFile<InstrumentSettings> _instrument;
    private readonly LabcoreTestMappings _mappings;

    public LabcoreTestMappingsTests()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("labcore").Returns(_ => new HttpClient(_labcore) { BaseAddress = new Uri("http://labcore/api/v1/") });

        _instrument = new SettingsFile<InstrumentSettings>(
            Path.Combine(_directory, "instrument.json"), () => new InstrumentSettings { InstrumentId = 12 }, NullLogger.Instance);
        _mappings = new LabcoreTestMappings(
            factory, Options.Create(new LabcoreOptions()), _instrument, NullLogger<LabcoreTestMappings>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Front_y_gateway_leen_del_mismo_cache()
    {
        await _mappings.GetAsync(refresh: false, CancellationToken.None);
        await _mappings.GetAsync(refresh: false, CancellationToken.None);
        var index = await _mappings.GetIndexAsync(CancellationToken.None);

        _labcore.Reads.ShouldBe(1);
        index.ByIncoming.Keys.ShouldBe(["RTO"]);
    }

    [Fact]
    public async Task El_indice_solo_tiene_las_pruebas_activas()
    {
        var all = await _mappings.GetAsync(refresh: false, CancellationToken.None);
        var index = await _mappings.GetIndexAsync(CancellationToken.None);

        all.Tests.Count.ShouldBe(2);
        index.ByIncoming.ContainsKey("OLD").ShouldBeFalse();
    }

    [Fact]
    public async Task Una_edicion_desde_el_front_queda_en_el_cache()
    {
        await _mappings.GetAsync(refresh: false, CancellationToken.None);

        _labcore.Tests = """[ { "testCode": "CG", "incomingCode": "RTO2", "active": true } ]""";
        await _mappings.UpdateAsync("RTO", new TestMapping { IncomingCode = "RTO2", TestCode = "CG" }, CancellationToken.None);

        var reads = _labcore.Reads;
        var index = await _mappings.GetIndexAsync(CancellationToken.None);

        index.ByIncoming.Keys.ShouldBe(["RTO2"]);
        _labcore.Reads.ShouldBe(reads);
    }

    [Fact]
    public async Task Refresh_relee_del_LIS()
    {
        await _mappings.GetAsync(refresh: false, CancellationToken.None);
        await _mappings.GetAsync(refresh: true, CancellationToken.None);

        _labcore.Reads.ShouldBe(2);
    }

    [Fact]
    public async Task Otro_analizador_es_otro_mapeo()
    {
        await _mappings.GetAsync(refresh: false, CancellationToken.None);
        await _instrument.SaveAsync(new InstrumentSettings { InstrumentId = 13 });

        var mappings = await _mappings.GetAsync(refresh: false, CancellationToken.None);

        mappings.InstrumentId.ShouldBe(13);
        _labcore.Reads.ShouldBe(2);
    }

    /// <summary>labcore-api de mentira: el mapeo de cualquier analizador y las ediciones, que responden 204.</summary>
    private sealed class StubLabcore : HttpMessageHandler
    {
        public string Tests { get; set; } = """
            [
                { "testCode": "CG", "incomingCode": "RTO", "active": true },
                { "testCode": "CG", "incomingCode": "OLD", "active": false }
            ]
            """;

        public int Reads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            Reads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{ "name": "Synapsys", "tests": {{Tests}} }""", Encoding.UTF8, "application/json")
            });
        }
    }
}
