using Monitor.Operator.Api;

namespace Monitor.Operator.Tests;

public sealed class FakeApi : IMonitorApi
{
    public List<DeviceDto> Devices { get; set; } = new();
    public List<AlertDto> Alerts { get; set; } = new();
    public Dictionary<long, HistoryDto> History { get; } = new();

    public int DeviceCalls { get; private set; }
    public int AlertCalls { get; private set; }
    public List<(string Site, string Line, string Device, InjectFaultRequest Body)> Injected { get; } = new();
    public List<(long Id, string By)> Acked { get; } = new();

    /// <summary>When set, every call throws it (a stopped backend, a 400 from the API...).</summary>
    public ApiException? Fail { get; set; }

    /// <summary>When set, only InjectFaultAsync throws it.</summary>
    public ApiException? FailInject { get; set; }

    public Task<List<DeviceDto>> GetDevicesAsync(CancellationToken ct = default)
    {
        DeviceCalls++;
        if (Fail is not null) throw Fail;
        return Task.FromResult(Devices.ToList());
    }

    public Task<List<AlertDto>> GetAlertsAsync(bool openOnly, CancellationToken ct = default)
    {
        AlertCalls++;
        if (Fail is not null) throw Fail;
        return Task.FromResult(Alerts.ToList());
    }

    public Task<HistoryDto> GetHistoryAsync(long seriesId, DateTimeOffset from, DateTimeOffset to, int maxPoints,
        CancellationToken ct = default) =>
        Task.FromResult(History.TryGetValue(seriesId, out var h)
            ? h
            : new HistoryDto(seriesId, "Raw", 1000, new List<HistoryPoint>()));

    public Task AckAlertAsync(long id, string by, CancellationToken ct = default)
    {
        Acked.Add((id, by));
        return Task.CompletedTask;
    }

    public Task InjectFaultAsync(string site, string line, string device, InjectFaultRequest body,
        CancellationToken ct = default)
    {
        if (FailInject is not null) throw FailInject;
        Injected.Add((site, line, device, body));
        return Task.CompletedTask;
    }

    public void Dispose() { }
}

public sealed class FakeLive : ILiveClient
{
    public event Action<IReadOnlyList<LiveReading>>? Telemetry;
    public event Action<DeviceStatusChanged>? DeviceStatus;
    public event Action<CommandResultDto>? CommandResult;
    public event Action<AlertEvent>? Alert;
    public event Action<LiveState>? StateChanged;
    public event Action? Connected;

    public bool Started { get; private set; }

    public Task StartAsync(CancellationToken ct)
    {
        Started = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void RaiseTelemetry(params LiveReading[] batch) => Telemetry?.Invoke(batch);
    public void RaiseStatus(DeviceStatusChanged s) => DeviceStatus?.Invoke(s);
    public void RaiseCommandResult(CommandResultDto r) => CommandResult?.Invoke(r);
    public void RaiseAlert(AlertEvent a) => Alert?.Invoke(a);
    public void RaiseState(LiveState s) => StateChanged?.Invoke(s);
    public void RaiseConnected() => Connected?.Invoke();
}

/// <summary>Builders with sensible defaults, so each test only states what it cares about.</summary>
public static class Make
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public static SensorDto Sensor(string name = "temperature", long id = 1, double? value = null,
        DateTimeOffset? time = null, string? unit = "C") =>
        new(id, name, unit, value, time ?? (value is null ? null : T0));

    public static DeviceDto Device(string name = "pump01", string site = "site1", string line = "line1",
        string status = "online", params SensorDto[] sensors) =>
        new(site, line, name, status, T0, T0, sensors.Length > 0 ? sensors.ToList() : new List<SensorDto> { Sensor() });

    public static AlertDto Alert(long id = 1, string severity = "warning", DateTimeOffset? acked = null,
        string? ackedBy = null) =>
        new(id, 1, 1, "site1", "line1", "pump01", "temperature", severity, "Temperature high", 91.5,
            T0, null, acked, ackedBy);

    public static LiveReading Reading(string sensor = "temperature", double value = 1, int secondsAfter = 1,
        string device = "pump01", string site = "site1", string line = "line1", string? unit = "C") =>
        new(site, line, device, sensor, value, unit, T0.AddSeconds(secondsAfter));

    public static HistoryPoint Point(int secondsAfter, double avg) => new(T0.AddSeconds(secondsAfter), avg, avg, avg, 1);
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = Make.T0;
    public override DateTimeOffset GetUtcNow() => Now;
}
