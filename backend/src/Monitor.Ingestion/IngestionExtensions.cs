using Microsoft.Extensions.DependencyInjection;

namespace Monitor.Ingestion;

public static class IngestionExtensions
{
    public static IHostApplicationBuilder AddIngestion(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
        builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection("Ingest"));
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));

        builder.Services.AddNpgsqlDataSource(
            builder.Configuration.GetConnectionString("Timescale")
            ?? throw new InvalidOperationException("ConnectionStrings:Timescale is not configured"));

        builder.Services.AddSingleton<IngestChannels>();
        builder.Services.AddSingleton<IngestStats>();
        builder.Services.AddSingleton<SeriesRegistry>();

        // Registration order matters: MQTT last so it starts last and stops first.
        builder.Services.AddHostedService<StatsReporter>();
        builder.Services.AddHostedService<StatusWriter>();
        builder.Services.AddHostedService<TelemetryWriter>();
        builder.Services.AddHostedService<MqttIngestService>();
        return builder;
    }
}