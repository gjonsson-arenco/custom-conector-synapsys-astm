namespace Synapsys.Connector.Runtime;

/// <summary>Estado del puerto ASTM del conector.</summary>
public enum PortState
{
    /// <summary>El puerto esta cerrado; no se escucha ni se conecta.</summary>
    Stopped,

    /// <summary>Se esta abriendo el puerto.</summary>
    Starting,

    /// <summary>Abierto y esperando a Synapsys (escuchando o intentando conectar).</summary>
    Listening,

    /// <summary>Con una sesion Synapsys activa.</summary>
    Connected,

    /// <summary>El puerto se cayo por un error (por ejemplo, puerto en uso).</summary>
    Faulted
}

/// <summary>Foto del estado del conector para el front.</summary>
public sealed record ConnectorStatus(
    PortState State,
    string Mode,
    string Host,
    int Port,
    string? Remote,
    DateTimeOffset? StartedAt,
    long TransmissionsReceived,
    long ResponsesSent,
    string? LastError,
    bool BlockedByLicense);
