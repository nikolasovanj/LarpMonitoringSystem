namespace Monitor.Ingestion;

public sealed class StatsReporter(IngestStats stats, IngestChannels channels,
                                  ILogger<StatsReporter> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var prev = stats.Snapshot();
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var cur = stats.Snapshot();
                log.LogInformation(
                    "last 10s: received={Rx} rejected={Rej} dropped={Drop} written={Wr} | queue={Q}",
                    cur.Received - prev.Received, cur.Rejected - prev.Rejected,
                    cur.Dropped - prev.Dropped, cur.Written - prev.Written,
                    channels.Telemetry.Reader.Count);
                prev = cur;
            }
        }
        catch (OperationCanceledException) { }
    }
}