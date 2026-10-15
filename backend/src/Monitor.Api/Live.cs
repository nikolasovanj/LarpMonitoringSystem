using System.Threading.Channels;
using Monitor.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Monitor.Api;

public sealed class MqttSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
}

public sealed class LiveHub : Hub<IMonitorClient> { }

/// Telemetry goes through a bounded queue and is flushed to clients every 250 ms as one batch.
public sealed class LiveBroadcaster(IHubContext<LiveHub, IMonitorClient> hub,
                                    ILogger<LiveBroadcaster> log) : BackgroundService
{
    private readonly Channel<LiveReading> _queue = Channel.CreateBounded<LiveReading>(
        new BoundedChannelOptions(20_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Enqueue(LiveReading r) => _queue.Writer.TryWrite(r);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var batch = new List<LiveReading>();
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                while (batch.Count < 5000 && _queue.Reader.TryRead(out var r)) batch.Add(r);
                if (batch.Count == 0) continue;
                try { await hub.Clients.All.Telemetry(batch.ToArray()); }
                catch (Exception ex) { log.LogWarning(ex, "Broadcast failed"); }
                batch.Clear();
            }
        }
        catch (OperationCanceledException) { }
    }
}