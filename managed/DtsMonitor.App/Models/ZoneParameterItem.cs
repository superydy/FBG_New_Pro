using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DtsMonitor.App.Models;

public sealed class ZoneParameterItem : INotifyPropertyChanged
{
    private int _zoneNo;
    private string _description = string.Empty;
    private int _startPos;
    private int _endPos;
    private double _alarmLevel1;
    private double _diffTempAlarm;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int ZoneNo
    {
        get => _zoneNo;
        set => SetField(ref _zoneNo, value);
    }

    public string Description
    {
        get => _description;
        set => SetField(ref _description, value);
    }

    public int StartPos
    {
        get => _startPos;
        set => SetField(ref _startPos, value);
    }

    public int EndPos
    {
        get => _endPos;
        set => SetField(ref _endPos, value);
    }

    public double AlarmLevel1
    {
        get => _alarmLevel1;
        set
        {
            if (SetField(ref _alarmLevel1, value))
            {
                OnPropertyChanged(nameof(NoActionTempText));
                OnPropertyChanged(nameof(FixedTempResponseWindowText));
            }
        }
    }

    public double DiffTempAlarm
    {
        get => _diffTempAlarm;
        set
        {
            if (SetField(ref _diffTempAlarm, value))
            {
                OnPropertyChanged(nameof(RateResponseWindowText));
            }
        }
    }

    public string NoActionTempText => AlarmLevel1 switch
    {
        60d => "40",
        70d => "45",
        85d => "60",
        105d => "75",
        138d => "85",
        180d => "108",
        _ => "-"
    };

    public string FixedTempResponseWindowText => AlarmLevel1 switch
    {
        60d => "<=30s",
        70d => "<=30s",
        85d => "<=45s",
        105d => "<=60s",
        138d => "<=60s",
        180d => "<=60s",
        _ => "-"
    };

    public string RateResponseWindowText => DiffTempAlarm switch
    {
        10d => "30s-180s",
        20d => "22.5s-95s",
        30d => "15s-70s",
        _ => "-"
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (!string.IsNullOrWhiteSpace(propertyName))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
