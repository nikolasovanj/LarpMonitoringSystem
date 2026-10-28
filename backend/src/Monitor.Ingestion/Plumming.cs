using System.Collections.Concurrent;
using System.Threading.Channels;
using Monitor.Core;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Monitor.Ingestion;

public sealed record TelemetrySample(SeriesKey Key, string? Unit, DateTimeOffset Time, double Value);
public sealed record StatusEvent(DeviceKey Device, string Status, DateTimeOffset Time);

public sealed class IngestChannels
{
    // Default FullMode (Wait) on purpose: TryWrite then returns false when full, so drops
    // can be counted. With DropWrite, TryWrite returns true and the drop is invisible.
    public Channel<TelemetrySample> Telemetry { get; }
    public Channel<StatusEvent> Status { get; } =
        Channel.CreateBounded<StatusEvent>(new BoundedChannelOptions(10_000) { SingleReader = true });

    public IngestChannels(IOptions<IngestOptions> options) =>
        Telemetry = Channel.CreateBounded<TelemetrySample>(
            new BoundedChannelOptions(options.Value.QueueCapacity) { SingleReader = true });
}

public sealed class IngestStats
{
    private long _received, _rejected, _dropped, _written;
    public void Received() => Interlocked.Increment(ref _received);
    public void Rejected() => Interlocked.Increment(ref _rejected);
    public void Dropped(int n = 1) => Interlocked.Add(ref _dropped, n);
    public void Written(int n) => Interlocked.Add(ref _written, n);
    public (long Received, long Rejected, long Dropped, long Written) Snapshot() =>
        (Interlocked.Read(ref _received), Interlocked.Read(ref _rejected),
         Interlocked.Read(ref _dropped), Interlocked.Read(ref _written));
}

/// Maps (site, line, device, sensor) to series.id; inserts on first sight.
public sealed class SeriesRegistry(NpgsqlDataSource db)
{
    private readonly ConcurrentDictionary<SeriesKey, int> _ids = new();

    // ON CONFLICT DO UPDATE (instead of DO NOTHING) so RETURNING yields the id in both cases.
    private const string Sql = """
        INSERT INTO series (site, line, device, sensor, unit)
        VALUES (@site, @line, @device, @sensor, @unit)
        ON CONFLICT (site, line, device, sensor)
        DO UPDATE SET unit = COALESCE(series.unit, EXCLUDED.unit)
        RETURNING id
        """;

    public void Clear() => _ids.Clear();

    public async ValueTask<int> GetIdAsync(SeriesKey key, string? unit, CancellationToken ct)
    {
        if (_ids.TryGetValue(key, out var cached)) return cached;

        await using var cmd = db.CreateCommand(Sql);
        cmd.Parameters.AddWithValue("site", key.Site);
        cmd.Parameters.AddWithValue("line", key.Line);
        cmd.Parameters.AddWithValue("device", key.Device);
        cmd.Parameters.AddWithValue("sensor", key.Sensor);
        cmd.Parameters.Add(new NpgsqlParameter("unit", NpgsqlTypes.NpgsqlDbType.Text)
            { Value = (object?)unit ?? DBNull.Value });

        var id = (int)(await cmd.ExecuteScalarAsync(ct))!;
        _ids[key] = id;
        return id;
    }
}

internal static class Retry
{
    /// Runs `action` until it succeeds (true). Returns false if the database reports a
    /// non-transient error (bad data): retrying would block the queue forever.
    public static async Task<bool> RunAsync(Func<Task> action, ILogger log, string what,
                                            CancellationToken ct, Action? onStaleReference = null)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try { await action(); return true; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                onStaleReference?.Invoke();   // cached series id no longer exists (DB was reset)
                log.LogWarning("{What}: stale series id, cache cleared, retrying", what);
            }
            catch (PostgresException ex) when (!ex.IsTransient)
            {
                log.LogError(ex, "{What} failed permanently, dropping it", what);
                return false;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "{What} failed, retrying in {Delay}s", what, delay.TotalSeconds);
            }
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
    }
}