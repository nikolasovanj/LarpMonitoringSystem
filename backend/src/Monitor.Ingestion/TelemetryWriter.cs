using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Monitor.Ingestion;

public sealed class TelemetryWriter(
    NpgsqlDataSource db, SeriesRegistry series, IngestChannels channels, IngestStats stats,
    IOptions<IngestOptions> options, ILogger<TelemetryWriter> log) : BackgroundService
{
    private const string InsertSql = """
    INSERT INTO telemetry (time, series_id, value)
    SELECT t, s, v FROM unnest(@t, @s, @v) AS u(t, s, v)
    ON CONFLICT (series_id, time) DO NOTHING
    """;
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value;
        var reader = channels.Telemetry.Reader;
        var batch = new List<TelemetrySample>(o.BatchSize);

        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                await FillAsync(reader, batch, o, ct);
                await FlushAsync(batch, ct);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) { }

        // Shutdown: the MQTT service has already stopped, so flush what is still queued.
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            do
            {
                Drain(reader, batch, o.BatchSize);
                if (batch.Count > 0) { await FlushAsync(batch, grace.Token); batch.Clear(); }
            } while (reader.Count > 0);
        }
        catch (OperationCanceledException)
        {
            log.LogWarning("Shutdown flush timed out, ~{Count} samples lost", batch.Count + reader.Count);
        }
    }

    // Take whatever is ready, then keep collecting until the batch is full or the window closes.
    private static async Task FillAsync(ChannelReader<TelemetrySample> reader, List<TelemetrySample> batch,
                                        IngestOptions o, CancellationToken ct)
    {
        Drain(reader, batch, o.BatchSize);
        if (batch.Count >= o.BatchSize) return;

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(o.FlushIntervalMs);
        try
        {
            while (batch.Count < o.BatchSize && await reader.WaitToReadAsync(window.Token))
                Drain(reader, batch, o.BatchSize);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* window elapsed */ }
    }

    private static void Drain(ChannelReader<TelemetrySample> reader, List<TelemetrySample> batch, int max)
    {
        while (batch.Count < max && reader.TryRead(out var s)) batch.Add(s);
    }

    private async Task FlushAsync(List<TelemetrySample> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        var inserted = 0;
        var ok = await Retry.RunAsync(async () => inserted = await WriteAsync(batch, ct), log,
                                    $"telemetry batch ({batch.Count} samples)", ct, series.Clear);
        if (!ok) { stats.Dropped(batch.Count); return; }
        stats.Written(inserted);
        if (inserted < batch.Count)
            log.LogDebug("{Dup} duplicate samples ignored", batch.Count - inserted);
    }

    private async Task<int> WriteAsync(List<TelemetrySample> batch, CancellationToken ct)
    {
        var n = batch.Count;
        var times = new DateTime[n];
        var ids = new int[n];
        var values = new double[n];
        for (var i = 0; i < n; i++)
        {
            ids[i] = await series.GetIdAsync(batch[i].Key, batch[i].Unit, ct);
            times[i] = batch[i].Time.UtcDateTime;
            values[i] = batch[i].Value;
        }

        await using var cmd = db.CreateCommand(InsertSql);
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = times });
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = ids });
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = values });
        return await cmd.ExecuteNonQueryAsync(ct);   // rows inserted, duplicates excluded
}
}