using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Synapsys.Connector.Configuration;
using Synapsys.Connector.Lis;
using Synapsys.Connector.Monitoring;

namespace Synapsys.Connector.Tests;

/// <summary>
/// Lo que le llega a labcore-api: el cultivo con el codigo de prueba del LIS, los nombres de los
/// catalogos y overwrite, porque cada envio es el informe completo.
/// </summary>
public sealed class LabcoreGatewayCultureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "synapsys-gateway-" + Guid.NewGuid().ToString("N"));
    private readonly StubLabcore _labcore = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private CodeCatalog Catalog(string name, params (string Code, string Description)[] rows) =>
        new(new SettingsFile<CodeCatalogSettings>(
            Path.Combine(_directory, name),
            () => new CodeCatalogSettings { Mappings = rows.Select(row => new CodeMapping(row.Code, row.Description)).ToList() },
            NullLogger.Instance));

    private LabcoreGateway Gateway()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("labcore").Returns(_ => new HttpClient(_labcore) { BaseAddress = new Uri("http://labcore/api/v1/") });

        return new LabcoreGateway(
            factory,
            Options.Create(new LabcoreOptions { UserId = 5 }),
            new SettingsFile<InstrumentSettings>(Path.Combine(_directory, "instrument.json"), () => new InstrumentSettings { InstrumentId = 12 }, NullLogger.Instance),
            new CodeCatalogs(
                Catalog("results.json", ("C3", "Positivo (Bacilos Gram Negativos)")),
                Catalog("organisms.json", ("PSEAER", "Pseudomonas aeruginosa")),
                Catalog("antibiotics.json", ("ATM", "Aztreonam"))),
            new SettingsFile<AutoValidationSettings>(Path.Combine(_directory, "autovalidation.json"), () => new AutoValidationSettings(), NullLogger.Instance),
            new ConnectorMonitor(),
            NullLogger<LabcoreGateway>.Instance);
    }

    private static CultureReport Culture(string testCode = "GC") => new(
        "2609017846B0",
        testCode,
        "C3",
        [
            new IsolateReport(1, "PSEAER", [],
            [
                new Susceptibility("ATM", "S", null),
                new Susceptibility("CT", "S", null)
            ])
        ]);

    [Fact]
    public async Task Manda_el_cultivo_traducido_al_codigo_de_prueba_del_LIS()
    {
        await Gateway().SaveCultureAsync(Culture(), CancellationToken.None);

        var (path, body) = _labcore.Posts.ShouldHaveSingleItem();
        path.ShouldBe("/api/v1/samples/2609017846B0/cultures");

        var root = body.RootElement;
        root.GetProperty("userId").GetInt32().ShouldBe(5);
        root.GetProperty("instrumentId").GetInt32().ShouldBe(12);
        root.GetProperty("overwrite").GetBoolean().ShouldBeTrue();
        root.GetProperty("testCode").GetString().ShouldBe("CGR");
        root.GetProperty("summary").GetString().ShouldBe("Positivo (Bacilos Gram Negativos)");

        var isolate = root.GetProperty("isolates").EnumerateArray().ShouldHaveSingleItem();
        isolate.GetProperty("number").GetInt32().ShouldBe(1);
        isolate.GetProperty("organism").GetString().ShouldBe("Pseudomonas aeruginosa");

        // CT no esta en el catalogo: va el codigo para no perder el renglon.
        isolate.GetProperty("antibiotics").EnumerateArray()
            .Select(row => (row.GetProperty("name").GetString(), row.GetProperty("interpretation").GetString()))
            .ShouldBe([("Aztreonam", "S"), ("CT", "S")]);
    }

    [Fact]
    public async Task Un_cultivo_sin_mapeo_de_prueba_no_se_informa()
    {
        await Gateway().SaveCultureAsync(Culture(testCode: "XX"), CancellationToken.None);

        _labcore.Posts.ShouldBeEmpty();
    }

    /// <summary>labcore-api de mentira: el mapeo del analizador 12 y los POST que recibe.</summary>
    private sealed class StubLabcore : HttpMessageHandler
    {
        public List<(string Path, JsonDocument Body)> Posts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get && path == "/api/v1/instruments/12/tests")
            {
                return Json("""{ "tests": [ { "testCode": "CGR", "incomingCode": "GC", "outgoingCode": "GC", "factor": 1 } ] }""");
            }

            if (request.Method == HttpMethod.Post)
            {
                Posts.Add((path, JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))));
                return Json("{}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
