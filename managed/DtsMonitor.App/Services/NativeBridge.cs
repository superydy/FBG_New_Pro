using System.Runtime.InteropServices;
using DtsMonitor.App.Models;
using System;
using System.Linq;

namespace DtsMonitor.App.Services;

public sealed class NativeBridge : IDisposable
{
    private const string CoreDll = "dts_core.dll";
    private const int InitialCalibrationSensorCapacity = 2048;
    private const int MaxCalibrationSensorCapacity = 16384;
    private const int InitialCalibrationWaveValueCapacity = 262144;
    private const int MaxCalibrationWaveValueCapacity = 2097152;
    private const int InitialSnapshotPointCapacity = 8192;
    private const int MaxSnapshotPointCapacity = 65536;
    private const int InitialWaveDataSensorCapacity = 2048;
    private const int MaxWaveDataSensorCapacity = 16384;
    private const int InitialWaveDataSpectrumCapacity = 4096;
    private const int MaxWaveDataSpectrumCapacity = 32768;
    private readonly IntPtr _context;
    private bool _disposed;

    public NativeBridge()
    {
        _context = NativeMethods.DTS_CreateContext();
        if (_context == IntPtr.Zero)
        {
            throw new InvalidOperationException("DTS_CreateContext failed.");
        }
    }

    public int Initialize(string ip, bool use1GInit = true)
    {
        return NativeMethods.DTS_Initialize(_context, ip, use1GInit ? 1 : 0);
    }

    public int Release()
    {
        NativeMethods.DTS_Release(_context);
        return 0;
    }

    public int GetState() => NativeMethods.DTS_GetState(_context);

    public int GetConnect() => NativeMethods.DTS_GetConnect(_context);

    public string GetLastError()
    {
        IntPtr ptr = NativeMethods.DTS_GetLastError(_context);
        return ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
    }

    public int ApplyHardwareConfig(HardwareConfig config)
    {
        int rc = NativeMethods.DTS_SetBasicConfig(
            _context,
            config.StartWavelengthNm,
            config.StopWavelengthNm,
            config.FiberLengthM,
            config.DelayNs,
            config.PulseWidth,
            config.TargetProfilePoints);
        if (rc != 0)
        {
            return rc;
        }

        rc = NativeMethods.DTS_SetHardwareTuning(
            _context,
            config.OpticSwitchEnabled ? 1 : 0,
            config.FiberDensityMode,
            config.MultiWaveReverse ? 1 : 0,
            config.EdfaCurrentMa,
            config.EdfaPaCurrentMa);
        if (rc != 0)
        {
            return rc;
        }

        rc = NativeMethods.DTS_SetRunMode(_context, config.SpeedMode, config.LaserType, config.AlgorithmType, config.WavelengthPrecisionMode, config.WavelengthAverageCount, config.AutoRun ? 1 : 0);
        if (rc != 0)
        {
            return rc;
        }

        rc = NativeMethods.DTS_SetWaveConfig(_context, config.Channel, config.CenterWavelengths.Length, config.CenterWavelengths);
        if (rc != 0)
        {
            return rc;
        }

        rc = NativeMethods.DTS_SetChannelConfig(
            _context,
            config.Channel,
            config.ChannelEnabled ? 1 : 0,
            config.SensorPositionsM.Length,
            config.SensorPositionsM,
            config.SensorWaveIndexes,
            config.SensorPositionScaleToMeters);
        if (rc != 0)
        {
            return rc;
        }

        if (config.SensorTempSensitivityPmPerC.Length == config.SensorPositionsM.Length &&
            config.SensorStrainSensitivity.Length == config.SensorPositionsM.Length &&
            config.SensorReferenceTemperaturesC.Length == config.SensorPositionsM.Length &&
            config.SensorReferenceStrains.Length == config.SensorPositionsM.Length &&
            config.SensorReferenceWavelengthsNm.Length == config.SensorPositionsM.Length &&
            config.SensorReferenceStrainWavelengthsNm.Length == config.SensorPositionsM.Length)
        {
            rc = NativeMethods.DTS_SetSensorCalibrationConfig(
                _context,
                config.Channel,
                config.SensorPositionsM.Length,
                config.SensorTempSensitivityPmPerC,
                config.SensorStrainSensitivity,
                config.SensorReferenceTemperaturesC,
                config.SensorReferenceStrains,
                config.SensorReferenceWavelengthsNm,
                config.SensorReferenceStrainWavelengthsNm);
            if (rc != 0)
            {
                return rc;
            }

            rc = NativeMethods.DTS_SetTemperatureSolveConfig(
                _context,
                config.Channel,
                config.SensorPositionsM.Length,
                config.SensorTempSensitivityPmPerC,
                config.SensorReferenceTemperaturesC,
                config.SensorReferenceWavelengthsNm);
            if (rc != 0)
            {
                return rc;
            }
        }
        else if (config.SensorTempSensitivityPmPerC.Length == config.SensorPositionsM.Length &&
                 config.SensorReferenceTemperaturesC.Length == config.SensorPositionsM.Length &&
                 config.SensorReferenceWavelengthsNm.Length == config.SensorPositionsM.Length)
        {
            rc = NativeMethods.DTS_SetTemperatureSolveConfig(
                _context,
                config.Channel,
                config.SensorPositionsM.Length,
                config.SensorTempSensitivityPmPerC,
                config.SensorReferenceTemperaturesC,
                config.SensorReferenceWavelengthsNm);
            if (rc != 0)
            {
                return rc;
            }
        }

        return NativeMethods.DTS_ApplyHardwareConfig(_context);
    }

