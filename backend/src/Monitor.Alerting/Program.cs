using Monitor.Alerting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<AlertingOptions>(builder.Configuration.GetSection("Alerting"));
builder.Services.Configure<MqttSettings>(builder.Configuration.GetSection("Mqtt"));
builder.Services.AddNpgsqlDataSource(
    builder.Configuration.GetConnectionString("Timescale")
    ?? throw new InvalidOperationException("ConnectionStrings:Timescale is not configured"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AlertStore>();
builder.Services.AddSingleton<AlertPublisher>();
builder.Services.AddHostedService<AlertEvaluator>();

await builder.Build().RunAsync();