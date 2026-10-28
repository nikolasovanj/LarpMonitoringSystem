using System.Text.Json;
using Monitor.Contracts;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace Monitor.Alerting;

public sealed class AlertPublisher(IOptions<MqttSettings> options, ILogger<AlertPublisher> log) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private DateTime _retryAfter = DateTime.MinValue;

    public async Task PublishAsync(AlertEvent ev, CancellationToken ct)
    {
        try
        {
            if (!_client.IsConnected)
            {
                if (DateTime.UtcNow < _retryAfter) return;
                var o = options.Value;
                await _client.ConnectAsync(new MqttClientOptionsBuilder()
                    .WithTcpServer(o.Host, o.Port)
                    .WithClientId("iot-alerting-" + Guid.NewGuid().ToString("N")[..8])
                    .WithCleanSession(true)
                    .Build(), ct);
            }
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic($"alerts/{ev.Type}")
                .WithPayload(JsonSerializer.SerializeToUtf8Bytes(ev, Web))
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)       // a retained alert would replay to every new subscriber
                .Build();
            await _client.PublishAsync(msg, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _retryAfter = DateTime.UtcNow.AddSeconds(5);
            log.LogWarning(ex, "Could not publish alert event; the alert is still stored in the database");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_client.IsConnected) await _client.DisconnectAsync(); } catch { }
        _client.Dispose();
    }
}