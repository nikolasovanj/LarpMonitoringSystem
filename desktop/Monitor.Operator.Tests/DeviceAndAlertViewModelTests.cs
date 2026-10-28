using Monitor.Operator.ViewModels;
using Xunit;

namespace Monitor.Operator.Tests;

public class DeviceViewModelTests
{
    [Fact]
    public void Builds_key_subtitle_and_online_flag()
    {
        var d = new DeviceViewModel(Make.Device("pump01", "siteA", "line2", "online"));

        Assert.Equal("siteA/line2/pump01", d.Key);
        Assert.Equal("siteA / line2", d.Subtitle);
        Assert.True(d.IsOnline);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("unknown")]
    [InlineData("something-new")]
    public void Anything_but_online_is_not_online(string status)
    {
        Assert.False(new DeviceViewModel(Make.Device(status: status)).IsOnline);
    }

    [Fact]
    public void Sensors_are_listed_alphabetically()
    {
        var d = new DeviceViewModel(Make.Device(sensors: new[]
        {
            Make.Sensor("vibration", 3), Make.Sensor("flow", 2), Make.Sensor("temperature", 1),
        }));

        Assert.Equal(new[] { "flow", "temperature", "vibration" }, d.Sensors.Select(s => s.Name));
    }

    [Fact]
    public void Status_change_raises_IsOnline_and_StatusText()
    {
        var d = new DeviceViewModel(Make.Device(status: "online"));
        var changed = new List<string?>();
        d.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        d.Status = "offline";

        Assert.Contains(nameof(DeviceViewModel.IsOnline), changed);
        Assert.Contains(nameof(DeviceViewModel.StatusText), changed);
        Assert.False(d.IsOnline);
    }

    [Fact]
    public void Merge_adds_new_sensors_and_keeps_the_existing_buffer()
    {
        var d = new DeviceViewModel(Make.Device(sensors: new[] { Make.Sensor("temperature", 1, value: 70) }));
        var temp = d.FindSensor("temperature")!;
        temp.Push(71, Make.T0.AddSeconds(1));

        d.Merge(Make.Device(status: "offline", sensors: new[]
        {
            Make.Sensor("temperature", 1, value: 70), // older than what is already buffered
            Make.Sensor("flow", 2, value: 40),
        }));

        Assert.Same(temp, d.FindSensor("temperature")); // not replaced
        Assert.Equal(new[] { 70.0, 71.0 }, temp.Values);
        Assert.NotNull(d.FindSensor("flow"));
        Assert.Equal("offline", d.Status);
    }

    [Fact]
    public void FindSensor_returns_null_for_an_unknown_name()
    {
        Assert.Null(new DeviceViewModel(Make.Device()).FindSensor("nope"));
    }
}

public class AlertViewModelTests
{
    [Fact]
    public void Source_joins_the_parts_that_exist()
    {
        var dto = Make.Alert() with { Site = "site1", Line = null, Device = "pump01", Sensor = "" };

        var a = new AlertViewModel(dto, _ => Task.CompletedTask);

        Assert.Equal("site1 / pump01", a.Source);
    }

    [Theory]
    [InlineData("critical", true, false)]
    [InlineData("warning", false, true)]
    [InlineData("info", false, false)]
    public void Severity_flags(string severity, bool critical, bool warning)
    {
        var a = new AlertViewModel(Make.Alert(severity: severity), _ => Task.CompletedTask);

        Assert.Equal(critical, a.IsCritical);
        Assert.Equal(warning, a.IsWarning);
    }

    [Fact]
    public async Task Ack_command_calls_back_with_itself()
    {
        AlertViewModel? received = null;
        var a = new AlertViewModel(Make.Alert(), x => { received = x; return Task.CompletedTask; });

        Assert.True(a.AckCommand.CanExecute(null));
        await a.AckCommand.ExecuteAsync(null);

        Assert.Same(a, received);
    }

    [Fact]
    public void An_acknowledged_alert_cannot_be_acknowledged_again()
    {
        var a = new AlertViewModel(Make.Alert(acked: Make.T0, ackedBy: "operator"), _ => Task.CompletedTask);

        Assert.True(a.IsAcknowledged);
        Assert.False(a.AckCommand.CanExecute(null));
    }

    [Fact]
    public void Acknowledging_updates_the_button_state()
    {
        var a = new AlertViewModel(Make.Alert(), _ => Task.CompletedTask);
        var raised = 0;
        a.AckCommand.CanExecuteChanged += (_, _) => raised++;

        a.AcknowledgedAt = Make.T0;

        Assert.True(raised > 0);
        Assert.False(a.AckCommand.CanExecute(null));
    }
}
