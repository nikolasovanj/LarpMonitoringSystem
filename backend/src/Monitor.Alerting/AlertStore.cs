using Monitor.Core;
using Npgsql;
using NpgsqlTypes;

namespace Monitor.Alerting;

public sealed record SnapshotWindow(DateTimeOffset Lookback, DateTimeOffset WindowFrom,
    DateTimeOffset BaselineFrom, DateTimeOffset BaselineTo, bool IncludeBaseline);

public sealed record ResolvedAlert(long Id, string Severity, string Message, double? Value);

public sealed class AlertStore(NpgsqlDataSource db)
{
    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static double? D(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    private static DateTimeOffset? T(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    public async Task<List<AlertRule>> LoadRulesAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, name, kind, operator, threshold, for_seconds, severity, site, line, device, sensor
            FROM alert_rules WHERE enabled ORDER BY id
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rules = new List<AlertRule>();
        while (await r.ReadAsync(ct))
        {
            RuleKind? kind = r.GetString(2) switch
            {
                "threshold" => RuleKind.Threshold, "zscore" => RuleKind.ZScore, "stale" => RuleKind.Stale, _ => null,
            };
            if (kind is null) continue;
            rules.Add(new AlertRule(r.GetInt32(0), r.GetString(1), kind.Value, S(r, 3), D(r, 4), r.GetInt32(5),
                                    r.GetString(6), S(r, 7), S(r, 8), S(r, 9), r.GetString(10)));
        }
        return rules;
    }

    public async Task<List<SeriesSnapshot>> LoadSnapshotsAsync(SnapshotWindow w, CancellationToken ct)
    {
        // Three lateral aggregates per series: the newest sample time, the recent window,
        // and (only when a z-score rule exists) the baseline just before the window.
        await using var cmd = db.CreateCommand("""
            SELECT s.id, s.site, s.line, s.device, s.sensor, s.unit,
                   l.time, w.w_min, w.w_max, w.w_n, b.b_mean, b.b_std, b.b_n
            FROM series s
            LEFT JOIN LATERAL (SELECT time FROM telemetry
                               WHERE series_id = s.id AND time > @lookback
                               ORDER BY time DESC LIMIT 1) l ON true
            LEFT JOIN LATERAL (SELECT min(value) AS w_min, max(value) AS w_max, count(*) AS w_n
                               FROM telemetry
                               WHERE series_id = s.id AND time >= @wfrom) w ON true
            LEFT JOIN LATERAL (SELECT avg(value) AS b_mean, stddev_samp(value) AS b_std, count(*) AS b_n
                               FROM telemetry
                               WHERE series_id = s.id AND time >= @bfrom AND time < @bto AND @baseline) b ON true
            ORDER BY s.id
            """);
        cmd.Parameters.AddWithValue("lookback", w.Lookback.UtcDateTime);
        cmd.Parameters.AddWithValue("wfrom", w.WindowFrom.UtcDateTime);
        cmd.Parameters.AddWithValue("bfrom", w.BaselineFrom.UtcDateTime);
        cmd.Parameters.AddWithValue("bto", w.BaselineTo.UtcDateTime);
        cmd.Parameters.AddWithValue("baseline", w.IncludeBaseline);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<SeriesSnapshot>();
        while (await r.ReadAsync(ct))
            list.Add(new SeriesSnapshot(
                r.GetInt32(0), new SeriesKey(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)), S(r, 5),
                T(r, 6), D(r, 7), D(r, 8), r.GetInt64(9), D(r, 10), D(r, 11), r.GetInt64(12)));
        return list;
    }

    public async Task<List<(int Rule, int Series)>> OpenAlertsAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "SELECT rule_id, series_id FROM alerts WHERE resolved_at IS NULL AND rule_id IS NOT NULL AND series_id IS NOT NULL");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<(int, int)>();
        while (await r.ReadAsync(ct)) list.Add((r.GetInt32(0), r.GetInt32(1)));
        return list;
    }

    /// Alerts whose rule was deleted (FK is ON DELETE SET NULL) can never resolve by themselves.
    public async Task ResolveOrphansAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "UPDATE alerts SET resolved_at = now() WHERE resolved_at IS NULL AND rule_id IS NULL");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// Returns the new alert id, or null if one is already open for this rule and series.
    public async Task<long?> OpenAsync(int ruleId, int seriesId, string severity, string message,
                                       double? value, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO alerts (rule_id, series_id, severity, message, value)
            VALUES (@r, @s, @sev, @msg, @v)
            ON CONFLICT (rule_id, series_id) WHERE resolved_at IS NULL DO NOTHING
            RETURNING id
            """);
        cmd.Parameters.AddWithValue("r", ruleId);
        cmd.Parameters.AddWithValue("s", seriesId);
        cmd.Parameters.AddWithValue("sev", severity);
        cmd.Parameters.AddWithValue("msg", message);
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Double) { Value = (object?)value ?? DBNull.Value });
        return await cmd.ExecuteScalarAsync(ct) as long?;
    }

    public async Task<ResolvedAlert?> ResolveAsync(int ruleId, int seriesId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE alerts SET resolved_at = now()
            WHERE rule_id = @r AND series_id = @s AND resolved_at IS NULL
            RETURNING id, severity, message, value
            """);
        cmd.Parameters.AddWithValue("r", ruleId);
        cmd.Parameters.AddWithValue("s", seriesId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct)
            ? new ResolvedAlert(rd.GetInt64(0), rd.GetString(1), rd.GetString(2), D(rd, 3))
            : null;
    }
}