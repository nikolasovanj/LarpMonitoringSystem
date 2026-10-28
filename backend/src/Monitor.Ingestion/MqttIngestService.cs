using System.Buffers;
using Monitor.Core;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace Monitor.Ingestion;

public sealed class MqttIngestService(
    IOptions<MqttOptions> options, IngestChannels channels, IngestStats stats,
    ILogger<MqttIngestService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value;
        using var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += OnMessage;

        var connect = new MqttClientOptionsBuilder()
            .WithTcpServer(o.Host, o.Port)
            .WithClientId(o.ClientId)
            .WithCleanSession(o.CleanSession)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .Build();
        var subscribe = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(o.TelemetryTopic).WithAtLeastOnceQoS())
            .WithTopicFilter(f => f.WithTopic(o.StatusTopic).WithAtLeastOnceQoS())
            .Build();

        // MQTTnet 5 has no managed client: reconnect and re-subscribe ourselves.
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
                log.LogWarning(ex, "MQTT connection problem, retrying in {Delay}s", delay.TotalSeconds);
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }

        if (client.IsConnected)
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder()
                .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection).Build(),
                CancellationToken.None);
    }

    // Runs on MQTTnet's thread: parse, enqueue, return. No I/O here.
    private Task OnMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        var msg = e.ApplicationMessage;
        stats.Received();

        if (!TopicParser.TryParse(msg.Topic, out var topic)) { stats.Rejected(); return Task.CompletedTask; }

        var payload = msg.Payload.ToArray();
        var now = DateTimeOffset.UtcNow;

        if (topic.Kind == TopicKind.Status)
        {
            if (!StatusPayload.TryParse(payload, out var status)) { stats.Rejected(); return Task.CompletedTask; }
            if (!channels.Status.Writer.TryWrite(new StatusEvent(topic.Device, status, now)))
                stats.Dropped();
            return Task.CompletedTask;
        }

        // Telemetry is never retained by design; a retained one would replay on every reconnect.
        if (msg.Retain) { stats.Rejected(); return Task.CompletedTask; }

        if (!TelemetryPayload.TryParse(payload, now, out var reading, out var error))
        {
            stats.Rejected();
            log.LogDebug("Rejected {Topic}: {Error}", msg.Topic, error);
            return Task.CompletedTask;
        }

        if (!channels.Telemetry.Writer.TryWrite(
                new TelemetrySample(topic.Series, reading.Unit, reading.Time, reading.Value)))
            stats.Dropped();
        return Task.CompletedTask;
    }
}