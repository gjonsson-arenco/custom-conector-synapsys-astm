using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Synapsys.Connector.Configuration;

/// <summary>
/// Un archivo JSON de settings editable desde el front. Se lee una vez al arrancar (si no existe
/// se crea con los valores por defecto) y cada guardado reemplaza el archivo de forma atomica y
/// avisa por <see cref="Changed"/>.
/// </summary>
/// <remarks>
/// <see cref="Current"/> es una foto: no se modifica en el lugar, se guarda una nueva con
/// <see cref="SaveAsync"/>. Asi quien la tomo al empezar una sesion no ve cambios a medias.
/// </remarks>
public sealed class SettingsFile<T> where T : class
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Descripciones con acentos y simbolos (<, >) legibles en el archivo.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private T _current;

    public SettingsFile(string path, Func<T> defaults, ILogger logger)
    {
        FilePath = path;

        if (File.Exists(path))
        {
            _current = Read(path);
            return;
        }

        _current = defaults();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Write(path, _current);
        logger.LogInformation("No existia {Path}: se creo con los valores por defecto.", path);
    }

    public string FilePath { get; }

    public T Current => Volatile.Read(ref _current);

    /// <summary>Se dispara despues de guardar, con el valor nuevo.</summary>
    public event Action<T>? Changed;

    public async Task SaveAsync(T value, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Write(FilePath, value);
            Volatile.Write(ref _current, value);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(value);
    }

    private static T Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
                ?? throw new InvalidOperationException("el archivo esta vacio");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"No se pudo leer {path}: {ex.Message}", ex);
        }
    }

    /// <summary>Escribe a un temporal y lo mueve encima: un corte a mitad no deja el archivo roto.</summary>
    private static void Write(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }
}