    public int StartCalibration(int channel, float threshold) => NativeMethods.DTS_StartCalibration(_context, channel, threshold);

    public int StopCalibration(int channel) => NativeMethods.DTS_StopCalibration(_context, channel);

    public int SetAmplifierCurrents(int edfaCurrentMa, int edfaPaCurrentMa) =>
        NativeMethods.DTS_SetAmplifierCurrents(_context, edfaCurrentMa, edfaPaCurrentMa);

    public int GetAmplifierCurrent() => NativeMethods.DTS_GetAmplifierCurrent(_context);

    public int RecalculateCalibrationPositions(float threshold) =>
        NativeMethods.DTS_RecalculateCalibrationPositions(_context, threshold);

    public CalibrationResultModel? TryReadLatestCalibrationResult(int maxSensorPoints = InitialCalibrationSensorCapacity)
    {
        int sensorCapacity = Math.Max(256, maxSensorPoints);
        while (true)
        {
            int[] sensorPositions = new int[sensorCapacity];
            int[] sensorWaveIndexes = new int[sensorCapacity];
            DtsCalibrationSummaryNative summary = default;

            int rc = NativeMethods.DTS_CopyLatestCalibrationPositions(
                _context,
                sensorPositions,
                sensorWaveIndexes,
                sensorCapacity,
                ref summary);

            if (rc != 0)
            {
                return null;
            }

            bool mayBeTruncated = summary.SensorCount >= sensorCapacity && sensorCapacity < MaxCalibrationSensorCapacity;
            if (mayBeTruncated)
            {
                sensorCapacity = Math.Min(sensorCapacity * 2, MaxCalibrationSensorCapacity);
                continue;
            }

            int sensorCount = Math.Max(0, Math.Min(summary.SensorCount, sensorPositions.Length));
            Array.Resize(ref sensorPositions, sensorCount);
            int[] waveIndexes = sensorWaveIndexes.Take(sensorCount).ToArray();

            return new CalibrationResultModel
            {
                TimestampMs = summary.TimestampMs,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(summary.TimestampMs).LocalDateTime,
                Channel = summary.Channel,
                SensorPositionsRaw = sensorPositions,
                SensorWaveIndexesRaw = waveIndexes
            };
        }
    }

