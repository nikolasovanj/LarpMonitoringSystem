using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monitor.Operator.Api;

namespace Monitor.Operator.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(15);
    private const int HistoryPoints = 300;
    private const int LogLimit = 200;

    private readonly MonitorApi _api;
    private readonly LiveClient _live;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, DeviceViewModel> _byKey = new();
    private CancellationTokenSource? _historyCts;
    private bool _connectedBefore;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private string? _awaitingReplyFrom;

    public MainViewModel(MonitorApi api, LiveClient live, string baseUrl)
    {
        _api = api;
        _live = live;
        BaseUrl = baseUrl;
        Alerts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NoAlerts));
    }

    public bool NoAlerts => Alerts.Count == 0;

    public string BaseUrl { get; }
    public IReadOnlyList<string> FaultNames { get; } = new[] { "spike", "stuck", "dropout", "ramp", "noisy" };

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    public ObservableCollection<AlertViewModel> Alerts { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InjectFaultCommand))]
    private DeviceViewModel? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive))]
    private string _connection = "Connecting";

    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private int _openAlertCount;

    public bool IsLive => Connection == "Live";

    // ---- fault injection form ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InjectFaultCommand))]
    private SensorViewModel? _faultSensor;

    // The ComboBox writes null back when its item list is replaced (a different device was selected);
    // put the first sensor back so the form is never left without a choice.
    partial void OnFaultSensorChanged(SensorViewModel? value)
    {
        if (value is null && SelectedDevice is { Sensors.Count: > 0 } d)
            Dispatcher.UIThread.Post(() => FaultSensor ??= d.Sensors.FirstOrDefault());
    }

    [ObservableProperty] private string _faultName = "spike";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationInvalid))]
    [NotifyCanExecuteChangedFor(nameof(InjectFaultCommand))]
    private string _faultDuration = "";

    [ObservableProperty] private string? _faultMessage;
    [ObservableProperty] private bool _faultIsError;

    public bool DurationInvalid => !TryParseDuration(out _);

    private bool TryParseDuration(out int? duration)
    {
        duration = null;
        var text = FaultDuration.Trim();
        if (text.Length == 0) return true; // empty means the device's default
        if (int.TryParse(text, out var n) && n is >= 1 and <= 1_000_000)
        {
            duration = n;
            return true;
        }
        return false;
    }

    // ---- lifecycle ----

    public async Task StartAsync()
    {
        _live.Telemetry += batch => Dispatcher.UIThread.Post(() => OnTelemetry(batch));
        _live.DeviceStatus += s => Dispatcher.UIThread.Post(() => OnStatus(s));
        _live.CommandResult += r => Dispatcher.UIThread.Post(() => OnCommandResult(r));
        _live.Alert += a => Dispatcher.UIThread.Post(() => OnAlert(a));
        _live.StateChanged += st => Dispatcher.UIThread.Post(() => Connection = st.ToString());
        _live.Connected += () => Dispatcher.UIThread.Post(async () =>
        {
            // The first connect was already preceded by a refresh; later ones follow a gap in the data.
            if (_connectedBefore) await RefreshAsync();
            _connectedBefore = true;
        });

        await RefreshAsync();
        _ = _live.StartAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        _historyCts?.Cancel();
        await _live.DisposeAsync();
        _api.Dispose();
    }

    // ---- snapshot ----

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _lastRefresh = DateTimeOffset.UtcNow;
        try
        {
            var devices = await _api.GetDevicesAsync(_cts.Token);
            MergeDevices(devices);
            await LoadAlertsAsync();
            ErrorText = null;
        }
        catch (OperationCanceledException) { }
        catch (ApiException e)
        {
            ErrorText = e.Message;
        }
    }

    private void MergeDevices(List<DeviceDto> devices)
    {
        foreach (var dto in devices)
        {
            var key = Keys.Device(dto.Site, dto.Line, dto.Device);
            if (_byKey.TryGetValue(key, out var existing))
            {
                existing.Merge(dto);
                continue;
            }

            var vm = new DeviceViewModel(dto);
            _byKey[key] = vm;
            // Keep the list sorted by site / line / device.
            var index = 0;
            while (index < Devices.Count && string.CompareOrdinal(Devices[index].Key, key) < 0) index++;
            Devices.Insert(index, vm);
        }

        SelectedDevice ??= Devices.FirstOrDefault();
    }

    private async Task LoadAlertsAsync()
    {
        var alerts = await _api.GetAlertsAsync(openOnly: true, _cts.Token);
        Alerts.Clear();
        foreach (var a in alerts.OrderByDescending(a => a.OpenedAt)) Alerts.Add(new AlertViewModel(a, AckAsync));
        OpenAlertCount = Alerts.Count(a => !a.IsAcknowledged);
    }

    private async Task AckAsync(AlertViewModel alert)
    {
        try
        {
            await _api.AckAlertAsync(alert.Id, Environment.UserName, _cts.Token);
            await LoadAlertsAsync();
        }
        catch (ApiException e)
        {
            ErrorText = $"Could not acknowledge the alert: {e.Message}";
        }
    }

    // ---- selection and history ----

    partial void OnSelectedDeviceChanged(DeviceViewModel? value)
    {
        FaultSensor = value?.Sensors.FirstOrDefault();
        FaultMessage = null;
        _awaitingReplyFrom = null;
        _ = LoadHistoryAsync(value);
    }

    private async Task LoadHistoryAsync(DeviceViewModel? device)
    {
        _historyCts?.Cancel();
        if (device is null) return;
        var cts = _historyCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

        var to = DateTimeOffset.UtcNow;
        var from = to - HistoryWindow;
        foreach (var sensor in device.Sensors.ToList())
        {
            try
            {
                var h = await _api.GetHistoryAsync(sensor.SeriesId, from, to, HistoryPoints, cts.Token);
                if (!cts.IsCancellationRequested) sensor.SeedHistory(h.Points);
            }
            catch (OperationCanceledException) { return; }
            catch (ApiException e)
            {
                ErrorText = $"History for {device.Name}/{sensor.Name}: {e.Message}";
            }
        }
    }

    // ---- live events (already on the UI thread) ----

    private void OnTelemetry(IReadOnlyList<LiveReading> batch)
    {
        var unknown = false;
        foreach (var r in batch)
        {
            if (!_byKey.TryGetValue(Keys.Device(r.Site, r.Line, r.Device), out var device))
            {
                unknown = true;
                continue;
            }

            var sensor = device.FindSensor(r.Sensor);
            if (sensor is null) { unknown = true; continue; }
            if (sensor.Unit is null && r.Unit is not null) sensor.Unit = r.Unit;
            sensor.Push(r.Value, r.Time);
            device.LastSeen = r.Time;
        }

        // A device or sensor we have not seen yet: fetch the snapshot, at most every 5 s.
        if (unknown && DateTimeOffset.UtcNow - _lastRefresh > TimeSpan.FromSeconds(5))
            _ = RefreshAsync();
    }

    private void OnStatus(DeviceStatusChanged s)
    {
        if (_byKey.TryGetValue(Keys.Device(s.Site, s.Line, s.Device), out var device))
            device.Status = s.Status;
        else if (DateTimeOffset.UtcNow - _lastRefresh > TimeSpan.FromSeconds(5))
            _ = RefreshAsync();

        AddLog($"{s.Device} is {s.Status}");
    }

    private void OnCommandResult(CommandResultDto r)
    {
        var key = Keys.Device(r.Site, r.Line, r.Device);
        var text = r.Ok ? "accepted the command" : $"rejected the command: {r.Error ?? "no reason given"}";
        AddLog($"{r.Device} {text}");

        if (_awaitingReplyFrom == key)
        {
            _awaitingReplyFrom = null;
            FaultIsError = !r.Ok;
            FaultMessage = r.Ok ? "The device accepted the fault." : $"The device rejected it: {r.Error ?? "it gave no reason"}";
        }
    }

    private async void OnAlert(AlertEvent a)
    {
        AddLog($"ALERT {a.Type} ({a.Severity}): {a.Message}");
        try { await LoadAlertsAsync(); }
        catch (OperationCanceledException) { }
        catch (ApiException e) { ErrorText = e.Message; }
    }

    private void AddLog(string line)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
        while (Log.Count > LogLimit) Log.RemoveAt(Log.Count - 1);
    }

    // ---- commands ----

    private bool CanInject() => SelectedDevice is not null && FaultSensor is not null && !DurationInvalid;

    [RelayCommand(CanExecute = nameof(CanInject))]
    private async Task InjectFaultAsync()
    {
        var device = SelectedDevice;
        var sensor = FaultSensor;
        if (device is null || sensor is null || !TryParseDuration(out var duration)) return;

        FaultIsError = false;
        FaultMessage = "Sent. Waiting for the device to answer…";
        _awaitingReplyFrom = device.Key;
        try
        {
            await _api.InjectFaultAsync(device.Site, device.Line, device.Name,
                new InjectFaultRequest(sensor.Name, FaultName, duration), _cts.Token);
            AddLog($"sent {FaultName} on {device.Name}/{sensor.Name}");
        }
        catch (ApiException e)
        {
            _awaitingReplyFrom = null;
            FaultIsError = true;
            FaultMessage = e.Message;
        }
    }
}
