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
/// La autovalidacion se decide en el conector: el resultado que acepta una regla, de una prueba con
/// la autovalidacion habilitada en el mapeo del LIS, va a labcore-api
/// con estado 4 y el motivo (para AppLog); todo lo demas sigue yendo como cargado (2).
/// </summary>
public sealed class LabcoreGatewayAutoValidationTests : IDisposable
{
    private const string Barcode = "2609017846B0";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "synapsys-autoval-" + Guid.NewGuid().ToString("N"));
    private readonly StubLabcore _labcore = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private LabcoreGateway Gateway(params AutoValidationRule[] rules) => Gateway(enabled: true, rules);

    private LabcoreGateway Gateway(bool enabled, params AutoValidationRule[] rules)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("labcore").Returns(_ => new HttpClient(_labcore) { BaseAddress = new Uri("http://labcore/api/v1/") });

        CodeCatalog Catalog(string name, params CodeMapping[] rows) =>
            new(new SettingsFile<CodeCatalogSettings>(Path.Combine(_directory, name), () => new CodeCatalogSettings { Mappings = rows }, NullLogger.Instance));

        return new LabcoreGateway(
            factory,
            Options.Create(new LabcoreOptions { UserId = 5 }),
            new SettingsFile<InstrumentSettings>(Path.Combine(_directory, "instrument.json"), () => new InstrumentSettings { InstrumentId = 12 }, NullLogger.Instance),
            new CodeCatalogs(
                Catalog("results.json", new CodeMapping("G8", "Cocos Gram positivos"), new CodeMapping("NEGB", "NO SE OBTUVO DESARROLLO BACTERIANO")),
                Catalog("organisms.json"),
                Catalog("antibiotics.json")),
            new SettingsFile<AutoValidationSettings>(
                Path.Combine(_directory, "autovalidation.json"),
                () => new AutoValidationSettings { Enabled = enabled, Rules = rules },
                NullLogger.Instance),
            new ConnectorMonitor(),
            NullLogger<LabcoreGateway>.Instance);
    }

    private static InstrumentResult Result(string value, params string[] flags) => new("RTO", value, flags);

    private JsonElement SavedResult() =>
        _labcore.Posts.ShouldHaveSingleItem().Body.RootElement.GetProperty("results").EnumerateArray().ShouldHaveSingleItem();

    [Fact]
    public async Task Prueba_tipo_de_muestra_y_resultado_de_la_regla_se_guarda_validado_con_el_motivo()
    {
        await Gateway(new AutoValidationRule("CG", "HEMI", "G8")).SaveResultsAsync(Barcode, [Result("G8")], CancellationToken.None);

        var saved = SavedResult();
        saved.GetProperty("testCode").GetString().ShouldBe("CG");
        saved.GetProperty("result").GetString().ShouldBe("Cocos Gram positivos");
        saved.GetProperty("status").GetInt32().ShouldBe(4);
        saved.GetProperty("autoValidation").GetString().ShouldBe("CG en HEMI = G8");
    }

    [Theory]
    [InlineData("CG", "HEMI", "NEGB")] // otro resultado
    [InlineData("CG", "ORINA", "G8")]  // otro tipo de muestra
    [InlineData("XX", "HEMI", "G8")]   // otra prueba
    public async Task Si_la_regla_no_coincide_en_todo_queda_cargado(string test, string sampleType, string result)
    {
        await Gateway(new AutoValidationRule(test, sampleType, result)).SaveResultsAsync(Barcode, [Result("G8")], CancellationToken.None);

        var saved = SavedResult();
        saved.GetProperty("status").GetInt32().ShouldBe(2);
        saved.GetProperty("autoValidation").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Sin_tipo_de_muestra_la_regla_vale_para_cualquiera_y_no_consulta_la_muestra()
    {
        await Gateway(new AutoValidationRule("cg", null, "g8")).SaveResultsAsync(Barcode, [Result("G8")], CancellationToken.None);

        SavedResult().GetProperty("status").GetInt32().ShouldBe(4);
        _labcore.SampleReads.ShouldBe(0);
    }

    [Fact]
    public async Task Apagada_no_valida_nada()
    {
        await Gateway(enabled: false, new AutoValidationRule("CG", "HEMI", "G8")).SaveResultsAsync(Barcode, [Result("G8")], CancellationToken.None);

        SavedResult().GetProperty("status").GetInt32().ShouldBe(2);
        _labcore.SampleReads.ShouldBe(0);
    }

    [Fact]
    public async Task Sin_la_autovalidacion_habilitada_en_el_mapeo_del_LIS_la_regla_no_alcanza()
    {
        await Gateway(new AutoValidationRule("GLU", null, "90"))
            .SaveResultsAsync(Barcode, [new InstrumentResult("GLU", "90", [])], CancellationToken.None);

        SavedResult().GetProperty("status").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Un_resultado_con_flags_del_equipo_no_se_autovalida()
    {
        await Gateway(new AutoValidationRule("CG", null, "G8")).SaveResultsAsync(Barcode, [Result("G8", "R")], CancellationToken.None);

        SavedResult().GetProperty("status").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Si_no_se_puede_leer_la_muestra_no_se_autovalida_por_tipo()
    {
        _labcore.SampleFails = true;

        await Gateway(new AutoValidationRule("CG", "HEMI", "G8")).SaveResultsAsync(Barcode, [Result("G8")], CancellationToken.None);

        SavedResult().GetProperty("status").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Un_cultivo_con_solo_el_estado_se_autovalida()
    {
        await Gateway(new AutoValidationRule("CGR", "HEMI", "NEGB"))
            .SaveCultureAsync(new CultureReport(Barcode, "GC", "NEGB", []), CancellationToken.None);

        var body = _labcore.Posts.ShouldHaveSingleItem().Body.RootElement;
        body.GetProperty("status").GetInt32().ShouldBe(4);
        body.GetProperty("autoValidation").GetString().ShouldBe("CGR en HEMI = NEGB");
    }

    [Fact]
    public async Task Un_cultivo_con_aislados_no_se_autovalida()
    {
        var culture = new CultureReport(Barcode, "GC", "NEGB", [new IsolateReport(1, "PSEAER", [], [])]);

        await Gateway(new AutoValidationRule("CGR", null, "NEGB")).SaveCultureAsync(culture, CancellationToken.None);

        _labcore.Posts.ShouldHaveSingleItem().Body.RootElement.GetProperty("status").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void Una_regla_incompleta_o_repetida_no_se_guarda()
    {
        var settings = new AutoValidationSettings
        {
            Rules =
            [
                new AutoValidationRule("CG", "HEMI", "G8"),
                new AutoValidationRule("cg", "hemi", "g8"),
                new AutoValidationRule("CG", null, " ")
            ]
        };

        settings.Validate().Keys.ShouldBe(["rules[1]", "rules[2]"], ignoreOrder: true);
    }

    /// <summary>
    /// labcore-api de mentira: mapeo RTO => CG y GC => CGR (con autovalidacion habilitada) y GLU (sin),
    /// un tubo de hemocultivo y los POST.
    /// </summary>
    private sealed class StubLabcore : HttpMessageHandler
    {
        public List<(string Path, JsonDocument Body)> Posts { get; } = [];

        public int SampleReads { get; private set; }

        public bool SampleFails { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get && path == "/api/v1/instruments/12/tests")
            {
                return Json("""
                    { "tests": [
                        { "testCode": "CG", "incomingCode": "RTO", "outgoingCode": "RTO", "factor": 1, "autovalidationEnabled": true },
                        { "testCode": "CGR", "incomingCode": "GC", "outgoingCode": "GC", "factor": 1, "autovalidationEnabled": true },
                        { "testCode": "GLU", "incomingCode": "GLU", "outgoingCode": "GLU", "factor": 1, "autovalidationEnabled": false }
                    ] }
                    """);
            }

            if (request.Method == HttpMethod.Get && path == $"/api/v1/samples/{Barcode}")
            {
                SampleReads++;
                return SampleFails
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : Json("""{ "sampleTypeCode": "HEMI ", "tests": [] }""");
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
