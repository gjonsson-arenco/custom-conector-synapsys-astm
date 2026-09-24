using System.Security.Cryptography;

namespace Arenco.Licensing.Tests;

/// <summary>
/// Una licencia habilita al conector si la firmo Arenco, es de este producto y de esta maquina, y
/// esta vigente o dentro de la tolerancia. Se avisa desde 30 dias antes y hasta 30 dias despues.
/// </summary>
public sealed class LicenseEvaluatorTests : IDisposable
{
    private const string Product = "synapsys-connector";
    private const string Machine = "ABCD-EFGH-JKMN-PQRS";

    private static readonly DateOnly Expires = new(2027, 3, 31);

    private readonly ECDsa _privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly LicenseEvaluator _evaluator;

    public LicenseEvaluatorTests()
    {
        var publicKey = LicenseKey.ImportPublicKey(Convert.ToBase64String(_privateKey.ExportSubjectPublicKeyInfo()));
        _evaluator = new LicenseEvaluator(publicKey, Product, Machine, warningDays: 30, graceDays: 30);
    }

    public void Dispose() => _privateKey.Dispose();

    private string Key(string product = Product, string machine = Machine, ECDsa? signer = null) =>
        LicenseKey.Sign(
            new LicensePayload(LicensePayload.CurrentVersion, product, "Hospital", machine, new DateOnly(2026, 3, 31), Expires),
            signer ?? _privateKey);

    private LicenseStatus On(DateOnly today) => _evaluator.Evaluate(Key(), today);

    [Fact]
    public void Sin_licencia_no_habilita_y_da_el_codigo_de_maquina()
    {
        var status = _evaluator.Evaluate(null, Expires);

        status.State.ShouldBe(LicenseState.Missing);
        status.IsUsable.ShouldBeFalse();
        status.Message.ShouldContain(Machine);
    }

    [Theory]
    [InlineData(-31, LicenseState.Valid)]
    [InlineData(-30, LicenseState.Valid)]
    [InlineData(-29, LicenseState.ExpiringSoon)]
    [InlineData(0, LicenseState.ExpiringSoon)]
    [InlineData(1, LicenseState.Grace)]
    [InlineData(30, LicenseState.Grace)]
    public void Habilita_vigente_por_vencer_y_en_tolerancia(int daysAfterExpiry, LicenseState expected)
    {
        var status = On(Expires.AddDays(daysAfterExpiry));

        status.State.ShouldBe(expected);
        status.IsUsable.ShouldBeTrue();
        status.DaysRemaining.ShouldBe(-daysAfterExpiry);
        status.GraceUntil.ShouldBe(Expires.AddDays(30));
    }

    [Fact]
    public void Pasada_la_tolerancia_no_habilita()
    {
        var status = On(Expires.AddDays(31));

        status.State.ShouldBe(LicenseState.Expired);
        status.IsUsable.ShouldBeFalse();
    }

    [Fact]
    public void Una_licencia_de_otro_producto_no_habilita()
    {
        var status = _evaluator.Evaluate(Key(product: "otro-conector"), Expires);

        status.State.ShouldBe(LicenseState.WrongProduct);
        status.IsUsable.ShouldBeFalse();
    }

    [Fact]
    public void Una_licencia_de_otra_maquina_no_habilita()
    {
        var status = _evaluator.Evaluate(Key(machine: "ZZZZ-ZZZZ-ZZZZ-ZZZZ"), Expires);

        status.State.ShouldBe(LicenseState.WrongMachine);
        status.IsUsable.ShouldBeFalse();
    }

    [Fact]
    public void El_codigo_de_maquina_se_compara_sin_guiones_ni_mayusculas()
    {
        _evaluator.Evaluate(Key(machine: "abcdefghjkmnpqrs"), Expires).State.ShouldBe(LicenseState.ExpiringSoon);
    }

    [Fact]
    public void Una_clave_alterada_no_habilita()
    {
        // Se reemplaza el payload por otro con mas vigencia, conservando la firma original.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = Key();
        var forged = LicenseKey.Sign(
            new LicensePayload(LicensePayload.CurrentVersion, Product, "Hospital", Machine, new DateOnly(2026, 3, 31), new DateOnly(2099, 1, 1)),
            other);
        var tampered = forged.Split('.')[0] + "." + original.Split('.')[1];

        _evaluator.Evaluate(tampered, Expires).State.ShouldBe(LicenseState.Invalid);
    }

    [Fact]
    public void Una_clave_firmada_por_otra_clave_privada_no_habilita()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        _evaluator.Evaluate(Key(signer: other), Expires).State.ShouldBe(LicenseState.Invalid);
    }

    [Theory]
    [InlineData("basura")]
    [InlineData("a.b.c")]
    [InlineData("!!!.???")]
    public void Una_clave_mal_formada_no_habilita(string key)
    {
        _evaluator.Evaluate(key, Expires).State.ShouldBe(LicenseState.Invalid);
    }

    [Fact]
    public void Acepta_la_clave_partida_en_lineas()
    {
        var key = Key();
        var wrapped = string.Join("\r\n", key.Chunk(40).Select(c => new string(c))) + "\n";

        _evaluator.Evaluate(wrapped, Expires.AddDays(-60)).State.ShouldBe(LicenseState.Valid);
    }
}
