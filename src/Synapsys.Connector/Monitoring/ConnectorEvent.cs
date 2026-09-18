namespace Synapsys.Connector.Monitoring;

/// <summary>Un evento semantico del conector (conexion, consulta, resultados, error, cambio de estado).</summary>
public sealed record ConnectorEvent(string Level, string Kind, string Message, DateTimeOffset Timestamp)
{
    public static ConnectorEvent Info(string kind, string message) => new("info", kind, message, DateTimeOffset.UtcNow);

    public static ConnectorEvent Warn(string kind, string message) => new("warn", kind, message, DateTimeOffset.UtcNow);

    public static ConnectorEvent Error(string kind, string message) => new("error", kind, message, DateTimeOffset.UtcNow);
}
