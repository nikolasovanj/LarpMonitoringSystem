using System.Text.Json;

namespace Monitor.Core;

public static class TelemetryPayload
{
    public static readonly TimeSpan MaxFuture = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 8 };

    /// {"device":"..","sensor":"..","value":70.1,"unit":"C","ts":1759750000}
    public static bool TryParse(ReadOnlyMemory<byte> payload, DateTimeOffset now,
                                out TelemetryReading reading, out string error)
    {
        reading = default;
        try
        {
            using var doc = JsonDocument.Parse(payload, Options);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "not a JSON object"; return false; }

            if (!root.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number ||
                !v.TryGetDouble(out var value) || !double.IsFinite(value))
            { error = "missing or invalid \"value\""; return false; }

            string? unit = root.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString() : null;
            if (unit is { Length: > 16 }) unit = unit[..16];

            var time = now;
            if (root.TryGetProperty("ts", out var t))
            {
                if (t.ValueKind != JsonValueKind.Number || !t.TryGetInt64(out var ts) || ts <= 0)
                { error = "invalid \"ts\""; return false; }
                try
                {
                    time = ts >= 100_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ts)
                        : DateTimeOffset.FromUnixTimeSeconds(ts);
                }
                catch (ArgumentOutOfRangeException) { error = "\"ts\" out of range"; return false; }
            }

            if (time > now + MaxFuture || time < now - MaxAge)
            { error = "timestamp outside accepted window"; return false; }

            reading = new TelemetryReading(value, unit, time);
            error = "";
            return true;
        }
        catch (JsonException) { error = "invalid JSON"; return false; }
    }
}

public static class StatusPayload
{
    /// Plain-text "online" / "offline", exactly as the simulator publishes them.
    public static bool TryParse(ReadOnlySpan<byte> payload, out string status)
    {
        if (payload.SequenceEqual("online"u8))  { status = "online";  return true; }
        if (payload.SequenceEqual("offline"u8)) { status = "offline"; return true; }
        status = "";
        return false;
    }
}