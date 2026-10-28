namespace Monitor.Operator.Api;

/// <summary>The live hub connection as the view models see it. Lets tests raise events by hand.</summary>
public interface ILiveClient : IAsyncDisposable
{
    event Action<IReadOnlyList<LiveReading>>? Telemetry;
    event Action<DeviceStatusChanged>? DeviceStatus;
    event Action<CommandResultDto>? CommandResult;
    event Action<AlertEvent>? Alert;
    event Action<LiveState>? StateChanged;

    /// <summary>Raised after the first connect and after every reconnect.</summary>
    event Action? Connected;

    Task StartAsync(CancellationToken ct);
}
