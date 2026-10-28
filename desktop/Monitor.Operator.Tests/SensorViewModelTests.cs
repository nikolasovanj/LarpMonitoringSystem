using Monitor.Operator.ViewModels;
using Xunit;

namespace Monitor.Operator.Tests;

public class SensorViewModelTests
{
    [Fact]
    public void Starts_from_the_snapshot_value()
    {
        var s = new SensorViewModel(Make.Sensor(value: 42.5));

        Assert.Equal(42.5, s.Latest);
        Assert.Equal(new[] { 42.5 }, s.Values);
    }

    [Fact]
    public void Starts_empty_without_a_snapshot_value()
    {
        var s = new SensorViewModel(Make.Sensor());

        Assert.Null(s.Latest);
        Assert.Empty(s.Values);
        Assert.Equal("no data", s.LatestText);
    }

    [Fact]
    public void Push_updates_latest_text_and_values()
    {
        var s = new SensorViewModel(Make.Sensor());
        var changed = new List<string?>();
        s.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        s.Push(1.23456, Make.T0.AddSeconds(1));

        Assert.Equal(1.23456, s.Latest);
        Assert.Equal(1.23456.ToString("0.###"), s.LatestText); // culture-dependent separator
        Assert.Equal(new[] { 1.23456 }, s.Values);
        Assert.Contains(nameof(SensorViewModel.Values), changed);
        Assert.Contains(nameof(SensorViewModel.LatestText), changed);
    }

    [Fact]
    public void Push_hands_out_a_new_list_each_time_so_the_chart_redraws()
    {
        var s = new SensorViewModel(Make.Sensor());
        s.Push(1, Make.T0.AddSeconds(1));
        var first = s.Values;

        s.Push(2, Make.T0.AddSeconds(2));

        Assert.NotSame(first, s.Values);
        Assert.Equal(new[] { 1.0 }, first); // the earlier snapshot is untouched
    }

    [Theory]
    [InlineData(0)] // the same timestamp again (a redelivered reading)
    [InlineData(-5)] // an older one
    public void Push_ignores_readings_that_are_not_newer(int secondsRelativeToLast)
    {
        var s = new SensorViewModel(Make.Sensor());
        s.Push(10, Make.T0.AddSeconds(10));

        s.Push(99, Make.T0.AddSeconds(10 + secondsRelativeToLast));

        Assert.Equal(10, s.Latest);
        Assert.Equal(new[] { 10.0 }, s.Values);
    }

    [Fact]
    public void Push_keeps_only_the_newest_600_points()
    {
        var s = new SensorViewModel(Make.Sensor());

        for (var i = 1; i <= 650; i++) s.Push(i, Make.T0.AddSeconds(i));

        Assert.Equal(600, s.Values.Count);
        Assert.Equal(51.0, s.Values[0]);
        Assert.Equal(650.0, s.Values[^1]);
    }

    [Fact]
    public void SeedHistory_goes_in_front_of_live_points_and_in_time_order()
    {
        var s = new SensorViewModel(Make.Sensor());
        s.Push(100, Make.T0.AddSeconds(100));

        // given out of order on purpose
        s.SeedHistory(new[] { Make.Point(20, 2), Make.Point(10, 1), Make.Point(30, 3) });

        Assert.Equal(new[] { 1.0, 2.0, 3.0, 100.0 }, s.Values);
        Assert.Equal(100, s.Latest); // history does not replace the live value
    }

    [Fact]
    public void SeedHistory_drops_points_that_overlap_live_data()
    {
        var s = new SensorViewModel(Make.Sensor());
        s.Push(100, Make.T0.AddSeconds(50));

        s.SeedHistory(new[] { Make.Point(40, 1), Make.Point(50, 2), Make.Point(60, 3) });

        Assert.Equal(new[] { 1.0, 100.0 }, s.Values); // only the point before the first live one
    }

    [Fact]
    public void SeedHistory_on_an_empty_sensor_sets_the_latest_value()
    {
        var s = new SensorViewModel(Make.Sensor());

        s.SeedHistory(new[] { Make.Point(1, 5), Make.Point(2, 6) });

        Assert.Equal(6, s.Latest);
        Assert.Equal(new[] { 5.0, 6.0 }, s.Values);
    }

    [Fact]
    public void SeedHistory_with_no_usable_points_changes_nothing()
    {
        var s = new SensorViewModel(Make.Sensor());
        s.Push(1, Make.T0.AddSeconds(5));
        var before = s.Values;

        s.SeedHistory(new[] { Make.Point(9, 7) }); // newer than the live point

        Assert.Same(before, s.Values);
    }
}
