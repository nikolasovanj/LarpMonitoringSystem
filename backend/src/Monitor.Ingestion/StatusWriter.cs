using Npgsql;

namespace Monitor.Ingestion;

public sealed class StatusWriter(
    NpgsqlDataSource db, IngestChannels channels, ILogger<StatusWriter> log) : BackgroundService
{
    // One statement: upsert the current state, and append a history row only if the
    // status actually changed. Re-delivered retained messages on reconnect add no noise.
    private const string Sql = """
        WITH prev AS (
            SELECT status FROM device_status WHERE site = @s AND line = @l AND device = @d
        ), up AS (
            INSERT INTO device_status (site, line, device, status, status_since, last_seen)
            VALUES (@s, @l, @d, @st, @t, @t)
            ON CONFLICT (site, line, device) DO UPDATE SET
                status       = EXCLUDED.status,
                status_since = CASE WHEN device_status.status = EXCLUDED.status
                                    THEN device_status.status_since ELSE EXCLUDED.status_since END,
                last_seen    = GREATEST(device_status.last_seen, EXCLUDED.last_seen)
            RETURNING 1
        )
        INSERT INTO device_status_events (time, site, line, device, status)
        SELECT @t, @s, @l, @d, @st
        WHERE (SELECT status FROM prev) IS DISTINCT FROM @st
        """;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            // Unprocessed events at shutdown are fine: status is retained on the broker,
            // so the next start re-derives the current state.
            await foreach (var ev in channels.Status.Reader.ReadAllAsync(ct))
                await Retry.RunAsync(() => ApplyAsync(ev, ct), log,
                                     $"status {ev.Device.Device}={ev.Status}", ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task ApplyAsync(StatusEvent ev, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(Sql);
        cmd.Parameters.AddWithValue("s", ev.Device.Site);
        cmd.Parameters.AddWithValue("l", ev.Device.Line);
        cmd.Parameters.AddWithValue("d", ev.Device.Device);
        cmd.Parameters.AddWithValue("st", ev.Status);
        cmd.Parameters.AddWithValue("t", ev.Time.UtcDateTime);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}