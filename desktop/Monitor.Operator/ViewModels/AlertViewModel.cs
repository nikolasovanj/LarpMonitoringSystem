using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monitor.Operator.Api;

namespace Monitor.Operator.ViewModels;

public sealed partial class AlertViewModel : ObservableObject
{
    private readonly Func<AlertViewModel, Task> _ack;

    public AlertViewModel(AlertDto dto, Func<AlertViewModel, Task> ack)
    {
        _ack = ack;
        Id = dto.Id;
        Severity = dto.Severity;
        Message = dto.Message;
        Source = string.Join(" / ", new[] { dto.Site, dto.Line, dto.Device, dto.Sensor }.Where(s => !string.IsNullOrEmpty(s)));
        OpenedAt = dto.OpenedAt;
        _acknowledgedBy = dto.AcknowledgedBy;
        _acknowledgedAt = dto.AcknowledgedAt;
    }

    public long Id { get; }
    public string Severity { get; }
    public string Message { get; }
    public string Source { get; }
    public DateTimeOffset OpenedAt { get; }

    public bool IsCritical => Severity == "critical";
    public bool IsWarning => Severity == "warning";
    public string OpenedText => OpenedAt.ToLocalTime().ToString("HH:mm:ss");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAcknowledged))]
    [NotifyCanExecuteChangedFor(nameof(AckCommand))]
    private DateTimeOffset? _acknowledgedAt;

    [ObservableProperty] private string? _acknowledgedBy;

    public bool IsAcknowledged => AcknowledgedAt is not null;

    [RelayCommand(CanExecute = nameof(CanAck))]
    private Task AckAsync() => _ack(this);

    private bool CanAck() => !IsAcknowledged;
}
