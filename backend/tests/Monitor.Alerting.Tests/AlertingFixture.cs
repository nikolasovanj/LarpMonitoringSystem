using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Monitor.Alerting;
using Monitor.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MQTTnet;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;
using Xunit;

namespace Monitor.Alerting.Tests;

public sealed record AlertRow(long Id, int? RuleId, string Severity, string Message, double? Value, bool Resolved);

public sealed class AlertingFixture : IAsyncLifetime
{
    private IContainer _mosquitto = null!;
    private PostgreSqlContainer _pg = null!;

    public NpgsqlDataSource Db { get; private set; } = null!;
    public string MqttHost => _mosquitto.Hostname;
    public int MqttPort => _mosquitto.GetMappedPublicPort(1883);

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
        Db = NpgsqlDataSource.Create(_pg.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await Db.DisposeAsync();
        await _pg.DisposeAsync();
        await _mosquitto.DisposeAsync();
    }

    // ---------------------------------------------------------------- generic

    public string NewDevice() => "d" + Guid.NewGuid().ToString("N")[..8];

    /// Whole-second "now": fake-clock times then round-trip through SQL exactly.
    public static DateTimeOffset StartTime()
    {
        var t = DateTimeOffset.UtcNow;
        return new DateTimeOffset(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; } finally { l.Stop(); }
    }

    public async Task EventuallyAsync(Func<bool> condition, string what, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"timed out after {seconds}s waiting for: {what}");
    }

    // ---------------------------------------------------------------- database

    public async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = Db.CreateCommand(sql);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    /// The init script seeds five rules that match everything. Tests start from a clean slate
    /// and create device-scoped rules, so they cannot see each other's series.
    public Task ResetAsync() => ExecAsync("TRUNCATE alerts, alert_rules RESTART IDENTITY");

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

    public async Task<int> InsertRuleAsync(string name, string kind, string? op, double? threshold,
                                           int forSeconds, string severity, string device, string sensor)
    {
        await using var cmd = Db.CreateCommand("""
            INSERT INTO alert_rules (name, sensor, device, kind, operator, threshold, for_seconds, severity)
            VALUES (@n, @s, @d, @k, @o, @t, @f, @sev) RETURNING id
            """);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("s", sensor);
        cmd.Parameters.AddWithValue("d", device);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Text) { Value = (object?)op ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Double) { Value = (object?)threshold ?? DBNull.Value });
        cmd.Parameters.AddWithValue("f", forSeconds);
        cmd.Parameters.AddWithValue("sev", severity);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    public Task InsertSampleAsync(int seriesId, DateTimeOffset time, double value) => ExecAsync("""
        INSERT INTO telemetry (time, series_id, value) VALUES (@t, @s, @v)
        ON CONFLICT (series_id, time) DO NOTHING
        """, ("t", time.UtcDateTime), ("s", seriesId), ("v", value));

    /// `count` samples, `stepSec` apart, alternating v+alt / v-alt (alt = 0 gives a constant series).
    public Task SeedAsync(int seriesId, DateTimeOffset start, int count, double stepSec, double v, double alt = 0) =>
        ExecAsync("""
            INSERT INTO telemetry (time, series_id, value)
            SELECT @t0 + make_interval(secs => i * @step), @s,
                   @v + CASE WHEN i % 2 = 0 THEN @a ELSE -@a END
            FROM generate_series(0, @n - 1) AS i
            """, ("t0", start.UtcDateTime), ("s", seriesId), ("step", stepSec), ("v", v), ("a", alt), ("n", count));

    public async Task<List<AlertRow>> AlertsAsync(string device)
    {
        await using var cmd = Db.CreateCommand("""
            SELECT a.id, a.rule_id, a.severity, a.message, a.value, a.resolved_at IS NOT NULL
            FROM alerts a JOIN series s ON s.id = a.series_id
            WHERE s.device = @d ORDER BY a.id
            """);
        cmd.Parameters.AddWithValue("d", device);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<AlertRow>();
        while (await r.ReadAsync())
            list.Add(new AlertRow(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.GetString(2),
                                  r.GetString(3), r.IsDBNull(4) ? null : r.GetDouble(4), r.GetBoolean(5)));
        return list;
    }

    public async Task<long> InsertOpenAlertAsync(int? ruleId, int seriesId, string message)
    {
        await using var cmd = Db.CreateCommand("""
            INSERT INTO alerts (rule_id, series_id, severity, message) VALUES (@r, @s, 'warning', @m) RETURNING id
            """);
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Integer) { Value = (object?)ruleId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("s", seriesId);
        cmd.Parameters.AddWithValue("m", message);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}

[CollectionDefinition("alerting")]
public class AlertingCollection : ICollectionFixture<AlertingFixture> { }

/// One evaluator wired to the real store and a real publisher, driven by a fake clock.
public sealed class Rig : IAsyncDisposable
{
    private readonly AlertEvaluator _evaluator;
    private readonly AlertPublisher _publisher;
    public FakeTimeProvider Clock { get; }

    public Rig(AlertingFixture f, FakeTimeProvider clock, int? mqttPort = null)
    {
        Clock = clock;
        _publisher = new AlertPublisher(
            Options.Create(new MqttSettings { Host = f.MqttHost, Port = mqttPort ?? f.MqttPort }),
            NullLogger<AlertPublisher>.Instance);
        _evaluator = new AlertEvaluator(new AlertStore(f.Db), _publisher,
            Options.Create(new AlertingOptions()),          // production defaults: 2 s window, 5 s resolve hold
            clock, NullLogger<AlertEvaluator>.Instance);
    }

    public Task TickAsync() => _evaluator.TickAsync(CancellationToken.None);
    public ValueTask DisposeAsync() => _publisher.DisposeAsync();
}

public static class Drive
{
    /// Each simulated second: write one sample (or none if value is null), tick every rig at the same
    /// moment, then advance the clock. All rigs must share the clock of the first one.
    public static async Task RunAsync(AlertingFixture f, int seriesId, int seconds, double? value, params Rig[] rigs)
    {
        var clock = rigs[0].Clock;
        for (var i = 0; i < seconds; i++)
        {
            if (value is { } v) await f.InsertSampleAsync(seriesId, clock.GetUtcNow(), v);
            await Task.WhenAll(rigs.Select(r => r.TickAsync()));
            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }
}

/// Records everything published under alerts/#.
public sealed class EventTap : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly IMqttClient _client;
    public ConcurrentQueue<(string Topic, string Payload, bool Retain)> Received { get; } = new();

    private EventTap(IMqttClient client)
    {
        _client = client;
        _client.ApplicationMessageReceivedAsync += e =>
        {
            var m = e.ApplicationMessage;
            Received.Enqueue((m.Topic, Encoding.UTF8.GetString(m.Payload.ToArray()), m.Retain));
            return Task.CompletedTask;
        };
    }

    public static async Task<EventTap> ConnectAsync(string host, int port)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(host, port).WithClientId("tap-" + Guid.NewGuid().ToString("N")[..8]).Build());
        await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic("alerts/#").WithAtLeastOnceQoS()).Build());
        return new EventTap(client);
    }

    /// type = "opened" or "resolved"
    public List<AlertEvent> For(string device, string type) =>
        Received.ToArray()
            .Where(m => m.Topic == $"alerts/{type}")
            .Select(m => JsonSerializer.Deserialize<AlertEvent>(m.Payload, Web)!)
            .Where(e => e.Device == device)
            .ToList();

    public async ValueTask DisposeAsync()
    {
        try { if (_client.IsConnected) await _client.DisconnectAsync(); } catch { }
        _client.Dispose();
    }
}