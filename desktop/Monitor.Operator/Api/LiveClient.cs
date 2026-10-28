using Microsoft.AspNetCore.SignalR.Client;

namespace Monitor.Operator.Api;

public enum LiveState { Connecting, Live, Reconnecting, Offline }

/// <summary>
/// SignalR connection to /hubs/live. Events fire on whatever thread SignalR uses; the view models
/// marshal to the UI thread.
/// </summary>
public sealed class LiveClient : ILiveClient
{
    private readonly HubConnection _conn;

    public event Action<IReadOnlyList<LiveReading>>? Telemetry;
    public event Action<DeviceStatusChanged>? DeviceStatus;
    public event Action<CommandResultDto>? CommandResult;
    public event Action<AlertEvent>? Alert;
    public event Action<LiveState>? StateChanged;

    /// <summary>Raised after a reconnect or the first connect, so callers can refetch a snapshot.</summary>
    public event Action? Connected;

    public LiveClient(string baseUrl)
    {
        _conn = new HubConnectionBuilder()
            .WithUrl($"{baseUrl.TrimEnd('/')}/hubs/live")
            .WithAutomaticReconnect(new RetryForever())
            .Build();

        _conn.On<List<LiveReading>>("Telemetry", batch => Telemetry?.Invoke(batch));
        _conn.On<DeviceStatusChanged>("DeviceStatus", s => DeviceStatus?.Invoke(s));
        _conn.On<CommandResultDto>("CommandResult", r => CommandResult?.Invoke(r));
        _conn.On<AlertEvent>("Alert", a => Alert?.Invoke(a));

        _conn.Reconnecting += _ => { StateChanged?.Invoke(LiveState.Reconnecting); return Task.CompletedTask; };
        _conn.Reconnected += _ =>
        {
            StateChanged?.Invoke(LiveState.Live);
            Connected?.Invoke();
            return Task.CompletedTask;
        };
        _conn.Closed += _ => { StateChanged?.Invoke(LiveState.Offline); return Task.CompletedTask; };
    }

    /// <summary>Keeps trying until it connects or the token is cancelled.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        StateChanged?.Invoke(LiveState.Connecting);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _conn.StartAsync(ct);
                StateChanged?.Invoke(LiveState.Live);
                Connected?.Invoke();
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                StateChanged?.Invoke(LiveState.Offline);
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    public ValueTask DisposeAsync() => _conn.DisposeAsync();

    // 0 s, 1 s, 2 s, 5 s, then every 10 s, with no upper limit: an operator console should always recover.
    private sealed class RetryForever : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext ctx) => ctx.PreviousRetryCount switch
        {
            0 => TimeSpan.Zero,
            1 => TimeSpan.FromSeconds(1),
            2 => TimeSpan.FromSeconds(2),
            3 => TimeSpan.FromSeconds(5),
            _ => TimeSpan.FromSeconds(10),
        };
    }
}
