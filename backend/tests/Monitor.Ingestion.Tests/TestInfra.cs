using System.Net;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;

namespace Monitor.Ingestion.Tests;

/// A throwaway Mosquitto on a FIXED host port, so a replacement broker can take over the
/// same address (a random mapped port can change when a container is recreated).
public sealed class TestBroker : IAsyncDisposable
{
    private readonly IContainer _container;
    private TestBroker(IContainer container, int port) { _container = container; Port = port; }

    public string Host => _container.Hostname;
    public int Port { get; }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; } finally { l.Stop(); }
    }

    public static async Task<TestBroker> StartAsync(int? port = null)
    {
        var p = port ?? FreePort();
        var c = new ContainerBuilder("eclipse-mosquitto:2")
            .WithPortBinding(p, 1883)
            .WithResourceMapping(
                Encoding.UTF8.GetBytes("listener 1883\nallow_anonymous true\n"),
                "/mosquitto/config/mosquitto.conf")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("running"))
            .Build();
        await c.StartAsync();
        return new TestBroker(c, p);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

public sealed class Publisher : IAsyncDisposable
{
    private readonly IMqttClient _client;
    private Publisher(IMqttClient client) => _client = client;

    public static async Task<Publisher> ConnectAsync(string host, int port)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId("pub-" + Guid.NewGuid().ToString("N")[..8])
            .Build());
        return new Publisher(client);
    }

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
        try { if (_client.IsConnected) await _client.DisconnectAsync(); } catch { /* broker may be gone */ }
        _client.Dispose();
    }
}

public static class WorkerHarness
{
    /// Builds (but does not start) a worker against the given broker and database.
    public static IHost Build(string mqttHost, int mqttPort, string pgConnectionString,
                              int batchSize = 1000, int flushIntervalMs = 100)
    {
        var b = Host.CreateApplicationBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mqtt:Host"] = mqttHost,
            ["Mqtt:Port"] = mqttPort.ToString(),
            ["Mqtt:ClientId"] = "ingest-test-" + Guid.NewGuid().ToString("N")[..8],
            ["Ingest:BatchSize"] = batchSize.ToString(),
            ["Ingest:FlushIntervalMs"] = flushIntervalMs.ToString(),
            ["ConnectionStrings:Timescale"] = pgConnectionString,
        });
        b.AddIngestion();
        return b.Build();
    }
}