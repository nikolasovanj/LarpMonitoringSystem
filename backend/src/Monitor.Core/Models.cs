namespace Monitor.Core;

public readonly record struct DeviceKey(string Site, string Line, string Device);

public readonly record struct SeriesKey(string Site, string Line, string Device, string Sensor)
{
    public DeviceKey DeviceKey => new(Site, Line, Device);
}

public readonly record struct TelemetryReading(double Value, string? Unit, DateTimeOffset Time);

public enum TopicKind { Telemetry, Status }

public readonly record struct ParsedTopic(TopicKind Kind, DeviceKey Device, string? Sensor)
{
    public SeriesKey Series => new(Device.Site, Device.Line, Device.Device, Sensor!);
}