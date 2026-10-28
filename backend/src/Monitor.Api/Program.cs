using Monitor.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNpgsqlDataSource(
    builder.Configuration.GetConnectionString("Timescale")
    ?? throw new InvalidOperationException("ConnectionStrings:Timescale is not configured"));
builder.Services.Configure<MqttSettings>(builder.Configuration.GetSection("Mqtt"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Queries>();

builder.Services.AddSignalR();
builder.Services.AddSingleton<LiveBroadcaster>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveBroadcaster>());
builder.Services.AddSingleton<MqttGateway>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttGateway>());

// SignalR over WebSockets needs credentials allowed, which forbids "any origin".
var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();
app.MapApi();
app.MapHub<LiveHub>("/hubs/live");
app.Run();

public partial class Program { }   // lets WebApplicationFactory<Program> find the entry point later