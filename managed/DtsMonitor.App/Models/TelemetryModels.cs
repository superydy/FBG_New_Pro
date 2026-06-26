namespace DtsMonitor.App.Models;

public sealed class AlarmPointModel
{
    public float PositionM { get; init; }
    public float TemperatureC { get; init; }
    public int SensorIndex { get; init; }
    public int SensorIndexDisplay => SensorIndex + 1;
    public int ZoneNo { get; init; }
    public string TypeCode { get; init; } = string.Empty;
    public string TypeText { get; init; } = string.Empty;
    public string DetailText { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
}

public sealed class SnapshotModel
{
    public DateTime Timestamp { get; init; }
    public long TimestampMs { get; init; }
    public int Channel { get; init; }

    public float[] PositionsM { get; init; } = Array.Empty<float>();
    public float[] TemperaturesC { get; init; } = Array.Empty<float>();
    public float[] SensorPositionsM { get; init; } = Array.Empty<float>();
    public float[] SensorTemperaturesC { get; init; } = Array.Empty<float>();
    public float[] SensorWavelengthsNm { get; init; } = Array.Empty<float>();
    public float[] SpectrumXAxisNm { get; init; } = Array.Empty<float>();
    public float[] SpectrumValues { get; init; } = Array.Empty<float>();
    public int SpectrumSensorIndex { get; init; }
    public float SpectrumSensorPositionM { get; init; }
    public float SpectrumSensorWavelengthNm { get; init; }
    public float SpectrumSensorTemperatureC { get; init; }
    public AlarmPointModel[] Alarms { get; init; } = Array.Empty<AlarmPointModel>();

    public float MinTemp { get; init; }
    public float MaxTemp { get; init; }
    public float AvgTemp { get; init; }
    public float MaxPosM { get; init; }
    public bool StatusOk { get; init; }
}

public sealed class AlarmRecord
{
    public long TimestampMs { get; init; }
    public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs).LocalDateTime.ToString("HH:mm:ss");
    public string FullTimeText => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    public int Channel { get; init; }
    public string ChannelText => $"通道{Channel + 1}";
    public float PositionM { get; init; }
    public string PositionText => float.IsFinite(PositionM) ? PositionM.ToString("F1") : "--";
    public float TemperatureC { get; init; }
    public string TemperatureText => float.IsFinite(TemperatureC) ? TemperatureC.ToString("F2") : "--";
    public int SensorIndex { get; init; }
    public int SensorIndexDisplay => SensorIndex + 1;
    public int ZoneNo { get; init; }
    public string ZoneText => ZoneNo > 0 ? $"分区{ZoneNo}" : "--";
    public string TypeCode { get; init; } = string.Empty;
    public string TypeText { get; init; } = string.Empty;
    public string DetailText { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
    public string TriggerTypeText => ResolveTriggerTypeText();
    public string AlarmTypeText => ResolveAlarmTypeText();

    private string ResolveTriggerTypeText()
    {
        if (!string.IsNullOrWhiteSpace(TypeText))
        {
            return TypeText;
        }

        if (string.IsNullOrWhiteSpace(DetailText))
        {
            return string.Empty;
        }

        if (DetailText.Contains("定温报警", StringComparison.Ordinal))
        {
            return "定温报警";
        }

        if (DetailText.Contains("差温报警", StringComparison.Ordinal))
        {
            return "差温报警";
        }

        if (DetailText.Contains("传感器故障", StringComparison.Ordinal))
        {
            return "传感器故障";
        }

        return string.Empty;
    }

    private string ResolveAlarmTypeText()
    {
        string text = ResolveTriggerTypeText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "未标注";
        }

        return text;
    }
}

public sealed class CalibrationResultModel
{
    public long TimestampMs { get; init; }
    public DateTime Timestamp { get; init; }
    public int Channel { get; init; }
    public int[] SensorPositionsRaw { get; init; } = Array.Empty<int>();
    public int[] SensorWaveIndexesRaw { get; init; } = Array.Empty<int>();
}

public sealed class CalibrationWaveDataModel
{
    public long TimestampMs { get; init; }
    public DateTime Timestamp { get; init; }
    public int Channel { get; init; }
    public int WaveCount { get; init; }
    public int SampleCount { get; init; }
    public float[] WaveValues { get; init; } = Array.Empty<float>();
}

public sealed class HardwareConfig
{
    public int StartWavelengthNm { get; set; } = 1528;
    public int StopWavelengthNm { get; set; } = 1552;
    public int FiberLengthM { get; set; } = 1200;
    public float DelayNs { get; set; } = 10;
    public int PulseWidth { get; set; } = 20;
    public int TargetProfilePoints { get; set; } = 5000;
    public bool OpticSwitchEnabled { get; set; }
    public int EdfaCurrentMa { get; set; } = 61;
    public int EdfaPaCurrentMa { get; set; } = 50;
    public int CalibrationEdfaCurrentMa { get; set; } = 61;
    public int CalibrationEdfaPaCurrentMa { get; set; } = 50;
    public int FiberDensityMode { get; set; } = 0;
    public int WavelengthAverageCount { get; set; } = 1;
    public bool MultiWaveReverse { get; set; }
    public bool AutoRun { get; set; }

    public int SpeedMode { get; set; } = 0;
    public int LaserType { get; set; } = 0;
    public int AlgorithmType { get; set; } = 0;
    public int WavelengthPrecisionMode { get; set; } = 0;

    public int Channel { get; set; } = 0;
    public bool ChannelEnabled { get; set; } = true;
    public float SensorPositionScaleToMeters { get; set; } = 1.0f;
    public float[] CenterWavelengths { get; set; } = new[] { 1532f, 1542f, 1552f };

    public int[] SensorPositionsM { get; set; } = new[] { 50, 100, 150, 200, 250, 300, 350, 400, 450, 500, 550, 600, 650, 700, 750, 800, 850, 900, 950, 1000, 1050, 1100, 1150 };
    public int[] SensorWaveIndexes { get; set; } = new[] { 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1 };
    public float ReferenceTemperatureC { get; set; } = 25.0f;
    public float WavelengthSensitivityNmPerC { get; set; } = 0.01f;
    public float[] SensorTempSensitivityPmPerC { get; set; } = Array.Empty<float>();
    public float[] SensorStrainSensitivity { get; set; } = Array.Empty<float>();
    public float[] SensorReferenceTemperaturesC { get; set; } = Array.Empty<float>();
    public float[] SensorReferenceStrains { get; set; } = Array.Empty<float>();
    public float[] SensorReferenceWavelengthsNm { get; set; } = Array.Empty<float>();
    public float[] SensorReferenceStrainWavelengthsNm { get; set; } = Array.Empty<float>();
    public string CoefficientFilePath { get; set; } = string.Empty;
}

