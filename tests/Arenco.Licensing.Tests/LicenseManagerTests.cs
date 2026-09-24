using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Arenco.Licensing.Tests;

/// <summary>La licencia instalada: se lee del archivo, se evalua con la fecha del dia y se reemplaza desde el front.</summary>
public sealed class LicenseManagerTests : IDisposable
{
    private const string Machine = "ABCD-EFGH-JKMN-PQRS";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arenco-license-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly LicenseOptions _options;

    public LicenseManagerTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _options = new LicenseOptions
        {
            Product = "synapsys-connector",
            Path = Path.Combine(_directory, "license.lic"),
            PublicKey = Convert.ToBase64String(_privateKey.ExportSubjectPublicKeyInfo())
        };
    }

    public void Dispose()
    {
        _privateKey.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private LicenseManager Manager() => new(_options, Machine, _time, NullLogger<LicenseManager>.Instance);

    private string Key(DateOnly expires, string product = "synapsys-connector") =>
        LicenseKey.Sign(new LicensePayload(LicensePayload.CurrentVersion, product, "Hospital", Machine, new DateOnly(2026, 9, 1), expires), _privateKey);

    [Fact]
    public void Instala_una_licencia_valida_la_guarda_y_avisa()
    {
        var manager = Manager();
        LicenseStatus? changed = null;
        manager.Changed += status => changed = status;

        var status = manager.Install(Key(new DateOnly(2027, 9, 30)));

        status.State.ShouldBe(LicenseState.Valid);
        changed.ShouldNotBeNull();
        manager.Current.State.ShouldBe(LicenseState.Valid);
        Manager().Current.State.ShouldBe(LicenseState.Valid);
    }

    [Fact]
    public void Una_clave_que_no_sirve_no_pisa_a_la_instalada()
    {
        var manager = Manager();
        manager.Install(Key(new DateOnly(2027, 9, 30)));
        var changes = 0;
        manager.Changed += _ => changes++;

        var status = manager.Install(Key(new DateOnly(2027, 9, 30), product: "otro-conector"));

        status.State.ShouldBe(LicenseState.WrongProduct);
        changes.ShouldBe(0);
        manager.Current.State.ShouldBe(LicenseState.Valid);
    }

    [Fact]
    public void Una_licencia_vencida_fuera_de_tolerancia_no_se_instala()
    {
        var manager = Manager();

        manager.Install(Key(new DateOnly(2026, 8, 1))).State.ShouldBe(LicenseState.Expired);

        File.Exists(_options.Path).ShouldBeFalse();
        manager.Current.State.ShouldBe(LicenseState.Missing);
    }

    [Fact]
    public void El_vencimiento_se_recalcula_con_la_fecha_del_dia()
    {
        var manager = Manager();
        manager.Install(Key(new DateOnly(2026, 10, 15)));
        manager.Current.State.ShouldBe(LicenseState.ExpiringSoon);

        _time.Advance(TimeSpan.FromDays(23));
        manager.Current.State.ShouldBe(LicenseState.Grace);

        _time.Advance(TimeSpan.FromDays(30));
        manager.Current.State.ShouldBe(LicenseState.Expired);
        manager.Current.IsUsable.ShouldBeFalse();
    }

    [Fact]
    public void El_codigo_de_maquina_es_estable_y_con_formato()
    {
        var code = MachineCode.FromId("4c4c4544-0042-3510-8052-b4c04f4d4e32");

        code.ShouldMatch("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$");
        MachineCode.FromId("4C4C4544-0042-3510-8052-B4C04F4D4E32 ").ShouldBe(code);
        MachineCode.FromId("otra-maquina").ShouldNotBe(code);
        MachineCode.Current.Length.ShouldBe(19);
    }
}
