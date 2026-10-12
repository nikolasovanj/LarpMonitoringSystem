using Monitor.Ingestion;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection("Ingest"));
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));

builder.Services.AddNpgsqlDataSource(
    builder.Configuration.GetConnectionString("Timescale")
    ?? throw new InvalidOperationException("ConnectionStrings:Timescale is not configured"));

builder.Services.AddSingleton<IngestChannels>();
builder.Services.AddSingleton<IngestStats>();
builder.Services.AddSingleton<SeriesRegistry>();

// Start order = registration order, stop order = reverse. The MQTT service is registered
// LAST so it starts last (writers are ready first) and stops first (writers then drain).
builder.Services.AddHostedService<StatsReporter>();
builder.Services.AddHostedService<StatusWriter>();
builder.Services.AddHostedService<TelemetryWriter>();
builder.Services.AddHostedService<MqttIngestService>();

await builder.Build().RunAsync();