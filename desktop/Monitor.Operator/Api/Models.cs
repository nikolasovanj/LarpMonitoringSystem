namespace Monitor.Operator.Api;

// Wire models. They mirror the API's JSON (System.Text.Json, camelCase) and the web client's
// api/types.ts. Status and severity stay strings on purpose: an unknown value from a newer
// API shows up as text instead of failing deserialisation.
//
// To share the backend's types instead, reference Monitor.Contracts and delete this file.

public sealed record SensorDto(
    long SeriesId, string Sensor, string? Unit, double? LatestValue, DateTimeOffset? LatestTime);

public sealed record DeviceDto(
    string Site, string Line, string Device, string Status,
    DateTimeOffset? StatusSince, DateTimeOffset? LastSeen, List<SensorDto> Sensors);

public sealed record HistoryPoint(DateTimeOffset Time, double Avg, double Min, double Max, long Samples);

public sealed record HistoryDto(long SeriesId, string Source, long BucketMs, List<HistoryPoint> Points);

public sealed record AlertDto(
    long Id, long? RuleId, long? SeriesId, string? Site, string? Line, string? Device, string? Sensor,
    string Severity, string Message, double? Value, DateTimeOffset OpenedAt, DateTimeOffset? ResolvedAt,
    DateTimeOffset? AcknowledgedAt, string? AcknowledgedBy);

public sealed record InjectFaultRequest(string Sensor, string Fault, int? Duration);

// ---- live (SignalR) payloads ----

public sealed record LiveReading(
    string Site, string Line, string Device, string Sensor, double Value, string? Unit, DateTimeOffset Time);

public sealed record DeviceStatusChanged(string Site, string Line, string Device, string Status, DateTimeOffset Time);

public sealed record CommandResultDto(
    string Site, string Line, string Device, bool Ok, string? Error, DateTimeOffset Time);

public sealed record AlertEvent(
    string Type, long AlertId, string Severity, string Message, string? Site, string? Line,
    string? Device, string? Sensor, double? Value, DateTimeOffset Time);

public static class Keys
{
    public static string Device(string site, string line, string device) => $"{site}/{line}/{device}";
}
