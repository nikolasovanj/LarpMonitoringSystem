using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Monitor.Operator.Api;

namespace Monitor.Operator.ViewModels;

public sealed partial class DeviceViewModel : ObservableObject
{
    public DeviceViewModel(DeviceDto dto)
    {
        Site = dto.Site;
        Line = dto.Line;
        Name = dto.Device;
        Key = Keys.Device(dto.Site, dto.Line, dto.Device);
        _status = dto.Status;
        _lastSeen = dto.LastSeen;
        foreach (var s in dto.Sensors.OrderBy(s => s.Sensor)) Sensors.Add(new SensorViewModel(s));
    }

    public string Site { get; }
    public string Line { get; }
    public string Name { get; }
    public string Key { get; }
    public string Subtitle => $"{Site} / {Line}";

    public ObservableCollection<SensorViewModel> Sensors { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _status;

    [ObservableProperty] private DateTimeOffset? _lastSeen;

    public bool IsOnline => Status == "online";
    public string StatusText => Status;

    /// <summary>Adds sensors the device did not have before; existing ones keep their buffers.</summary>
    public void Merge(DeviceDto dto)
    {
        Status = dto.Status;
        LastSeen = dto.LastSeen;
        foreach (var s in dto.Sensors)
        {
            var existing = Sensors.FirstOrDefault(x => x.SeriesId == s.SeriesId);
            if (existing is null) Sensors.Add(new SensorViewModel(s));
            else if (s.LatestValue is { } v && s.LatestTime is { } t) existing.Push(v, t);
        }
    }

    public SensorViewModel? FindSensor(string name) => Sensors.FirstOrDefault(s => s.Name == name);
}