    public CalibrationWaveDataModel? TryReadLatestCalibrationWaveData(int maxValues = InitialCalibrationWaveValueCapacity)
    {
        int valueCapacity = Math.Max(4096, maxValues);
        while (true)
        {
            float[] waveValues = new float[valueCapacity];
            DtsCalibrationWaveSummaryNative summary = default;

            int rc = NativeMethods.DTS_CopyLatestCalibrationWaveData(
                _context,
                waveValues,
                valueCapacity,
                ref summary);

            if (rc != 0)
            {
                return null;
            }

            int requiredCount = Math.Max(0, summary.WaveCount * summary.SampleCount);
            bool mayBeTruncated = requiredCount >= valueCapacity && valueCapacity < MaxCalibrationWaveValueCapacity;
            if (mayBeTruncated)
            {
                valueCapacity = Math.Min(valueCapacity * 2, MaxCalibrationWaveValueCapacity);
                continue;
            }

            int totalCount = Math.Max(0, Math.Min(requiredCount, waveValues.Length));
            Array.Resize(ref waveValues, totalCount);

            return new CalibrationWaveDataModel
            {
                TimestampMs = summary.TimestampMs,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(summary.TimestampMs).LocalDateTime,
                Channel = summary.Channel,
                WaveCount = summary.WaveCount,
                SampleCount = summary.SampleCount,
                WaveValues = waveValues
            };
        }
    }

    public int StartAcquisition(int channel, bool allChannelsLowSpeed)
    {
        return NativeMethods.DTS_StartAcquisition(_context, channel, allChannelsLowSpeed ? 1 : 0);
    }

    public int StopAcquisition(int channel) => NativeMethods.DTS_StopAcquisition(_context, channel);

    public int SetSpectrumSensorIndex(int sensorIndex) => NativeMethods.DTS_SetSpectrumSensorIndex(_context, sensorIndex);

    public SnapshotModel? TryReadLatestSnapshot(int maxPoints = InitialSnapshotPointCapacity)
    {
        int pointCapacity = Math.Max(1024, maxPoints);
        while (true)
        {
            float[] positions = new float[pointCapacity];
            float[] temperatures = new float[pointCapacity];
            DtsSnapshotSummaryNative summary = default;

            int rc = NativeMethods.DTS_CopyLatestSnapshot(
                _context,
                positions,
                temperatures,
                pointCapacity,
                ref summary);

            if (rc != 0)
            {
                return null;
            }

            bool mayBeTruncated = summary.PointCount >= pointCapacity && pointCapacity < MaxSnapshotPointCapacity;
            if (mayBeTruncated)
            {
                pointCapacity = Math.Min(pointCapacity * 2, MaxSnapshotPointCapacity);
                continue;
            }

            int pointCount = Math.Max(0, Math.Min(summary.PointCount, positions.Length));
            Array.Resize(ref positions, pointCount);
            Array.Resize(ref temperatures, pointCount);

            WaveDataModel? waveData = TryReadLatestWaveData();
            if (waveData is not null && waveData.Channel != summary.Channel)
            {
                waveData = null;
            }

            return new SnapshotModel
            {
                TimestampMs = summary.TimestampMs,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(summary.TimestampMs).LocalDateTime,
                Channel = summary.Channel,
                PositionsM = positions,
                TemperaturesC = temperatures,
                SensorPositionsM = waveData?.SensorPositionsM ?? Array.Empty<float>(),
                SensorTemperaturesC = waveData?.SensorTemperaturesC ?? Array.Empty<float>(),
                SensorWavelengthsNm = waveData?.SensorWavelengthsNm ?? Array.Empty<float>(),
                SpectrumXAxisNm = waveData?.SpectrumXAxisNm ?? Array.Empty<float>(),
                SpectrumValues = waveData?.SpectrumValues ?? Array.Empty<float>(),
                SpectrumSensorIndex = waveData?.SelectedSensorIndex ?? 0,
                SpectrumSensorPositionM = waveData?.SelectedSensorPositionM ?? 0,
                SpectrumSensorWavelengthNm = waveData?.SelectedSensorWavelengthNm ?? 0,
                SpectrumSensorTemperatureC = waveData?.SelectedSensorTemperatureC ?? 0,
                Alarms = Array.Empty<AlarmPointModel>(),
                MinTemp = summary.MinTemp,
                MaxTemp = summary.MaxTemp,
                AvgTemp = summary.AvgTemp,
                MaxPosM = summary.MaxPosM,
                StatusOk = summary.StatusOk == 1
            };
        }
    }

