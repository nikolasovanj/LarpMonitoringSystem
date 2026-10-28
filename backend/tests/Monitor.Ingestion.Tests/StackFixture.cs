using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Monitor.Ingestion.Tests;

public sealed class StackFixture : IAsyncLifetime
{
    private IContainer _mosquitto = null!;
    private PostgreSqlContainer _pg = null!;
    private IHost _host = null!;
    private IMqttClient _pub = null!;
    

    public NpgsqlDataSource Db { get; private set; } = null!;
    public string PgConnectionString => _pg.GetConnectionString();
    public async Task InitializeAsync()
    {
        _mosquitto = new ContainerBuilder("eclipse-mosquitto:2")
            .WithPortBinding(1883, true)
            .WithResourceMapping(
                Encoding.UTF8.GetBytes("listener 1883\nallow_anonymous true\n"),
                "/mosquitto/config/mosquitto.conf")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("running"))
            .Build();

        // init.sql runs through the image's entrypoint (psql), because creating the continuous
        // aggregate cannot run inside the implicit transaction Npgsql wraps around multi-statements.
        _pg = new PostgreSqlBuilder("timescale/timescaledb:latest-pg16")
            .WithDatabase("iot").WithUsername("iot").WithPassword("iot_test")
            .WithResourceMapping(
                File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "init.sql")),
                "/docker-entrypoint-initdb.d/01-init.sql")
            .Build();

        await Task.WhenAll(_mosquitto.StartAsync(), _pg.StartAsync());
        Db = NpgsqlDataSource.Create(_pg.GetConnectionString());

        var port = _mosquitto.GetMappedPublicPort(1883);

        _pub = new MqttClientFactory().CreateMqttClient();
        await _pub.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(_mosquitto.Hostname, port).WithClientId("test-publisher").Build());

        var b = Host.CreateApplicationBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mqtt:Host"] = _mosquitto.Hostname,
            ["Mqtt:Port"] = port.ToString(),
            ["Mqtt:ClientId"] = "ingest-test-" + Guid.NewGuid().ToString("N")[..8],
            ["Ingest:FlushIntervalMs"] = "100",
            ["ConnectionStrings:Timescale"] = _pg.GetConnectionString(),
        });
        b.AddIngestion();
        _host = b.Build();
        await _host.StartAsync();

        // Nothing published before the worker subscribes is delivered (no session exists yet),
        // so keep publishing a warm-up sample until one lands.
        var warm = NewDevice();
        await EventuallyAsync(async () =>
        {
            await PublishAsync(Topic(warm, "warmup"), Sample(warm, "warmup", 1, NowMs()));
            return await CountAsync(warm) > 0;
        }, "worker to subscribe and write", 30);
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();          // exercises the drain-on-shutdown path too
        _host.Dispose();
        if (_pub.IsConnected) await _pub.DisconnectAsync();
        _pub.Dispose();
        await Db.DisposeAsync();
        await _pg.DisposeAsync();
        await _mosquitto.DisposeAsync();
    }

    // ---------------------------------------------------------------- helpers

    public string NewDevice() => "d" + Guid.NewGuid().ToString("N")[..8];
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public string Topic(string device, string sensor) => $"factory/it/line1/{device}/{sensor}/telemetry";
    public string StatusTopic(string device) => $"factory/it/line1/{device}/status";

    public static string Sample(string device, string sensor, double value, long tsMs, string unit = "C") =>
        JsonSerializer.Serialize(new { device, sensor, value, unit, ts = tsMs });

    public async Task PublishAsync(string topic, string payload, bool retain = false)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();
        var result = await _pub.PublishAsync(msg);
        if (!result.IsSuccess) throw new InvalidOperationException($"publish failed: {result.ReasonString}");
    }

    public async Task EventuallyAsync(Func<Task<bool>> condition, string what, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }
        throw new TimeoutException($"timed out after {seconds}s waiting for: {what}");
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = Db.CreateCommand(sql);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task ExecAsync(string sql)
    {
        await using var cmd = Db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task<long> CountAsync(string device) => ScalarAsync<long>(
        "SELECT count(*) FROM telemetry t JOIN series s ON s.id = t.series_id WHERE s.device = @d",
        ("d", device));
}

[CollectionDefinition("stack")]
public class StackCollection : ICollectionFixture<StackFixture> { }