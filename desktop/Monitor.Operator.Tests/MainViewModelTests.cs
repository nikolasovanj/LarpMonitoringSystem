using Monitor.Operator.Api;
using Monitor.Operator.ViewModels;
using Xunit;

namespace Monitor.Operator.Tests;

public class MainViewModelTests
{
    private sealed record Rig(MainViewModel Vm, FakeApi Api, FakeLive Live, TestClock Clock);

    /// <summary>A view model on fakes, with the UI thread replaced by "run it now".</summary>
    private static Rig Create(params DeviceDto[] devices)
    {
        var api = new FakeApi { Devices = devices.ToList() };
        var live = new FakeLive();
        var clock = new TestClock();
        var vm = new MainViewModel(api, live, "http://test", a => a(), clock);
        return new Rig(vm, api, live, clock);
    }

    private static async Task<Rig> Started(params DeviceDto[] devices)
    {
        var rig = Create(devices.Length > 0 ? devices : new[] { Make.Device() });
        await rig.Vm.StartAsync();
        return rig;
    }

    // ---- start-up and snapshot ----

    [Fact]
    public async Task Start_loads_devices_and_alerts_selects_the_first_device_and_connects()
    {
        var rig = Create(Make.Device("pump02"), Make.Device("pump01"));
        rig.Api.Alerts = new List<AlertDto> { Make.Alert(1), Make.Alert(2, acked: Make.T0, ackedBy: "me") };

        await rig.Vm.StartAsync();

        Assert.Equal(new[] { "pump01", "pump02" }, rig.Vm.Devices.Select(d => d.Name)); // sorted by key
        Assert.Equal("pump01", rig.Vm.SelectedDevice?.Name);
        Assert.Equal(2, rig.Vm.Alerts.Count);
        Assert.Equal(1, rig.Vm.OpenAlertCount); // the acknowledged one does not count
        Assert.True(rig.Live.Started);
        Assert.Null(rig.Vm.ErrorText);
    }

    [Fact]
    public async Task Devices_are_ordered_by_site_then_line_then_device()
    {
        var rig = Create(
            Make.Device("b", "site2", "line1"),
            Make.Device("a", "site1", "line2"),
            Make.Device("c", "site1", "line1"));

        await rig.Vm.StartAsync();

        Assert.Equal(new[] { "site1/line1/c", "site1/line2/a", "site2/line1/b" }, rig.Vm.Devices.Select(d => d.Key));
    }

    [Fact]
    public async Task An_unreachable_api_shows_an_error_instead_of_throwing_and_recovers_on_refresh()
    {
        var rig = Create(Make.Device());
        rig.Api.Fail = new ApiException(0, "Cannot reach the API. Is the backend running?");

        await rig.Vm.StartAsync();

        Assert.Equal("Cannot reach the API. Is the backend running?", rig.Vm.ErrorText);
        Assert.Empty(rig.Vm.Devices);
        Assert.True(rig.Live.Started); // the live connection keeps trying regardless

        rig.Api.Fail = null;
        await rig.Vm.RefreshCommand.ExecuteAsync(null);

        Assert.Null(rig.Vm.ErrorText);
        Assert.Single(rig.Vm.Devices);
    }