    private WaveDataModel? TryReadLatestWaveData(int maxSensorPoints = InitialWaveDataSensorCapacity, int maxSpectrumPoints = InitialWaveDataSpectrumCapacity)
    {
        int sensorCapacity = Math.Max(256, maxSensorPoints);
        int spectrumCapacity = Math.Max(1024, maxSpectrumPoints);
        while (true)
        {
            float[] sensorPositions = new float[sensorCapacity];
            float[] sensorTemperatures = new float[sensorCapacity];
            float[] sensorWavelengths = new float[sensorCapacity];
            float[] spectrumX = new float[spectrumCapacity];
            float[] spectrumY = new float[spectrumCapacity];
            DtsWaveDataSummaryNative summary = default;

            int rc = NativeMethods.DTS_CopyLatestWaveData(
                _context,
                sensorPositions,
                sensorTemperatures,
                sensorWavelengths,
                sensorCapacity,
                spectrumX,
                spectrumY,
                spectrumCapacity,
                ref summary);

            if (rc != 0)
            {
                return null;
            }

            bool mayBeTruncated = summary.SensorCount >= sensorCapacity && sensorCapacity < MaxWaveDataSensorCapacity;
            bool spectrumMayBeTruncated = summary.SpectrumPointCount >= spectrumCapacity && spectrumCapacity < MaxWaveDataSpectrumCapacity;
            if (mayBeTruncated)
            {
                sensorCapacity = Math.Min(sensorCapacity * 2, MaxWaveDataSensorCapacity);
                continue;
            }
            if (spectrumMayBeTruncated)
            {
                spectrumCapacity = Math.Min(spectrumCapacity * 2, MaxWaveDataSpectrumCapacity);
                continue;
            }

            int sensorCount = Math.Max(0, Math.Min(summary.SensorCount, sensorPositions.Length));
            int spectrumCount = Math.Max(0, Math.Min(summary.SpectrumPointCount, spectrumX.Length));

            Array.Resize(ref sensorPositions, sensorCount);
            Array.Resize(ref sensorTemperatures, sensorCount);
            Array.Resize(ref sensorWavelengths, sensorCount);
            Array.Resize(ref spectrumX, spectrumCount);
            Array.Resize(ref spectrumY, spectrumCount);

            return new WaveDataModel
            {
                Channel = summary.Channel,
                SensorPositionsM = sensorPositions,
                SensorTemperaturesC = sensorTemperatures,
                SensorWavelengthsNm = sensorWavelengths,
                SpectrumXAxisNm = spectrumX,
                SpectrumValues = spectrumY,
                SelectedSensorIndex = summary.SelectedSensorIndex,
                SelectedSensorPositionM = summary.SelectedSensorPositionM,
                SelectedSensorTemperatureC = summary.SelectedSensorTemperatureC,
                SelectedSensorWavelengthNm = summary.SelectedSensorWavelengthNm
            };
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            NativeMethods.DTS_Release(_context);
        }
        catch
        {
        }

        try
        {
            NativeMethods.DTS_DestroyContext(_context);
        }
        catch
        {
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~NativeBridge()
    {
        Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DtsSnapshotSummaryNative
    {
        public long TimestampMs;
        public int Channel;
        public int PointCount;
        public float MinTemp;
        public float MaxTemp;
        public float AvgTemp;
        public float MaxPosM;
        public int StatusOk;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DtsWaveDataSummaryNative
    {
        public long TimestampMs;
        public int Channel;
        public int SensorCount;
        public int SpectrumPointCount;
        public int SelectedSensorIndex;
        public float SelectedSensorPositionM;
        public float SelectedSensorTemperatureC;
        public float SelectedSensorWavelengthNm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DtsCalibrationSummaryNative
    {
        public long TimestampMs;
        public int Channel;
        public int SensorCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DtsCalibrationWaveSummaryNative
    {
        public long TimestampMs;
        public int Channel;
        public int WaveCount;
        public int SampleCount;
    }

    private sealed class WaveDataModel
    {
        public int Channel { get; init; }
        public float[] SensorPositionsM { get; init; } = Array.Empty<float>();
        public float[] SensorTemperaturesC { get; init; } = Array.Empty<float>();
        public float[] SensorWavelengthsNm { get; init; } = Array.Empty<float>();
        public float[] SpectrumXAxisNm { get; init; } = Array.Empty<float>();
        public float[] SpectrumValues { get; init; } = Array.Empty<float>();
        public int SelectedSensorIndex { get; init; }
        public float SelectedSensorPositionM { get; init; }
        public float SelectedSensorTemperatureC { get; init; }
        public float SelectedSensorWavelengthNm { get; init; }
    }

    private static class NativeMethods
    {
        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr DTS_CreateContext();

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void DTS_DestroyContext(IntPtr handle);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int DTS_Initialize(IntPtr handle, string device_ip, int use_1g_init);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void DTS_Release(IntPtr handle);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_GetState(IntPtr handle);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr DTS_GetLastError(IntPtr handle);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_GetConnect(IntPtr handle);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_SetBasicConfig(
            IntPtr handle,
            int start_wl,
            int stop_wl,
            int fiber_length_m,
            float delay_ns,
            int pulse_width,
            int target_profile_points);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_SetWaveConfig(IntPtr handle, int channel, int wave_count, float[] center_wavelengths_nm);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_SetChannelConfig(
            IntPtr handle,
            int channel,
            int enabled,
            int fbg_count,
            int[] positions_m,
            int[] wave_indexes,
            float position_scale_to_m);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_SetTemperatureSolveConfig(
            IntPtr handle,
            int channel,
            int sensor_count,
            float[] temp_sensitivity_pm_per_c,
            float[] reference_temperature_c,
            float[] reference_wavelength_nm);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_SetHardwareTuning(
            IntPtr handle,
            int optic_switch_enabled,
            int fiber_density_mode,
            int multi_wave_reverse,
            int edfa_current_ma,
            int edfa_pa_current_ma);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_SetSensorCalibrationConfig(
            IntPtr handle,
            int channel,
            int sensor_count,
            float[] temp_sensitivity_pm_per_c,
            float[] strain_sensitivity,
            float[] reference_temperature_c,
            float[] reference_strain,
            float[] reference_temperature_wavelength_nm,
            float[] reference_strain_wavelength_nm);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_SetSpectrumSensorIndex(IntPtr handle, int sensor_index);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_SetRunMode(IntPtr handle, int speed_mode, int laser_type, int algorithm_type, int wavelength_precision_mode, int wavelength_average_count, int auto_run);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_ApplyHardwareConfig(IntPtr handle);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_StartCalibration(IntPtr handle, int channel, float threshold);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_StopCalibration(IntPtr handle, int channel);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_SetAmplifierCurrents(IntPtr handle, int edfa_current_ma, int edfa_pa_current_ma);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_GetAmplifierCurrent(IntPtr handle);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_RecalculateCalibrationPositions(IntPtr handle, float threshold);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_CopyLatestCalibrationPositions(
            IntPtr handle,
            [Out] int[] sensor_positions_buffer,
            [Out] int[] sensor_wave_indexes_buffer,
            int max_sensor_points,
            ref DtsCalibrationSummaryNative out_summary);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_CopyLatestCalibrationWaveData(
            IntPtr handle,
            [Out] float[] wave_data_buffer,
            int max_values,
            ref DtsCalibrationWaveSummaryNative out_summary);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_StartAcquisition(IntPtr handle, int channel, int low_speed_all_channels);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_StopAcquisition(IntPtr handle, int channel);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_CopyLatestSnapshot(
            IntPtr handle,
            [Out] float[] positions_m_buffer,
            [Out] float[] temperatures_c_buffer,
            int max_points,
            ref DtsSnapshotSummaryNative out_summary);

        [DllImport(CoreDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DTS_CopyLatestWaveData(
            IntPtr handle,
            [Out] float[] sensor_positions_m_buffer,
            [Out] float[] sensor_temperatures_c_buffer,
            [Out] float[] sensor_wavelengths_nm_buffer,
            int max_sensor_points,
            [Out] float[] spectrum_x_nm_buffer,
            [Out] float[] spectrum_y_buffer,
            int max_spectrum_points,
            ref DtsWaveDataSummaryNative out_summary);
    }
}
