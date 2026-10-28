namespace Monitor.Core;

/// factory/{site}/{line}/{device}/status             (5 segments)
/// factory/{site}/{line}/{device}/{sensor}/telemetry (6 segments)
/// Everything else (cmd, cmd/result, junk) is rejected.
public static class TopicParser
{
    public static bool TryParse(string topic, out ParsedTopic parsed)
    {
        parsed = default;
        var p = topic.Split('/');
        if (p.Length < 5 || p.Length > 6 || p[0] != "factory") return false;
        if (!IsValidId(p[1]) || !IsValidId(p[2]) || !IsValidId(p[3])) return false;
        var device = new DeviceKey(p[1], p[2], p[3]);

        if (p.Length == 5 && p[4] == "status")
        {
            parsed = new ParsedTopic(TopicKind.Status, device, null);
            return true;
        }
        if (p.Length == 6 && p[5] == "telemetry" && IsValidId(p[4]))
        {
            parsed = new ParsedTopic(TopicKind.Telemetry, device, p[4]);
            return true;
        }
        return false;
    }

    // Same alphabet the simulator enforces; keeps garbage out of the series table.
    private static bool IsValidId(string s)
    {
        if (s.Length is 0 or > 64) return false;
        foreach (var c in s)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
                return false;
        return true;
    }
}