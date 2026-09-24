using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Synapsys.Connector.Configuration;

/// <summary>
/// Settings editables desde el front, un archivo por tema bajo <c>Settings:Directory</c>
/// (por defecto <c>settings/</c> junto al appsettings.json). appsettings.json queda para lo
/// de despliegue: URLs, acceso a labcore-api y logging.
/// </summary>
public static class SettingsServiceCollectionExtensions
{
    public const string DirectoryKey = "Settings:Directory";

    public static IServiceCollection AddConnectorSettings(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var directory = configuration[DirectoryKey];
        directory = string.IsNullOrWhiteSpace(directory) ? "settings" : directory;
        if (!Path.IsPathRooted(directory))
        {
            directory = Path.Combine(environment.ContentRootPath, directory);
        }

        // Los valores iniciales salen de las secciones que antes vivian en appsettings.json,
        // asi una instalacion existente conserva su configuracion al crear los archivos.
        services.AddSettingsFile(directory, "instrument.json", () => new InstrumentSettings
        {
            InstrumentId = configuration.GetValue<int>("Labcore:InstrumentId")
        });

        services.AddSettingsFile(directory, "communication.json", () => new CommunicationSettings
        {
            Transport = configuration.GetSection(TransportOptions.SectionName).Get<TransportOptions>() ?? new(),
            Astm = configuration.GetSection(AstmOptions.SectionName).Get<AstmOptions>() ?? new()
        });

        services.AddSettingsFile(directory, "petitions.json", () => new PetitionSettings());

        services.AddSingleton(provider => new CodeCatalogs(
            Catalog(provider, directory, "result-mappings.json", LoadDefaultResultMappings),
            Catalog(provider, directory, "organisms.json", () => new CodeCatalogSettings()),
            Catalog(provider, directory, "antibiotics.json", () => new CodeCatalogSettings())));

        return services;
    }

    private static CodeCatalog Catalog(IServiceProvider provider, string directory, string fileName, Func<CodeCatalogSettings> defaults) =>
        new(NewFile(provider, directory, fileName, defaults));

    private static void AddSettingsFile<T>(this IServiceCollection services, string directory, string fileName, Func<T> defaults)
        where T : class =>
        services.AddSingleton(provider => NewFile(provider, directory, fileName, defaults));

    private static SettingsFile<T> NewFile<T>(IServiceProvider provider, string directory, string fileName, Func<T> defaults)
        where T : class =>
        new(Path.Combine(directory, fileName),
            defaults,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger($"Settings.{fileName}"));

    /// <summary>Mapeo de resultados de partida (el mismo que usa el adapter de Epicenter).</summary>
    private static CodeCatalogSettings LoadDefaultResultMappings()
    {
        using var stream = typeof(SettingsServiceCollectionExtensions).Assembly
            .GetManifestResourceStream("Synapsys.Connector.Configuration.Defaults.result-mappings.json")!;

        return JsonSerializer.Deserialize<CodeCatalogSettings>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? new CodeCatalogSettings();
    }
}
