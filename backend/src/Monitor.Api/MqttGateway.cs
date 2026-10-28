using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Monitor.Contracts;
using Monitor.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace Monitor.Api;

public sealed class MqttGateway(IOptions<MqttSettings> options, LiveBroadcaster live,
    IHubContext<LiveHub, IMonitorClient> hub, ILogger<MqttGateway> log) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new()
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private IMqttClient? _client;
    public bool IsConnected => _client?.IsConnected == true;

    public async Task PublishCommandAsync(string site, string line, string device,
                                          string sensor, string fault, int? duration, CancellationToken ct)
    {
        var client = _client;
        if (client is not { IsConnected: true }) throw new InvalidOperationException("MQTT broker not connected");

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic($"factory/{site}/{line}/{device}/cmd")
            .WithPayload(JsonSerializer.SerializeToUtf8Bytes(new { sensor, fault, duration }, Json))
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)       // a retained command would replay on every device reconnect
            .Build();
        var result = await client.PublishAsync(msg, ct);
        if (!result.IsSuccess) throw new InvalidOperationException($"publish failed: {result.ReasonString}");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value;
        using var client = new MqttClientFactory().CreateMqttClient();
        _client = client;
        client.ApplicationMessageReceivedAsync += OnMessage;

        var connect = new MqttClientOptionsBuilder()
            .WithTcpServer(o.Host, o.Port)
            .WithClientId("iot-api-" + Guid.NewGuid().ToString("N")[..8])   // unique: several API instances may run
            .WithCleanSession(true)                                          // live view only, nothing to queue while down
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .Build();
        var subscribe = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic("factory/+/+/+/+/telemetry").WithAtMostOnceQoS())
            .WithTopicFilter(f => f.WithTopic("factory/+/+/+/status").WithAtLeastOnceQoS())
            .WithTopicFilter(f => f.WithTopic("factory/+/+/+/cmd/result").WithAtLeastOnceQoS())
            .WithTopicFilter(f => f.WithTopic("alerts/+").WithAtLeastOnceQoS())
            .Build();

        var delay = TimeSpan.FromSeconds(1);
        var subscribed = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    subscribed = false;
                    await client.ConnectAsync(connect, ct);
                    log.LogInformation("Connected to MQTT broker {Host}:{Port}", o.Host, o.Port);
                }
                if (!subscribed)
                {
                    await client.SubscribeAsync(subscribe, ct);
                    subscribed = true;
                    delay = TimeSpan.FromSeconds(1);
                }
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "MQTT problem, retrying in {Delay}s", delay.TotalSeconds);
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }

        _client = null;
        if (client.IsConnected)
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder()
                .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection).Build(), CancellationToken.None);
    }

    private async Task OnMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        var msg = e.ApplicationMessage;
        var now = DateTimeOffset.UtcNow;
        var payload = msg.Payload.ToArray();

        try
        {
            if (TopicParser.TryParse(msg.Topic, out var t))
            {
                if (t.Kind == TopicKind.Telemetry)
                {
                    if (msg.Retain || !TelemetryPayload.TryParse(payload, now, out var r, out _)) return;
                    live.Enqueue(new LiveReading(t.Device.Site, t.Device.Line, t.Device.Device,
                                                 t.Sensor!, r.Value, r.Unit, r.Time));
                }
                else if (StatusPayload.TryParse(payload, out var status))
                {
                    await hub.Clients.All.DeviceStatus(new DeviceStatusChanged(
                        t.Device.Site, t.Device.Line, t.Device.Device, status, now));
                }
                return;
            }

            // factory/{site}/{line}/{device}/cmd/result  (TopicParser deliberately rejects it)
            if (msg.Topic is "alerts/opened" or "alerts/resolved" && payload.Length is > 0 and <= 4096)
            {
                var ev = JsonSerializer.Deserialize<AlertEvent>(payload, Web);
                if (ev is null) return;
                var message = ev.Message.Length > 300 ? ev.Message[..300] : ev.Message;
                await hub.Clients.All.Alert(ev with { Type = msg.Topic["alerts/".Length..], Message = message });
                return;
            }
            var seg = msg.Topic.Split('/');
            if (seg is ["factory", var site, var line, var device, "cmd", "result"] && payload.Length is > 0 and <= 2048)
            {
                using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                var error = root.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String
                    ? er.GetString() : null;
                if (error is { Length: > 300 }) error = error[..300];
                await hub.Clients.All.CommandResult(new CommandResultDto(site, line, device, ok, error, now));
            }
        }
        catch (JsonException) { /* malformed reply from a device: ignore */ }
        catch (Exception ex) { log.LogWarning(ex, "Failed handling {Topic}", msg.Topic); }
    }
}