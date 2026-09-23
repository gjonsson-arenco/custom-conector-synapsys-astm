using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synapsys.Connector.Flows;
using Synapsys.Connector.Lis;

namespace Synapsys.Connector.Microbiology;

/// <summary>
/// Acumula los cultivos de cada tubo a medida que Synapsys los informa por partes (estado,
/// aislado 1, aislado 2...) y devuelve el informe completo de los que cambiaron, que es lo que
/// se manda al LIS. Cumple el papel que en el adapter de Epicenter cumplia la base de Epicenter:
/// el LIS solo guarda texto, asi que la fuente de verdad del cultivo esta aca.
/// </summary>
/// <remarks>
/// Un archivo JSON por tubo, con los codigos tal como los manda el equipo: si despues se corrige
/// un catalogo, el proximo envio ya sale con el nombre nuevo. Un aislado que se reenvia reemplaza
/// al anterior con el mismo numero, asi que reprocesar un mensaje no duplica nada.
/// </remarks>
public sealed class CultureStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _directory;
    private readonly TimeSpan _retention;
    private readonly TimeProvider _clock;
    private readonly ILogger<CultureStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    public CultureStore(IOptions<CultureStoreOptions> options, TimeProvider clock, ILogger<CultureStore> logger)
    {
        _directory = options.Value.Directory;
        _retention = TimeSpan.FromDays(Math.Max(1, options.Value.RetentionDays));
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Incorpora estados y aislados de un tubo. Devuelve el informe completo de cada cultivo que
    /// cambio. Lo que no se puede ubicar queda en <paramref name="warnings"/>.
    /// </summary>
    public async Task<IReadOnlyList<CultureReport>> ApplyAsync(
        string barcode,
        IReadOnlyList<CultureStatus> statuses,
        IReadOnlyList<IsolateUpdate> isolates,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var path = PathFor(barcode);
            var sample = await ReadAsync(path, cancellationToken) ?? new StoredSample { Barcode = barcode };
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var status in statuses)
            {
                Culture(sample, status.TestCode).StatusCode = status.StatusCode;
                changed.Add(status.TestCode);
            }

            foreach (var update in isolates)
            {
                var testCode = update.SourceTest ?? SingleCulture(sample);
                if (testCode is null)
                {
                    warnings.Add(
                        $"El aislado {update.Isolate.Number} del tubo {barcode} no dice de que cultivo es y el tubo " +
                        $"tiene {sample.Cultures.Count} cultivos registrados: no se puede ubicar.");
                    continue;
                }

                Culture(sample, testCode).Isolates[update.Isolate.Number] = update.Isolate;
                changed.Add(testCode);
            }

            if (changed.Count == 0)
            {
                return [];
            }

            sample.UpdatedAt = _clock.GetUtcNow();
            await WriteAsync(path, sample, cancellationToken);
            Purge();

            return sample.Cultures
                .Where(culture => changed.Contains(culture.TestCode))
                .Select(culture => new CultureReport(
                    barcode,
                    culture.TestCode,
                    culture.StatusCode,
                    culture.Isolates.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList()))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lo que se tiene registrado de un tubo, o <c>null</c>. Para diagnostico y tests.</summary>
    public async Task<IReadOnlyList<CultureReport>?> GetAsync(string barcode, CancellationToken cancellationToken)
    {
        var sample = await ReadAsync(PathFor(barcode), cancellationToken);

        return sample?.Cultures
            .Select(culture => new CultureReport(
                barcode,
                culture.TestCode,
                culture.StatusCode,
                culture.Isolates.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList()))
            .ToList();
    }

    private static StoredCulture Culture(StoredSample sample, string testCode)
    {
        var culture = sample.Cultures.FirstOrDefault(candidate =>
            string.Equals(candidate.TestCode, testCode, StringComparison.OrdinalIgnoreCase));

        if (culture is null)
        {
            culture = new StoredCulture { TestCode = testCode };
            sample.Cultures.Add(culture);
        }

        return culture;
    }

    /// <summary>Si el O del aislado no trae el cultivo de origen, solo se puede ubicar si hay uno solo.</summary>
    private static string? SingleCulture(StoredSample sample) =>
        sample.Cultures.Count == 1 ? sample.Cultures[0].TestCode : null;

    private string PathFor(string barcode)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(barcode.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return Path.Combine(_directory, name.ToUpperInvariant() + ".json");
    }

    private static async Task<StoredSample?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StoredSample>(stream, Json, cancellationToken);
    }

    /// <summary>Escribe a un temporal y lo mueve encima: un corte a mitad no deja el archivo roto.</summary>
    private async Task WriteAsync(string path, StoredSample sample, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var temp = path + ".tmp";

        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, sample, Json, cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Borra, como mucho una vez por hora, los tubos que no se tocan hace mas que la retencion.</summary>
    private void Purge()
    {
        var now = _clock.GetUtcNow();
        if (now - _lastPurge < TimeSpan.FromHours(1))
        {
            return;
        }

        _lastPurge = now;

        try
        {
            var removed = 0;
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*.json"))
            {
                if (now - file.LastWriteTimeUtc > _retention)
                {
                    file.Delete();
                    removed++;
                }
            }

            if (removed > 0)
            {
                _logger.LogInformation("Se borraron {Count} cultivos sin novedades en mas de {Days} dias.", removed, _retention.TotalDays);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "No se pudieron purgar los cultivos viejos de {Directory}.", _directory);
        }
    }

    private sealed class StoredSample
    {
        public string Barcode { get; set; } = string.Empty;

        public DateTimeOffset UpdatedAt { get; set; }

        public List<StoredCulture> Cultures { get; set; } = [];
    }

    private sealed class StoredCulture
    {
        public string TestCode { get; set; } = string.Empty;

        public string? StatusCode { get; set; }

        public Dictionary<int, IsolateReport> Isolates { get; set; } = [];
    }
}

/// <summary>Donde y por cuanto se guardan los cultivos en curso. Seccion "Cultures" del appsettings.</summary>
public sealed class CultureStoreOptions
{
    public const string SectionName = "Cultures";

    /// <summary>Carpeta de los cultivos. Relativa a la carpeta del conector.</summary>
    public string Directory { get; set; } = "data/cultures";

    /// <summary>
    /// Dias sin novedades despues de los cuales se olvida un tubo. Tiene que cubrir el cultivo mas
    /// largo (hongos, micobacterias): si llega un aislado de un tubo olvidado, el informe sale sin
    /// el estado que se habia recibido antes.
    /// </summary>
    public int RetentionDays { get; set; } = 120;
}