    [Fact]
    public async Task Refresh_keeps_existing_device_objects_and_adds_new_ones()
    {
        var rig = await Started(Make.Device("pump01"));
        var first = rig.Vm.Devices[0];

        rig.Api.Devices = new List<DeviceDto> { Make.Device("pump01", status: "offline"), Make.Device("pump02") };
        await rig.Vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, rig.Vm.Devices.Count);
        Assert.Same(first, rig.Vm.Devices[0]);
        Assert.Equal("offline", first.Status);
    }

    [Fact]
    public async Task Selecting_a_device_seeds_its_charts_from_history()
    {
        var rig = Create(Make.Device(sensors: new[] { Make.Sensor("temperature", 7) }));
        rig.Api.History[7] = new HistoryDto(7, "Raw", 1000,
            new List<HistoryPoint> { Make.Point(1, 10), Make.Point(2, 20) });

        await rig.Vm.StartAsync();

        Assert.Equal(new[] { 10.0, 20.0 }, rig.Vm.SelectedDevice!.Sensors[0].Values);
    }

    // ---- connection state ----

    [Fact]
    public async Task Connection_text_follows_the_hub_state()
    {
        var rig = await Started();

        rig.Live.RaiseState(LiveState.Live);
        Assert.Equal("Live", rig.Vm.Connection);
        Assert.True(rig.Vm.IsLive);

        rig.Live.RaiseState(LiveState.Reconnecting);
        Assert.Equal("Reconnecting", rig.Vm.Connection);
        Assert.False(rig.Vm.IsLive);
    }

    [Fact]
    public async Task Only_a_reconnect_reloads_the_snapshot_not_the_first_connect()
    {
        var rig = await Started();
        Assert.Equal(1, rig.Api.DeviceCalls);

        rig.Live.RaiseConnected(); // first connect: StartAsync already loaded the snapshot
        Assert.Equal(1, rig.Api.DeviceCalls);

        rig.Live.RaiseConnected(); // a reconnect: there may be a gap
        Assert.Equal(2, rig.Api.DeviceCalls);
    }

    // ---- live events ----

    [Fact]
    public async Task Telemetry_reaches_the_right_sensor_and_updates_last_seen()
    {
        var rig = await Started(
            Make.Device("pump01", sensors: new[] { Make.Sensor("temperature", 1), Make.Sensor("flow", 2) }),
            Make.Device("pump02", sensors: new[] { Make.Sensor("temperature", 3) }));

        rig.Live.RaiseTelemetry(
            Make.Reading("temperature", 71.5, device: "pump01"),
            Make.Reading("flow", 40, device: "pump01"),
            Make.Reading("temperature", 99, device: "pump02"));

        var pump01 = rig.Vm.Devices.Single(d => d.Name == "pump01");
        var pump02 = rig.Vm.Devices.Single(d => d.Name == "pump02");
        Assert.Equal(71.5, pump01.FindSensor("temperature")!.Latest);
        Assert.Equal(40, pump01.FindSensor("flow")!.Latest);
        Assert.Equal(99, pump02.FindSensor("temperature")!.Latest);
        Assert.Equal(Make.T0.AddSeconds(1), pump01.LastSeen);
    }

    [Fact]
    public async Task Telemetry_fills_in_a_missing_unit()
    {
        var rig = await Started(Make.Device(sensors: new[] { Make.Sensor("temperature", 1, unit: null) }));

        rig.Live.RaiseTelemetry(Make.Reading("temperature", 70, unit: "C"));

        Assert.Equal("C", rig.Vm.SelectedDevice!.Sensors[0].Unit);
    }

    [Fact]
    public async Task Unknown_devices_trigger_a_snapshot_reload_but_not_more_often_than_every_5_seconds()
    {
        var rig = await Started();
        Assert.Equal(1, rig.Api.DeviceCalls);

        rig.Live.RaiseTelemetry(Make.Reading(device: "new-device")); // right after the load: throttled
        Assert.Equal(1, rig.Api.DeviceCalls);

        rig.Clock.Now += TimeSpan.FromSeconds(6);
        rig.Live.RaiseTelemetry(Make.Reading(device: "new-device"));
        Assert.Equal(2, rig.Api.DeviceCalls);

        rig.Live.RaiseTelemetry(Make.Reading(device: "new-device")); // and throttled again
        Assert.Equal(2, rig.Api.DeviceCalls);
    }

    [Fact]
    public async Task A_status_event_updates_the_device_and_is_logged()
    {
        var rig = await Started(Make.Device("pump01", status: "online"));

        rig.Live.RaiseStatus(new DeviceStatusChanged("site1", "line1", "pump01", "offline", Make.T0));

        Assert.False(rig.Vm.Devices[0].IsOnline);
        Assert.Contains("pump01 is offline", rig.Vm.Log[0]);
    }

    [Fact]
    public async Task The_log_shows_newest_first_and_is_capped()
    {
        var rig = await Started();

        for (var i = 0; i < 250; i++)
            rig.Live.RaiseStatus(new DeviceStatusChanged("site1", "line1", "pump01", i % 2 == 0 ? "online" : "offline", Make.T0));

        Assert.Equal(200, rig.Vm.Log.Count);
        Assert.Contains("is offline", rig.Vm.Log[0]); // the 250th event (i = 249)
    }

    [Fact]
    public async Task An_alert_event_reloads_the_alert_list_and_is_logged()
    {
        var rig = await Started();
        Assert.Empty(rig.Vm.Alerts);
        var callsBefore = rig.Api.AlertCalls;
        rig.Api.Alerts = new List<AlertDto> { Make.Alert(5, "critical") };

        rig.Live.RaiseAlert(new AlertEvent("opened", 5, "critical", "Temperature high", "site1", "line1", "pump01",
            "temperature", 91.5, Make.T0));

        Assert.Equal(callsBefore + 1, rig.Api.AlertCalls);
        Assert.Single(rig.Vm.Alerts);
        Assert.False(rig.Vm.NoAlerts);
        Assert.Contains("ALERT opened (critical): Temperature high", rig.Vm.Log[0]);
    }

    // ---- alerts ----

    [Fact]
    public async Task NoAlerts_tracks_the_list()
    {
        var rig = await Started();
        Assert.True(rig.Vm.NoAlerts);

        rig.Api.Alerts = new List<AlertDto> { Make.Alert() };
        await rig.Vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(rig.Vm.NoAlerts);
    }

    [Fact]
    public async Task Acknowledging_sends_the_os_user_and_reloads()
    {
        var rig = Create(Make.Device());
        rig.Api.Alerts = new List<AlertDto> { Make.Alert(42) };
        await rig.Vm.StartAsync();
        var callsBefore = rig.Api.AlertCalls;

        // the server marks it acknowledged; the console must show what the server says after the reload
        rig.Api.Alerts = new List<AlertDto> { Make.Alert(42, acked: Make.T0, ackedBy: Environment.UserName) };
        await rig.Vm.Alerts[0].AckCommand.ExecuteAsync(null);

        Assert.Equal(new[] { (42L, Environment.UserName) }, rig.Api.Acked);
        Assert.Equal(callsBefore + 1, rig.Api.AlertCalls);
        Assert.True(rig.Vm.Alerts[0].IsAcknowledged);
        Assert.Equal(0, rig.Vm.OpenAlertCount);
    }

    // ---- fault injection ----

    [Fact]
    public async Task The_form_starts_on_the_first_sensor_of_the_selected_device()
    {
        var rig = await Started(Make.Device(sensors: new[] { Make.Sensor("flow", 2), Make.Sensor("pressure", 1) }));

        Assert.Equal("flow", rig.Vm.FaultSensor?.Name); // sensors are listed alphabetically
        Assert.True(rig.Vm.InjectFaultCommand.CanExecute(null));
    }

    [Fact]
    public void Injecting_is_not_possible_before_a_device_is_selected()
    {
        var rig = Create(Make.Device());

        Assert.False(rig.Vm.InjectFaultCommand.CanExecute(null));
    }

    [Fact]
    public async Task Inject_posts_the_chosen_sensor_fault_and_duration()
    {
        var rig = await Started(Make.Device("pump01", "siteA", "line9",
            sensors: new[] { Make.Sensor("temperature", 1), Make.Sensor("vibration", 2) }));
        rig.Vm.FaultSensor = rig.Vm.SelectedDevice!.FindSensor("vibration");
        rig.Vm.FaultName = "stuck";
        rig.Vm.FaultDuration = "25";

        await rig.Vm.InjectFaultCommand.ExecuteAsync(null);

        var call = Assert.Single(rig.Api.Injected);
        Assert.Equal(("siteA", "line9", "pump01"), (call.Site, call.Line, call.Device));
        Assert.Equal(new InjectFaultRequest("vibration", "stuck", 25), call.Body);
        Assert.Equal("Sent. Waiting for the device to answer…", rig.Vm.FaultMessage);
        Assert.False(rig.Vm.FaultIsError);
    }

    [Fact]
    public async Task An_empty_duration_means_the_device_default()
    {
        var rig = await Started();

        await rig.Vm.InjectFaultCommand.ExecuteAsync(null);

        Assert.Null(Assert.Single(rig.Api.Injected).Body.Duration);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("1000001")]
    public async Task A_bad_duration_is_flagged_and_blocks_the_button(string text)
    {
        var rig = await Started();

        rig.Vm.FaultDuration = text;

        Assert.True(rig.Vm.DurationInvalid);
        Assert.False(rig.Vm.InjectFaultCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData(" 12 ")]
    [InlineData("1000000")]
    public async Task A_good_duration_is_accepted(string text)
    {
        var rig = await Started();

        rig.Vm.FaultDuration = text;

        Assert.False(rig.Vm.DurationInvalid);
        Assert.True(rig.Vm.InjectFaultCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_device_accepting_the_command_is_shown()
    {
        var rig = await Started();
        await rig.Vm.InjectFaultCommand.ExecuteAsync(null);

        rig.Live.RaiseCommandResult(new CommandResultDto("site1", "line1", "pump01", true, null, Make.T0));

        Assert.Equal("The device accepted the fault.", rig.Vm.FaultMessage);
        Assert.False(rig.Vm.FaultIsError);
    }

    [Fact]
    public async Task The_device_rejecting_the_command_shows_its_reason()
    {
        var rig = await Started();
        await rig.Vm.InjectFaultCommand.ExecuteAsync(null);

        rig.Live.RaiseCommandResult(
            new CommandResultDto("site1", "line1", "pump01", false, "no sensor named x", Make.T0));

        Assert.Equal("The device rejected it: no sensor named x", rig.Vm.FaultMessage);
        Assert.True(rig.Vm.FaultIsError);
    }

    [Fact]
    public async Task A_rejection_without_a_reason_says_so()
    {
        var rig = await Started();
        await rig.Vm.InjectFaultCommand.ExecuteAsync(null);

        rig.Live.RaiseCommandResult(new CommandResultDto("site1", "line1", "pump01", false, null, Make.T0));

        Assert.Equal("The device rejected it: it gave no reason", rig.Vm.FaultMessage);
    }

    [Fact]
    public async Task A_reply_from_another_device_does_not_answer_this_command()
    {
        var rig = await Started(Make.Device("pump01"), Make.Device("pump02"));
        await rig.Vm.InjectFaultCommand.ExecuteAsync(null); // pump01 is selected

        rig.Live.RaiseCommandResult(new CommandResultDto("site1", "line1", "pump02", false, "boom", Make.T0));

        Assert.StartsWith("Sent.", rig.Vm.FaultMessage);
        Assert.Contains("pump02 rejected the command: boom", rig.Vm.Log[0]); // still in the event log
    }

    [Fact]
    public async Task A_reply_nobody_asked_for_only_goes_to_the_log()
    {
        var rig = await Started();

        rig.Live.RaiseCommandResult(new CommandResultDto("site1", "line1", "pump01", true, null, Make.T0));

        Assert.Null(rig.Vm.FaultMessage);
        Assert.Contains("pump01 accepted the command", rig.Vm.Log[0]);
    }

    [Fact]
    public async Task Only_the_first_reply_answers_a_command()
    {
        var rig = await Started();
        await rig.Vm.InjectFaultCommand.ExecuteAsync(null);
        rig.Live.RaiseCommandResult(new CommandResultDto("site1", "line1", "pump01", true, null, Make.T0));

        rig.Live.RaiseCommandResult(new CommandResultDto("site1", "line1", "pump01", false, "late", Make.T0));

        Assert.Equal("The device accepted the fault.", rig.Vm.FaultMessage);
    }

    [Fact]
    public async Task An_api_error_is_shown_and_no_reply_is_awaited()
    {
        var rig = await Started();
        rig.Api.FailInject = new ApiException(400, "Unknown fault 'spike2'");

        await rig.Vm.InjectFaultCommand.ExecuteAsync(null);

        Assert.Equal("Unknown fault 'spike2'", rig.Vm.FaultMessage);
        Assert.True(rig.Vm.FaultIsError);

        rig.Live.RaiseCommandResult(new CommandResultDto("site1", "line1", "pump01", true, null, Make.T0));
        Assert.Equal("Unknown fault 'spike2'", rig.Vm.FaultMessage); // a stray reply does not overwrite it
    }

    [Fact]
    public async Task Selecting_another_device_resets_the_form()
    {
        var rig = await Started(
            Make.Device("pump01", sensors: new[] { Make.Sensor("temperature", 1) }),
            Make.Device("pump02", sensors: new[] { Make.Sensor("flow", 2) }));
        await rig.Vm.InjectFaultCommand.ExecuteAsync(null);

        rig.Vm.SelectedDevice = rig.Vm.Devices[1];

        Assert.Null(rig.Vm.FaultMessage);
        Assert.Equal("flow", rig.Vm.FaultSensor?.Name);

        // and a late reply from the first device no longer lands on this form
        rig.Live.RaiseCommandResult(new CommandResultDto("site1", "line1", "pump01", false, "late", Make.T0));
        Assert.Null(rig.Vm.FaultMessage);
    }

    [Fact]
    public async Task The_form_never_ends_up_without_a_sensor_when_the_combo_box_clears_it()
    {
        var rig = await Started();

        rig.Vm.FaultSensor = null; // what the ComboBox does when its item list is replaced

        Assert.NotNull(rig.Vm.FaultSensor);
    }
}
