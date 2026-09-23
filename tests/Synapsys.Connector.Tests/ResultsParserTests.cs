using Synapsys.Connector.Astm;
using Synapsys.Connector.Flows;
using Synapsys.Connector.Lis;

namespace Synapsys.Connector.Tests;

/// <summary>
/// Synapsys no dice en el R que prueba es sino de que categoria: estos tests fijan como se lee
/// cada caso real para que un resultado no termine en el test equivocado.
/// </summary>
public class ResultsParserTests
{
    private static ParsedResults Parse(string[] lines) =>
        ResultsParser.Parse(Messages.Records(lines), AstmSeparators.Default);

    [Fact]
    public void Un_OTHER_es_resultado_de_la_prueba_del_O()
    {
        var parsed = Parse(Messages.SimpleResult);

        var result = parsed.Results["350542823B8"].ShouldHaveSingleItem();
        result.InstrumentCode.ShouldBe("RTO");
        result.Value.ShouldBe("--");
        parsed.CultureStatuses.ShouldBeEmpty();
        parsed.Isolates.ShouldBeEmpty();
        parsed.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Un_GND_es_el_estado_del_cultivo_del_O()
    {
        var parsed = Parse(Messages.CultureGrowth);

        parsed.CultureStatuses.ShouldHaveSingleItem().ShouldBe(new CultureStatus("2609017846B0", "GC", "C3"));
        parsed.Results.ShouldBeEmpty();
        parsed.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Un_aislado_trae_numero_organismo_cultivo_de_origen_y_antibiograma()
    {
        var parsed = Parse(Messages.CultureIsolate);

        var update = parsed.Isolates.ShouldHaveSingleItem();
        update.Barcode.ShouldBe("2609017846B0");
        update.SourceTest.ShouldBe("GC");
        update.Isolate.Number.ShouldBe(1);
        update.Isolate.OrganismCode.ShouldBe("PSEAER");
        update.Isolate.Antibiogram.Select(row => row.AntibioticCode)
            .ShouldBe(["ATM", "CAZ", "CIP", "FEP", "IPM", "MEM", "TZP", "CT"]);
        update.Isolate.Antibiogram.ShouldAllBe(row => row.Interpretation == "S" && row.Mic == null);
        parsed.Results.ShouldBeEmpty();
        parsed.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Tambien_lee_el_aislado_de_Epicenter()
    {
        var update = Parse(Messages.EpicenterIsolate).Isolates.ShouldHaveSingleItem();

        update.Barcode.ShouldBe("84005406826");
        update.SourceTest.ShouldBe("HCI");
        update.Isolate.OrganismCode.ShouldBe("EC");
        update.Isolate.Antibiogram.ShouldBe(
        [
            new Susceptibility("AMC", "S", null),
            new Susceptibility("CIP", "R", null)
        ]);
    }

    [Fact]
    public void La_interpretacion_final_pisa_a_la_del_instrumento()
    {
        var update = Parse(
        [
            "O|1|T1^1^EC||^^^ISOLATE RESULT|||||||||GC",
            "R|1|^^^AST^^CIP^,|0.5^^S^^R^bnf|||||F",
            "L|1|N"
        ]).Isolates.ShouldHaveSingleItem();

        update.Isolate.Antibiogram.ShouldHaveSingleItem().ShouldBe(new Susceptibility("CIP", "R", "0.5"));
    }

    [Fact]
    public void Una_categoria_desconocida_en_un_aislado_queda_avisada_y_no_frena_al_resto()
    {
        var parsed = Parse(
        [
            "O|1|T1^1^KPN||^^^ISOLATE RESULT|||||||||GC",
            "R|1|^^^RES|^KPC|||||F",
            "R|2|^^^AST^^MEM^,|^^R^^R^bnf|||||F",
            "L|1|N"
        ]);

        parsed.Isolates.ShouldHaveSingleItem().Isolate.Antibiogram.ShouldHaveSingleItem().AntibioticCode.ShouldBe("MEM");
        parsed.Warnings.ShouldHaveSingleItem().ShouldContain("'RES'");
    }

    [Fact]
    public void Varios_aislados_en_una_transmision_salen_por_separado()
    {
        var parsed = Parse(
        [
            "O|1|T1^1^EC||^^^ISOLATE RESULT|||||||||GC",
            "R|1|^^^AST^^CIP^,|^^S^^S^bnf|||||F",
            "O|2|T1^2^KPN||^^^ISOLATE RESULT|||||||||GC",
            "R|1|^^^AST^^MEM^,|^^R^^R^bnf|||||F",
            "L|1|N"
        ]);

        parsed.Isolates.Select(update => (update.Isolate.Number, update.Isolate.OrganismCode))
            .ShouldBe([(1, "EC"), (2, "KPN")]);
    }
}
