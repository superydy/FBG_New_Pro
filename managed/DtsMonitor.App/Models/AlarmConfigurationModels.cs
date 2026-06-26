namespace DtsMonitor.App.Models;

public sealed class AlarmChannelSettingsModel
{
    public int Channel { get; init; }
    public bool EnableAlarmL1 { get; init; }
    public bool EnableDiffAlarm { get; init; }
    public float TempCorrectionC { get; init; }
    public int[] SourceSensorIndexes { get; init; } = Array.Empty<int>();
    public float[] SensorPositionsM { get; init; } = Array.Empty<float>();
    public float[] TempSensitivityPmPerC { get; init; } = Array.Empty<float>();
    public float[] ReferenceTemperaturesC { get; init; } = Array.Empty<float>();
    public float[] ReferenceWavelengthsNm { get; init; } = Array.Empty<float>();
    public ZoneParameterItem[] Zones { get; init; } = Array.Empty<ZoneParameterItem>();

    public bool IsEnabled =>
        Zones.Length > 0 &&
        (EnableAlarmL1 || EnableDiffAlarm);
}

public sealed class AlarmSettingsModel
{
    public AlarmChannelSettingsModel[] Channels { get; init; } = Array.Empty<AlarmChannelSettingsModel>();

    public bool IsEnabled
    {
        get
        {
            for (int i = 0; i < Channels.Length; i++)
            {
                if (Channels[i].IsEnabled)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool TryGetChannelSettings(int channel, out AlarmChannelSettingsModel settings)
    {
        for (int i = 0; i < Channels.Length; i++)
        {
            if (Channels[i].Channel == channel)
            {
                settings = Channels[i];
                return true;
            }
        }

        settings = new AlarmChannelSettingsModel();
        return false;
    }
}
