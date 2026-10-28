using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Monitor.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using MQTTnet;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;
using Xunit;

namespace Monitor.Api.Tests;

public sealed class ApiFactory(string mqttHost, int mqttPort, string pg) : WebApplicationFactory<Program>
{
    // UseSetting (not ConfigureAppConfiguration): Program.cs reads the connection string eagerly.
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Mqtt:Host", mqttHost);
        builder.UseSetting("Mqtt:Port", mqttPort.ToString());
        builder.UseSetting("ConnectionStrings:Timescale", pg);
    }
}

public sealed class ApiFixture : IAsyncLifetime
{
    private IContainer _mosquitto = null!;
    private PostgreSqlContainer _pg = null!;

    public ApiFactory Factory { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    public NpgsqlDataSource Db { get; private set; } = null!;
    public string MqttHost => _mosquitto.Hostname;
    public int MqttPort => _mosquitto.GetMappedPublicPort(1883);
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

        _pg = new PostgreSqlBuilder("timescale/timescaledb:latest-pg16")   // pin a version once it works
            .WithDatabase("iot").WithUsername("iot").WithPassword("iot_test")
            .WithResourceMapping(
                File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "init.sql")),
                "/docker-entrypoint-initdb.d/01-init.sql")
            .Build();

        await Task.WhenAll(_mosquitto.StartAsync(), _pg.StartAsync());
        Db = NpgsqlDataSource.Create(PgConnectionString);

        Factory = new ApiFactory(MqttHost, MqttPort, PgConnectionString);
        Http = Factory.CreateClient();                         // starts the host

        // "Connected" is reported by /healthz; the gateway's SUBSCRIBE follows within milliseconds.
        // Tests that depend on subscriptions publish until they see the effect.
        await EventuallyAsync(async () => (await Http.GetAsync("/healthz")).IsSuccessStatusCode,
                              "API to connect to the broker and database", 40);
    }

    public async Task DisposeAsync()
    {
        Http.Dispose();
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
        await _pg.DisposeAsync();
        await _mosquitto.DisposeAsync();
    }

    // ---------------------------------------------------------------- generic helpers

    public string NewDevice() => "d" + Guid.NewGuid().ToString("N")[..8];

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; } finally { l.Stop(); }
    }

    /// UTC time `ago` before now, cut to a whole second (or minute), so SQL round trips compare exactly.
    public static DateTime Ago(TimeSpan ago, bool wholeMinute = false)
    {
        var t = DateTime.UtcNow - ago;
        var unit = wholeMinute ? TimeSpan.TicksPerMinute : TimeSpan.TicksPerSecond;
        return new DateTime(t.Ticks - t.Ticks % unit, DateTimeKind.Utc);
    }

    public static string Url(DateTime t) => Uri.EscapeDataString(t.ToString("O"));

    public async Task EventuallyAsync(Func<Task<bool>> condition, string what, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            try { if (await condition()) return; } catch { /* not ready yet */ }
            await Task.Delay(200);
        }
        throw new TimeoutException($"timed out after {seconds}s waiting for: {what}");
    }

    public Task EventuallyAsync(Func<bool> condition, string what, int seconds = 15) =>
        EventuallyAsync(() => Task.FromResult(condition()), what, seconds);

    // ---------------------------------------------------------------- database helpers

    public async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = Db.CreateCommand(sql);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = Db.CreateCommand(sql);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<int> CreateSeriesAsync(string device, string sensor, string? unit = null)
    {
        await using var cmd = Db.CreateCommand("""
            INSERT INTO series (site, line, device, sensor, unit)
            VALUES ('it', 'line1', @d, @s, @u) RETURNING id
            """);
        cmd.Parameters.AddWithValue("d", device);
        cmd.Parameters.AddWithValue("s", sensor);
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Text) { Value = (object?)unit ?? DBNull.Value });
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<long> InsertAlertAsync(int seriesId, string message, DateTime opened, DateTime? resolved = null)
    {
        await using var cmd = Db.CreateCommand("""
            INSERT INTO alerts (series_id, severity, message, value, opened_at, resolved_at)
            VALUES (@s, 'warning', @m, 1.5, @o, @r) RETURNING id
            """);
        cmd.Parameters.AddWithValue("s", seriesId);
        cmd.Parameters.AddWithValue("m", message);
        cmd.Parameters.AddWithValue("o", opened);
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.TimestampTz) { Value = (object?)resolved ?? DBNull.Value });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // ---------------------------------------------------------------- http helpers

    public Task<HttpResponseMessage> PostFaultAsync(string device, string jsonBody, HttpClient? client = null) =>
        (client ?? Http).PostAsync($"/api/devices/it/line1/{device}/faults",
            new StringContent(jsonBody, Encoding.UTF8, "application/json"));
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFixture> { }

/// A plain MQTT client for tests: publishes, and records everything it is subscribed to.
public sealed class MqttTestClient : IAsyncDisposable
{
    private readonly IMqttClient _client;
    public ConcurrentQueue<(string Topic, string Payload, bool Retain)> Received { get; } = new();

    private MqttTestClient(IMqttClient client)
    {
        _client = client;
        _client.ApplicationMessageReceivedAsync += e =>
        {
            var m = e.ApplicationMessage;
            Received.Enqueue((m.Topic, Encoding.UTF8.GetString(m.Payload.ToArray()), m.Retain));
            return Task.CompletedTask;
        };
    }

    public static async Task<MqttTestClient> ConnectAsync(string host, int port)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId("test-" + Guid.NewGuid().ToString("N")[..8])
            .Build());
        return new MqttTestClient(client);
    }

    public async Task SubscribeAsync(string filter) =>
        await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(filter).WithAtLeastOnceQoS()).Build());

    public async Task PublishAsync(string topic, string payload, bool retain = false)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();
        var result = await _client.PublishAsync(msg);
        if (!result.IsSuccess) throw new InvalidOperationException($"publish failed: {result.ReasonString}");
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_client.IsConnected) await _client.DisconnectAsync(); } catch { }
        _client.Dispose();
    }
}

/// A real SignalR client connected to the in-process test server.
public sealed class HubProbe : IAsyncDisposable
{
    private readonly HubConnection _hub;
    public ConcurrentQueue<LiveReading[]> Batches { get; } = new();
    public ConcurrentQueue<DeviceStatusChanged> Statuses { get; } = new();
    public ConcurrentQueue<CommandResultDto> Results { get; } = new();

    private HubProbe(HubConnection hub) => _hub = hub;

    public IEnumerable<LiveReading> ReadingsFor(string device) =>
        Batches.ToArray().SelectMany(b => b).Where(r => r.Device == device);
    public IEnumerable<DeviceStatusChanged> StatusesFor(string device) =>
        Statuses.ToArray().Where(s => s.Device == device);
    public IEnumerable<CommandResultDto> ResultsFor(string device) =>
        Results.ToArray().Where(r => r.Device == device);

    public static async Task<HubProbe> ConnectAsync(ApiFactory factory)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/live"), o =>
            {
                // The in-memory test server speaks HTTP through a handler; long polling works over it.
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        var probe = new HubProbe(hub);
        hub.On<LiveReading[]>("Telemetry", b => probe.Batches.Enqueue(b));
        hub.On<DeviceStatusChanged>("DeviceStatus", s => probe.Statuses.Enqueue(s));
        hub.On<CommandResultDto>("CommandResult", r => probe.Results.Enqueue(r));
        await hub.StartAsync();
        return probe;
    }

    public async ValueTask DisposeAsync() => await _hub.DisposeAsync();
}