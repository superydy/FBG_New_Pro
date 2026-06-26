using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DtsMonitor.App.Models;

public enum WorkerState
{
    Offline,
    Starting,
    Online,
    Faulted
}

public enum DeviceState
{
    Disconnected,
    Connecting,
    Connected,
    Running,
    Calibrating,
    Error
}

public sealed class DeviceDefinition
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "设备1";
    public string Ip { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool AutoConnect { get; set; }
    public string DbPath { get; set; } = string.Empty;
    public string UiStatePath { get; set; } = string.Empty;
    public string WorkerPipeName { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastConnectedUtc { get; set; }

    public string DisplayName
    {
        get
        {
            string name = Name?.Trim() ?? string.Empty;
            string ip = Ip?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(ip))
            {
                return $"{name} ({ip})";
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            return string.IsNullOrWhiteSpace(ip) ? DeviceId : ip;
        }
    }

    public override string ToString() => DisplayName;
}

public sealed class DevicesSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public List<DeviceDefinition> Devices { get; set; } = new();
}

public sealed class DeviceViewState : INotifyPropertyChanged
{
    private string _deviceId = string.Empty;
    private string _name = string.Empty;
    private string _ip = string.Empty;
    private WorkerState _workerState;
    private DeviceState _deviceState;
    private int _currentAlarmCount;
    private DateTime? _lastSnapshotTime;
    private string _lastErrorMessage = string.Empty;
    private bool _isBusy;
    private bool _canConnect = true;
    private bool _canStart;
    private bool _canStop;

    public string DeviceId
    {
        get => _deviceId;
        set => SetField(ref _deviceId, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Ip
    {
        get => _ip;
        set => SetField(ref _ip, value);
    }

    public WorkerState WorkerState
    {
        get => _workerState;
        set => SetField(ref _workerState, value);
    }

    public DeviceState DeviceState
    {
        get => _deviceState;
        set => SetField(ref _deviceState, value);
    }

    public int CurrentAlarmCount
    {
        get => _currentAlarmCount;
        set => SetField(ref _currentAlarmCount, value);
    }

    public DateTime? LastSnapshotTime
    {
        get => _lastSnapshotTime;
        set => SetField(ref _lastSnapshotTime, value);
    }

    public string LastErrorMessage
    {
        get => _lastErrorMessage;
        set => SetField(ref _lastErrorMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    public bool CanConnect
    {
        get => _canConnect;
        set => SetField(ref _canConnect, value);
    }

    public bool CanStart
    {
        get => _canStart;
        set => SetField(ref _canStart, value);
    }

    public bool CanStop
    {
        get => _canStop;
        set => SetField(ref _canStop, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class DeviceStatusSnapshot
{
    public WorkerState WorkerState { get; set; } = WorkerState.Offline;
    public DeviceState DeviceState { get; set; } = DeviceState.Disconnected;
    public int NativeState { get; set; }
    public int ConnectState { get; set; }
    public int CurrentAlarmCount { get; set; }
    public long? LastSnapshotTimestampMs { get; set; }
    public string LastErrorMessage { get; set; } = string.Empty;
}
