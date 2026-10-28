namespace Monitor.Contracts;

public sealed record SensorDto(int SeriesId, string Sensor, string? Unit,
                               double? LatestValue, DateTimeOffset? LatestTime);

public sealed record DeviceDto(string Site, string Line, string Device,
                               string Status,                  // online | offline | unknown
                               DateTimeOffset? StatusSince, DateTimeOffset? LastSeen,
                               IReadOnlyList<SensorDto> Sensors);

public sealed record SeriesDto(int Id, string Site, string Line, string Device, string Sensor, string? Unit);

public sealed record HistoryPoint(DateTimeOffset Time, double Avg, double Min, double Max, long Samples);

public sealed record HistoryDto(int SeriesId, string Source, long BucketMs,
                                IReadOnlyList<HistoryPoint> Points);

public sealed record AlertDto(long Id, int? RuleId, int? SeriesId,
                              string? Site, string? Line, string? Device, string? Sensor,
                              string Severity, string Message, double? Value,
                              DateTimeOffset OpenedAt, DateTimeOffset? ResolvedAt,
                              DateTimeOffset? AcknowledgedAt, string? AcknowledgedBy);

public sealed record AcknowledgeRequest(string? By);

/// fault: spike | stuck | dropout | ramp | noisy.  Duration is in samples (optional).
public sealed record InjectFaultRequest(string Sensor, string Fault, int? Duration);

// ---- live (SignalR) payloads ----
public sealed record LiveReading(string Site, string Line, string Device, string Sensor,
                                 double Value, string? Unit, DateTimeOffset Time);

public sealed record DeviceStatusChanged(string Site, string Line, string Device,
                                         string Status, DateTimeOffset Time);

public sealed record CommandResultDto(string Site, string Line, string Device,
                                      bool Ok, string? Error, DateTimeOffset Time);

/// type: "opened" | "resolved"
public sealed record AlertEvent(string Type, long AlertId, string Severity, string Message,
    string? Site, string? Line, string? Device, string? Sensor, double? Value, DateTimeOffset Time);                                    