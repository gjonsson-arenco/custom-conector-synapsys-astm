using System.Text.Json.Serialization;

namespace Synapsys.Connector.Configuration;

/// <summary>settings/instrument.json: que analizador del LIS es este conector.</summary>
public sealed record InstrumentSettings
{
    /// <summary>Analizador (Analizadores.a_id): clave del mapeo de codigos y origen de los resultados.</summary>
    public int InstrumentId { get; init; }

    [JsonIgnore]
    public bool IsConfigured => InstrumentId > 0;

    public Dictionary<string, string[]> Validate() =>
        InstrumentId > 0
            ? []
            : new() { ["instrumentId"] = ["Tiene que ser el id del analizador en el LIS (mayor que 0)."] };
}

/// <summary>settings/communication.json: como se habla con Synapsys (socket + ASTM).</summary>
public sealed record CommunicationSettings
{
    public TransportOptions Transport { get; init; } = new();

    public AstmOptions Astm { get; init; } = new();

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();

        void Check(bool ok, string field, string message)
        {
            if (!ok)
            {
                errors[field] = [message];
            }
        }

        Check(Transport.Mode is "Server" or "Client", "transport.mode", "Tiene que ser Server o Client.");
        Check(!string.IsNullOrWhiteSpace(Transport.Host), "transport.host", "Falta el host.");
        Check(Transport.Port is > 0 and <= 65535, "transport.port", "Tiene que estar entre 1 y 65535.");
        Check(Transport.ReconnectSeconds >= 1, "transport.reconnectSeconds", "Minimo 1 segundo.");

        Check(Astm.Level is "LowLevel" or "HighLevel", "astm.level", "Tiene que ser LowLevel o HighLevel.");
        Check(Astm.ReceiveTimeoutSeconds >= 1, "astm.receiveTimeoutSeconds", "Minimo 1 segundo.");
        Check(Astm.MaxRetries >= 0, "astm.maxRetries", "No puede ser negativo.");
        Check(Astm.FieldSeparator.Length == 1, "astm.fieldSeparator", "Tiene que ser un solo caracter.");
        Check(Astm.ComponentSeparator.Length == 1, "astm.componentSeparator", "Tiene que ser un solo caracter.");
        Check(Astm.RepeatSeparator.Length == 1, "astm.repeatSeparator", "Tiene que ser un solo caracter.");

        var separators = new[] { Astm.FieldSeparator, Astm.ComponentSeparator, Astm.RepeatSeparator };
        Check(separators.Distinct().Count() == separators.Length, "astm.separators", "Los separadores tienen que ser distintos.");

        return errors;
    }
}

/// <summary>
/// settings/petitions.json: pulling de peticiones. El LIS deja en InstrumentPetitionQueue las
/// muestras que hay que mandar al equipo; el conector las toma y las baja con la linea libre.
/// </summary>
public sealed record PetitionSettings
{
    /// <summary>Apagado por defecto: una instalacion existente no empieza a bajar muestras sola.</summary>
    public bool Enabled { get; init; }

    /// <summary>Cada cuanto se consulta la tabla cuando no hay nada pendiente.</summary>
    public int PollSeconds { get; init; } = 10;

    public Dictionary<string, string[]> Validate() =>
        PollSeconds is >= 1 and <= 3600
            ? []
            : new() { ["pollSeconds"] = ["Tiene que estar entre 1 y 3600 segundos."] };
}

/// <summary>
/// settings/autovalidation.json: los resultados simples (negativos, recuentos) que se guardan ya
/// validados. El conector evalua las reglas antes de mandar el resultado al LIS.
/// </summary>
public sealed record AutoValidationSettings
{
    /// <summary>Apagado por defecto: nada se valida solo hasta que alguien lo prenda.</summary>
    public bool Enabled { get; init; }

    public IReadOnlyList<AutoValidationRule> Rules { get; init; } = [];

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < Rules.Count; index++)
        {
            var rule = Rules[index];

            if (string.IsNullOrWhiteSpace(rule.TestCode) || string.IsNullOrWhiteSpace(rule.Result))
            {
                errors[$"rules[{index}]"] = ["La prueba y el resultado son obligatorios."];
            }
            else if (!seen.Add(rule.Describe()))
            {
                errors[$"rules[{index}]"] = [$"La regla {rule.Describe()} esta repetida."];
            }
        }

        return errors;
    }
}

/// <summary>
/// Una regla: si la prueba del LIS, en ese tipo de muestra, trae ese resultado, se autovalida.
/// </summary>
/// <param name="TestCode">Codigo de la prueba en el LIS (Pruebas.p_codigo), el del mapeo de tests.</param>
/// <param name="SampleType">Codigo del tipo de muestra (HEMI...). Vacio = cualquiera.</param>
/// <param name="Result">
/// Valor tal cual lo manda el equipo, antes del mapeo de resultados y del factor: G8, NEGB, 0.
/// En un cultivo es el codigo del estado.
/// </param>
public sealed record AutoValidationRule(string TestCode, string? SampleType, string Result)
{
    [JsonIgnore]
    public bool NeedsSampleType => !string.IsNullOrWhiteSpace(SampleType);

    public bool Matches(string testCode, string result) =>
        Same(TestCode, testCode) && Same(Result, result);

    public bool AcceptsSample(string? sampleType) => !NeedsSampleType || Same(SampleType, sampleType);

    /// <summary>Como queda escrita en la bitacora del LIS: "CGR en HEMI = G8".</summary>
    public string Describe() => NeedsSampleType
        ? $"{TestCode.Trim()} en {SampleType!.Trim()} = {Result.Trim()}"
        : $"{TestCode.Trim()} = {Result.Trim()}";

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Traduccion de un codigo que manda el equipo a su texto para el LIS.</summary>
public sealed record CodeMapping(string Code, string Description);

/// <summary>
/// Un catalogo codigo => descripcion: settings/result-mappings.json, organisms.json o antibiotics.json.
/// </summary>
public sealed record CodeCatalogSettings
{
    public IReadOnlyList<CodeMapping> Mappings { get; init; } = [];
}
