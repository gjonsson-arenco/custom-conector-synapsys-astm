using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Synapsys.Connector.Astm;
using Synapsys.Connector.Configuration;
using Synapsys.Connector.Flows;
using Synapsys.Connector.Lis;
using Synapsys.Connector.Microbiology;
using Synapsys.Connector.Monitoring;
using Synapsys.Connector.Petitions;
using Synapsys.Connector.Runtime;
using Synapsys.Connector.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddConnectorSettings(builder.Configuration, builder.Environment);
builder.Services.Configure<LabcoreOptions>(builder.Configuration.GetSection(LabcoreOptions.SectionName));

builder.Services.AddHttpClient("labcore", (serviceProvider, http) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<LabcoreOptions>>().Value;
    http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");

    if (!string.IsNullOrWhiteSpace(options.ApiKey))
    {
        http.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
    }
});

builder.Services.AddSingleton<LabcoreGateway>();
builder.Services.AddSingleton<ILisGateway>(provider => provider.GetRequiredService<LabcoreGateway>());
builder.Services.AddSingleton<LabcoreTestMappings>();
builder.Services.AddSingleton<AstmChannelFactory>();
builder.Services.AddSingleton<QueryFlow>();
builder.Services.AddSingleton<ResultsFlow>();

builder.Services.Configure<CultureStoreOptions>(builder.Configuration.GetSection(CultureStoreOptions.SectionName));
builder.Services.PostConfigure<CultureStoreOptions>(options =>
    options.Directory = Path.Combine(builder.Environment.ContentRootPath, options.Directory));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CultureStore>();
builder.Services.AddSingleton<TransmissionRouter>();
builder.Services.AddSingleton<ILisPetitions, LabcorePetitions>();
builder.Services.AddSingleton<PetitionOutbox>();

builder.Services.AddSingleton<IConnectorMonitor, ConnectorMonitor>();
builder.Services.AddSingleton<TransportFactory>();
builder.Services.AddSingleton<ConnectorController>();
builder.Services.AddHostedService<ConnectorBootstrap>();

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

app.UseWebSockets();

// Front React compilado (si existe wwwroot).
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapConnectorApi();
app.MapSettingsApi();
app.MapPetitionsApi();
app.MapMonitorSockets();

// Cualquier ruta no-API cae en la SPA.
app.MapFallbackToFile("index.html");

app.Run();
