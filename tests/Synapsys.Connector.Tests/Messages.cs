using Synapsys.Connector.Astm;

namespace Synapsys.Connector.Tests;

/// <summary>Transmisiones reales (logs de Synapsys y de Epicenter) para los tests.</summary>
internal static class Messages
{
    /// <summary>Resultado simple: el estudio RTO va en el O; el R solo dice OTHER y trae el codigo de resultado.</summary>
    public static readonly string[] SimpleResult =
    [
        @"H|\^&|||Becton Dickinson||||||||V1.0|20260123140126",
        "P|1||18548070||NEIMAN FABIANA JUDITH||19661226|F||||||||||||||||||||||||A|35",
        "O|1|350542823B8||^^^RTO|R||20260121000000|||||||20260121000000|ORINA|||A||||20260123134456",
        "R|1|^^^OTHER|--|||||F",
        "L|1|N"
    ];

    /// <summary>Cultivo general GC de la muestra 2609017846B0, positivo (C3).</summary>
    public static readonly string[] CultureGrowth =
    [
        @"H|\^&|||Synapsys||||||||V1.0|20260923083312",
        "O|1|2609017846B0||^^^GC|R||20260917124433|||||||20260917124433|tej|||||||20260923083312",
        "C|1||TENDON MIEMBRO INFERIOR DERECHO|S",
        "R|1|^^^GND|C3|||||F",
        "L|1|N"
    ];

    /// <summary>Aislado 1 del cultivo GC de la misma muestra: Pseudomonas aeruginosa con antibiograma.</summary>
    public static readonly string[] CultureIsolate =
    [
        @"H|\^&|||Synapsys||||||||V1.0|20260923083313",
        "O|1|2609017846B0^1^PSEAER^E||^^^ISOLATE RESULT|||20260917124433||||||GC|20260917124433|tej|||||||20260923083313||||||.",
        "C|1||TENDON MIEMBRO INFERIOR DERECHO|S",
        "R|1|^^^ID|^PSEAER|||||F",
        "R|2|^^^AST^^ATM^,|^^S^^S^bnf|||||F",
        "R|3|^^^AST^^CAZ^,|^^S^^S^bnf|||||F",
        "R|4|^^^AST^^CIP^,|^^S^^S^bnf|||||F",
        "R|5|^^^AST^^FEP^,|^^S^^S^bnf|||||F",
        "R|6|^^^AST^^IPM^,|^^S^^S^bnf|||||F",
        "R|7|^^^AST^^MEM^,|^^S^^S^bnf|||||F",
        "R|8|^^^AST^^TZP^,|^^S^^S^bnf|||||F",
        "R|9|^^^AST^^CT^,|^^S^^S^bnf|||||F",
        "L|1|N"
    ];

    /// <summary>Aislado de Epicenter: el cultivo de origen viene con repeticiones (HCI\PLUSAEF^fecha).</summary>
    public static readonly string[] EpicenterIsolate =
    [
        @"H|\^&|||Becton Dickinson||||||||V1.0|20260123135806",
        "P|1||10460606||GOMEZ RAMON ALBERTO||19520307|M||||||||||||||||||||||||1101|80",
        @"O|1|84005406826^1^EC||^^^ISOLATE RESULT|||20260122000000||||||HCI\PLUSAEF^20260122005227|20260122000000|hem|||I||||20260123134132||||||UNK",
        "R|1|^^^ID|^EC^^^^^^^MBT_ID|||||F",
        "R|2|^^^AST^^AMC^.|^^S^S^^ENTEROB|||||F",
        "R|5|^^^AST^^CIP^.|^^R^R^^ENTEROB|||||F",
        "L|1|N"
    ];

    public static IReadOnlyList<AstmRecord> Records(params string[] lines) =>
        lines.Select(line => AstmRecord.Parse(line, AstmSeparators.Default)).ToList();
}
