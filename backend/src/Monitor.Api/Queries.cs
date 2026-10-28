using Monitor.Contracts;
using Monitor.Core;
using Npgsql;

namespace Monitor.Api;

public sealed class Queries(NpgsqlDataSource db)
{
    public async Task<List<DeviceDto>> GetDevicesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT s.id, s.site, s.line, s.device, s.sensor, s.unit,
                   l.time, l.value, d.status, d.status_since, d.last_seen
            FROM series s
            LEFT JOIN device_status d
                   ON d.site = s.site AND d.line = s.line AND d.device = s.device
            LEFT JOIN LATERAL (SELECT time, value FROM telemetry
                               WHERE series_id = s.id ORDER BY time DESC LIMIT 1) l ON true
            ORDER BY s.site, s.line, s.device, s.sensor
            """;

        var order = new List<(string, string, string)>();
        var sensors = new Dictionary<(string, string, string), List<SensorDto>>();
        var meta = new Dictionary<(string, string, string), (string Status, DateTimeOffset? Since, DateTimeOffset? Seen)>();

        await using var cmd = db.CreateCommand(sql);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var key = (r.GetString(1), r.GetString(2), r.GetString(3));
            if (!sensors.TryGetValue(key, out var list))
            {
                sensors[key] = list = [];
                order.Add(key);
                meta[key] = (r.IsDBNull(8) ? "unknown" : r.GetString(8),
                             r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
                             r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10));
            }
            list.Add(new SensorDto(r.GetInt32(0), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(7) ? null : r.GetDouble(7),
                r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6)));
        }

        return order.Select(k =>
        {
            var m = meta[k];
            var newest = sensors[k].Max(s => s.LatestTime);
            var seen = m.Seen is null ? newest : newest is null ? m.Seen : (m.Seen > newest ? m.Seen : newest);
            return new DeviceDto(k.Item1, k.Item2, k.Item3, m.Status, m.Since, seen, sensors[k]);
        }).ToList();
    }

    public async Task<SeriesDto?> GetSeriesAsync(int id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "SELECT id, site, line, device, sensor, unit FROM series WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new SeriesDto(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                            r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5))
            : null;
    }

    public async Task<List<SeriesDto>> ListSeriesAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "SELECT id, site, line, device, sensor, unit FROM series ORDER BY site, line, device, sensor");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<SeriesDto>();
        while (await r.ReadAsync(ct))
            list.Add(new SeriesDto(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                                   r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    public async Task<List<HistoryPoint>> GetHistoryAsync(int seriesId, DateTimeOffset from, DateTimeOffset to,
                                                          HistoryPlan plan, CancellationToken ct)
    {
        // Raw rows are aggregated per bucket; rollup rows are re-bucketed with a sample-weighted mean.
        var sql = plan.Source == HistorySource.Raw
            ? """
              SELECT time_bucket(make_interval(secs => @b), time) AS t,
                     avg(value), min(value), max(value), count(*)
              FROM telemetry
              WHERE series_id = @id AND time >= @from AND time < @to
              GROUP BY t ORDER BY t
              """
            : """
              SELECT time_bucket(make_interval(secs => @b), bucket) AS t,
                     sum(avg * samples) / sum(samples), min(min), max(max), sum(samples)::bigint
              FROM telemetry_1m
              WHERE series_id = @id AND bucket >= @from AND bucket < @to
              GROUP BY t ORDER BY t
              """;

        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("b", plan.BucketMs / 1000.0);
        cmd.Parameters.AddWithValue("id", seriesId);
        cmd.Parameters.AddWithValue("from", from.UtcDateTime);
        cmd.Parameters.AddWithValue("to", to.UtcDateTime);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        var points = new List<HistoryPoint>();
        while (await r.ReadAsync(ct))
            points.Add(new HistoryPoint(r.GetFieldValue<DateTimeOffset>(0),
                r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetInt64(4)));
        return points;
    }

    public async Task<List<AlertDto>> GetAlertsAsync(bool openOnly, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT a.id, a.rule_id, a.series_id, s.site, s.line, s.device, s.sensor,
                   a.severity, a.message, a.value, a.opened_at, a.resolved_at,
                   a.acknowledged_at, a.acknowledged_by
            FROM alerts a LEFT JOIN series s ON s.id = a.series_id
            WHERE NOT @open OR a.resolved_at IS NULL
            ORDER BY a.opened_at DESC LIMIT 200
            """);
        cmd.Parameters.AddWithValue("open", openOnly);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AlertDto>();
        while (await r.ReadAsync(ct))
            list.Add(new AlertDto(r.GetInt64(0),
                r.IsDBNull(1) ? null : r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetDouble(9),
                r.GetFieldValue<DateTimeOffset>(10),
                r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11),
                r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12),
                r.IsDBNull(13) ? null : r.GetString(13)));
        return list;
    }

    /// Idempotent: acknowledging twice keeps the first acknowledgement. False = no such alert.
    public async Task<bool> AcknowledgeAsync(long id, string? by, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE alerts SET acknowledged_at = COALESCE(acknowledged_at, now()),
                              acknowledged_by = COALESCE(acknowledged_by, @by)
            WHERE id = @id
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)by ?? DBNull.Value });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }
}