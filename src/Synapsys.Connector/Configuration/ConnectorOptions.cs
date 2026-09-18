namespace Synapsys.Connector.Configuration;

/// <summary>Como se establece el socket: escuchando (Server) o conectando (Client).</summary>
public sealed class TransportOptions
{
    public const string SectionName = "Transport";

    /// <summary>"Server" para escuchar en <see cref="Port"/>, "Client" para conectar a <see cref="Host"/>.</summary>
    public string Mode { get; set; } = "Server";

    /// <summary>En Server, interfaz de escucha. En Client, IP/host de Synapsys.</summary>
    public string Host { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 5150;

    /// <summary>Segundos antes de reintentar aceptar o reconectar cuando se cae el socket.</summary>
    public int ReconnectSeconds { get; set; } = 5;

    public bool IsClient => string.Equals(Mode, "Client", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Parametros del protocolo ASTM. El nivel elige el canal.</summary>
public sealed class AstmOptions
{
    public const string SectionName = "Astm";

    /// <summary>"LowLevel" (ENQ/ACK + frames con checksum) o "HighLevel" (mensaje completo VT..FS).</summary>
    public string Level { get; set; } = "LowLevel";

    /// <summary>Calcula y valida el checksum de cada frame (solo LowLevel).</summary>
    public bool UseChecksum { get; set; } = true;

    /// <summary>Espera maxima por un ACK o el proximo frame dentro de una transmision en curso.</summary>
    public int ReceiveTimeoutSeconds { get; set; } = 30;

    /// <summary>Reintentos ante NAK antes de abandonar una transmision.</summary>
    public int MaxRetries { get; set; } = 6;

    public string FieldSeparator { get; set; } = "|";
    public string ComponentSeparator { get; set; } = "^";
    public string RepeatSeparator { get; set; } = "\\";

    public bool IsHighLevel => string.Equals(Level, "HighLevel", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Conexion con labcore-api: donde esta, como se autentica y con que identidad escribe.</summary>
public sealed class LabcoreOptions
{
    public const string SectionName = "Labcore";

    /// <summary>Base de la API, incluida la version. Ej: http://localhost:5080/api/v1</summary>
    public string BaseUrl { get; set; } = "http://localhost:5080/api/v1";

    /// <summary>Header X-Api-Key. Vacio = API en modo anonimo (solo desarrollo).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Usuario del LIS con el que se cargan los resultados (AppUsers.usr_id).</summary>
    public int UserId { get; set; } = 1;

    /// <summary>Analizador (Analizadores.a_id): clave del mapeo de codigos y origen de los resultados.</summary>
    public int InstrumentId { get; set; }

    /// <summary>Cada cuanto se refresca el mapeo de codigos que vive en el LIS.</summary>
    public int MappingRefreshMinutes { get; set; } = 30;
}
