using CommunityToolkit.Mvvm.ComponentModel;
using Monitor.Operator.Api;

namespace Monitor.Operator.ViewModels;

/// <summary>One sensor of one device: its latest value and a rolling window for the chart.</summary>
public sealed partial class SensorViewModel : ObservableObject
{
    private const int Capacity = 600;
    private readonly List<(DateTimeOffset Time, double Value)> _buffer = new();

    public SensorViewModel(SensorDto dto)
    {
        SeriesId = dto.SeriesId;
        Name = dto.Sensor;
        _unit = dto.Unit;
        if (dto.LatestValue is { } v && dto.LatestTime is { } t)
        {
            _latest = v;
            _buffer.Add((t, v));
            _values = new[] { v };
        }
    }

    public long SeriesId { get; }
    public string Name { get; }

    [ObservableProperty] private string? _unit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestText))]
    private double? _latest;

    /// <summary>Replaced (never mutated) on every change so the chart control sees a new reference.</summary>
    [ObservableProperty] private IReadOnlyList<double> _values = Array.Empty<double>();

    public string LatestText => Latest is { } v ? $"{v:0.###}" : "no data";

    public void Push(double value, DateTimeOffset time)
    {
        // Reconnects and retries can redeliver a reading; drop anything not newer than the last one.
        if (_buffer.Count > 0 && time <= _buffer[^1].Time) return;

        _buffer.Add((time, value));
        if (_buffer.Count > Capacity) _buffer.RemoveRange(0, _buffer.Count - Capacity);
        Latest = value;
        Values = _buffer.Select(p => p.Value).ToArray();
    }

    /// <summary>Puts history in front of whatever live readings already arrived.</summary>
    public void SeedHistory(IEnumerable<HistoryPoint> points)
    {
        var firstLive = _buffer.Count > 0 ? _buffer[0].Time : DateTimeOffset.MaxValue;
        var older = points.Where(p => p.Time < firstLive).OrderBy(p => p.Time)
            .Select(p => (p.Time, p.Avg)).ToList();
        if (older.Count == 0) return;

        _buffer.InsertRange(0, older);
        if (_buffer.Count > Capacity) _buffer.RemoveRange(0, _buffer.Count - Capacity);
        Latest ??= _buffer[^1].Value;
        Values = _buffer.Select(p => p.Value).ToArray();
    }
}
