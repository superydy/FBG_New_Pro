using System.Globalization;
using System.Threading;
using DtsMonitor.App.Models;

namespace DtsMonitor.App.Services;

public sealed class MonitoringService : IDisposable
{
    private static readonly TimeSpan SnapshotPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RateHistoryRetention = TimeSpan.FromMinutes(16);
    private const int SensorFaultConfirmSamples = 3;
    private const int FixedTempConfirmSamples = 2;
    private const int RateAlarmConfirmSamples = 2;
    private const int AlarmRecoveryConfirmSamples = 2;
    private readonly NativeBridge _native;
    private readonly SnapshotStore _store;
    private readonly Timer _pollTimer;
    private readonly object _sync = new();
    private readonly object _alarmSync = new();

    private readonly Dictionary<int, long> _lastSnapshotTsByChannel = new();
    private AlarmSettingsModel _alarmSettings = new();
    private readonly HashSet<string> _activeAlarmKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _latchedAlarmKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _suppressedAlarmKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _alarmRecoveryMissCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _fixedTempCandidateCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ZoneTrendState> _zoneTrendStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _sensorFaultCounts = new(StringComparer.Ordinal);
    private int _polling;
    private bool _disposed;
    private bool _primeAlarmStateOnNextSnapshot;

    public event Action<SnapshotModel>? SnapshotUpdated;
    public event Action<AlarmRecord>? AlarmRaised;

    public MonitoringService(string databasePath)
    {
        _native = new NativeBridge();
        _store = new SnapshotStore(databasePath);
        _pollTimer = new Timer(PollSnapshot, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public SnapshotStore Store => _store;

    public void UpdateAlarmSettings(AlarmSettingsModel settings)
    {
        settings ??= new AlarmSettingsModel();

        AlarmSettingsModel normalized = new()
        {
            Channels = settings.Channels
                .Select(channel => new AlarmChannelSettingsModel
                {
                    Channel = channel.Channel,
                    EnableAlarmL1 = channel.EnableAlarmL1,
                    EnableDiffAlarm = channel.EnableDiffAlarm,
                    TempCorrectionC = channel.TempCorrectionC,
                    SensorPositionsM = channel.SensorPositionsM.ToArray(),
                    TempSensitivityPmPerC = channel.TempSensitivityPmPerC.ToArray(),
                    ReferenceTemperaturesC = channel.ReferenceTemperaturesC.ToArray(),
                    ReferenceWavelengthsNm = channel.ReferenceWavelengthsNm.ToArray(),
                    Zones = channel.Zones
                        .Select(CloneZone)
                        .OrderBy(z => z.ZoneNo)
                        .ToArray()
                })
                .OrderBy(channel => channel.Channel)
                .ToArray()
        };

        lock (_alarmSync)
        {
            _alarmSettings = normalized;
            _activeAlarmKeys.Clear();
            _latchedAlarmKeys.Clear();
            _suppressedAlarmKeys.Clear();
            _alarmRecoveryMissCounts.Clear();
            _fixedTempCandidateCounts.Clear();
            _zoneTrendStates.Clear();
            _sensorFaultCounts.Clear();
            _primeAlarmStateOnNextSnapshot = true;
        }
    }

    public void ResetCurrentAlarms()
    {
        lock (_alarmSync)
        {
            foreach (string key in _activeAlarmKeys)
            {
                _suppressedAlarmKeys.Add(key);
            }

            _activeAlarmKeys.Clear();
            _latchedAlarmKeys.Clear();
            _alarmRecoveryMissCounts.Clear();
            _fixedTempCandidateCounts.Clear();
            _zoneTrendStates.Clear();
        }
    }

    public int Connect(string ip, bool use1GInit = true)
    {
        return _native.Initialize(ip, use1GInit);
    }

    public int GetConnect()
    {
        return _native.GetConnect();
    }

    public int Disconnect()
    {
        StopSnapshotPolling();
        return _native.Release();
    }

    public int GetState()
    {
        return _native.GetState();
    }

    public string GetLastError() => _native.GetLastError();

    public int ApplyConfig(HardwareConfig config)
    {
        return _native.ApplyHardwareConfig(config);
    }

    public int StartCalibration(int channel, float threshold)
    {
        return _native.StartCalibration(channel, threshold);
    }

    public int StopCalibration(int channel)
    {
        return _native.StopCalibration(channel);
    }

    public CalibrationResultModel? TryReadLatestCalibrationResult()
    {
        try
        {
            return _native.TryReadLatestCalibrationResult();
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    public CalibrationWaveDataModel? TryReadLatestCalibrationWaveData()
    {
        try
        {
            return _native.TryReadLatestCalibrationWaveData();
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    public int SetAmplifierCurrents(int edfaCurrentMa, int edfaPaCurrentMa)
    {
        try
        {
            return _native.SetAmplifierCurrents(edfaCurrentMa, edfaPaCurrentMa);
        }
        catch (EntryPointNotFoundException)
        {
            return -1;
        }
    }

    public int GetAmplifierCurrent()
    {
        try
        {
            return _native.GetAmplifierCurrent();
        }
        catch (EntryPointNotFoundException)
        {
            return -1;
        }
    }

    public int RecalculateCalibrationPositions(float threshold)
    {
        try
        {
            return _native.RecalculateCalibrationPositions(threshold);
        }
        catch (EntryPointNotFoundException)
        {
            return -1;
        }
    }

    public int StartAcquisition(int channel, bool allChannelsLowSpeed)
    {
        int rc = _native.StartAcquisition(channel, allChannelsLowSpeed);
        if (rc == 0)
        {
            StartSnapshotPolling();
        }

        return rc;
    }

    public int StopAcquisition(int channel)
    {
        StopSnapshotPolling();
        return _native.StopAcquisition(channel);
    }

    public int SetSpectrumSensorIndex(int sensorIndex)
    {
        return _native.SetSpectrumSensorIndex(sensorIndex);
    }

    public SnapshotModel? TryReadLatestSnapshotNow()
    {
        if (_native.GetState() == 0)
        {
            return null;
        }

        SnapshotModel? snapshot = _native.TryReadLatestSnapshot();
        return snapshot is null ? null : EvaluateAlarmSnapshot(snapshot);
    }

    private void PollSnapshot(object? state)
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            if (_native.GetState() == 0)
            {
                return;
            }

            SnapshotModel? snapshot;
            try
            {
                snapshot = _native.TryReadLatestSnapshot();
            }
            catch
            {
                return;
            }

            if (snapshot is null || snapshot.PositionsM.Length == 0)
            {
                return;
            }

            bool isNewSnapshot;
            lock (_sync)
            {
                isNewSnapshot = !_lastSnapshotTsByChannel.TryGetValue(snapshot.Channel, out long previousTimestampMs) ||
                                snapshot.TimestampMs > previousTimestampMs;
                if (isNewSnapshot)
                {
                    _lastSnapshotTsByChannel[snapshot.Channel] = snapshot.TimestampMs;
                }
            }

            if (!isNewSnapshot)
            {
                return;
            }

            snapshot = EvaluateAlarmSnapshot(snapshot);

            if (TryPrimeAlarmState(snapshot, out SnapshotModel? primedSnapshot) && primedSnapshot is not null)
            {
                snapshot = primedSnapshot;
            }

            _store.EnqueueSnapshot(snapshot);
            SnapshotUpdated?.Invoke(snapshot);
            EmitAlarmEvents(snapshot);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private SnapshotModel EvaluateAlarmSnapshot(SnapshotModel snapshot)
    {
        AlarmChannelSettingsModel settings;
        lock (_alarmSync)
        {
            if (!_alarmSettings.TryGetChannelSettings(snapshot.Channel, out settings))
            {
                return CloneSnapshotWithAlarms(snapshot, Array.Empty<AlarmPointModel>());
            }
        }

        if (snapshot.PositionsM.Length == 0 || snapshot.TemperaturesC.Length == 0)
        {
            return CloneSnapshotWithAlarms(snapshot, Array.Empty<AlarmPointModel>());
        }

        var alarms = new List<AlarmPointModel>();
        lock (_alarmSync)
        {
            if (TryEvaluateSensorFaultAlarm(snapshot, out AlarmPointModel? faultAlarm) && faultAlarm is not null)
            {
                alarms.Add(faultAlarm);
            }

            if (settings.IsEnabled)
            {
                foreach (ZoneParameterItem zone in settings.Zones)
                {
                    if (!TryBuildZonePoints(snapshot, settings, zone, settings.TempCorrectionC, out List<ZonePoint> points))
                    {
                        continue;
                    }

                    if (settings.EnableAlarmL1 && zone.AlarmLevel1 > 0)
                    {
                        foreach (ZonePoint point in points)
                        {
                            string fixedTempSourceKey = $"{snapshot.Channel}:zone:{zone.ZoneNo}:fixed_temp:sensor:{point.Index}";
                            if (point.Temperature < zone.AlarmLevel1)
                            {
                                _fixedTempCandidateCounts.Remove(fixedTempSourceKey);
                                continue;
                            }

                            int candidateCount = _fixedTempCandidateCounts.TryGetValue(fixedTempSourceKey, out int existingCount)
                                ? existingCount + 1
                                : 1;
                            _fixedTempCandidateCounts[fixedTempSourceKey] = candidateCount;
                            if (candidateCount < FixedTempConfirmSamples)
                            {
                                continue;
                            }

                            var metrics = new ZoneMetrics(
                                zone.ZoneNo,
                                point.Temperature,
                                point.Position,
                                point.Index);
                            alarms.Add(CreateAlarm(
                                metrics,
                                zone,
                                "fixed_temp",
                                "定温报警",
                                point.Temperature,
                                point.Position,
                                point.Index,
                                $"{point.Temperature:F2}℃ >= {zone.AlarmLevel1:F2}℃，位置 {point.Position:F1}m",
                                fixedTempSourceKey));
                        }
                    }

                    if (settings.EnableDiffAlarm &&
                        zone.DiffTempAlarm > 0 &&
                        TryEvaluateRateAlarms(snapshot.Channel, snapshot.TimestampMs, zone, points, out IReadOnlyList<AlarmPointModel>? diffAlarms) &&
                        diffAlarms is not null)
                    {
                        alarms.AddRange(diffAlarms);
                    }
                }
            }
        }

        string[] alarmKeys = alarms
            .Select(alarm => BuildAlarmKey(snapshot.Channel, alarm))
            .ToArray();
        lock (_alarmSync)
        {
            _suppressedAlarmKeys.IntersectWith(alarmKeys);
            if (_suppressedAlarmKeys.Count > 0)
            {
                alarms = alarms
                    .Where(alarm => !_suppressedAlarmKeys.Contains(BuildAlarmKey(snapshot.Channel, alarm)))
                    .ToList();
            }
        }

        return CloneSnapshotWithAlarms(snapshot, alarms.ToArray());
    }

    private bool TryPrimeAlarmState(SnapshotModel snapshot, out SnapshotModel? primedSnapshot)
    {
        primedSnapshot = null;

        lock (_alarmSync)
        {
            if (!_primeAlarmStateOnNextSnapshot)
            {
                return false;
            }

            _primeAlarmStateOnNextSnapshot = false;
            _activeAlarmKeys.Clear();
            _alarmRecoveryMissCounts.Clear();
            _fixedTempCandidateCounts.Clear();

            foreach (AlarmPointModel alarm in snapshot.Alarms)
            {
                string key = BuildAlarmKey(snapshot.Channel, alarm);
                _activeAlarmKeys.Add(key);
                _latchedAlarmKeys.Add(key);
            }
        }

        primedSnapshot = CloneSnapshotWithAlarms(snapshot, Array.Empty<AlarmPointModel>());
        return true;
    }

    private bool TryEvaluateSensorFaultAlarm(SnapshotModel snapshot, out AlarmPointModel? alarm)
    {
        alarm = null;

        float[] positions = snapshot.SensorPositionsM.Length > 0 ? snapshot.SensorPositionsM : snapshot.PositionsM;
        float[] temperatures = snapshot.SensorTemperaturesC.Length > 0 ? snapshot.SensorTemperaturesC : snapshot.TemperaturesC;
        int sensorCount = Math.Min(positions.Length, temperatures.Length);
        string channelPrefix = $"{snapshot.Channel}:sensor_fault:";

        if (sensorCount <= 1)
        {
            foreach (string staleKey in _sensorFaultCounts.Keys.Where(key => key.StartsWith(channelPrefix, StringComparison.Ordinal)).ToArray())
            {
                _sensorFaultCounts.Remove(staleKey);
            }

            return false;
        }

        int firstFaultIndex = -1;
        bool hasValidPrefix = false;
        for (int i = 0; i < sensorCount; i++)
        {
            bool isValid = float.IsFinite(positions[i]) && float.IsFinite(temperatures[i]);
            if (isValid)
            {
                hasValidPrefix = true;
                continue;
            }

            if (!hasValidPrefix)
            {
                continue;
            }

            bool suffixAllInvalid = true;
            for (int j = i; j < sensorCount; j++)
            {
                if (float.IsFinite(positions[j]) && float.IsFinite(temperatures[j]))
                {
                    suffixAllInvalid = false;
                    break;
                }
            }

            if (suffixAllInvalid)
            {
                firstFaultIndex = i;
                break;
            }
        }

        if (firstFaultIndex < 0)
        {
            foreach (string staleKey in _sensorFaultCounts.Keys.Where(key => key.StartsWith(channelPrefix, StringComparison.Ordinal)).ToArray())
            {
                _sensorFaultCounts.Remove(staleKey);
            }

            return false;
        }

        string activeKey = $"{snapshot.Channel}:sensor_fault:sensor:{firstFaultIndex}";
        foreach (string staleKey in _sensorFaultCounts.Keys.Where(key => key.StartsWith(channelPrefix, StringComparison.Ordinal) && !string.Equals(key, activeKey, StringComparison.Ordinal)).ToArray())
        {
            _sensorFaultCounts.Remove(staleKey);
        }

        int count = _sensorFaultCounts.TryGetValue(activeKey, out int existingCount)
            ? existingCount + 1
            : 1;
        _sensorFaultCounts[activeKey] = count;

        if (count < SensorFaultConfirmSamples)
        {
            return false;
        }

        float position = positions[firstFaultIndex];
        alarm = new AlarmPointModel
        {
            PositionM = position,
            TemperatureC = float.NaN,
            SensorIndex = firstFaultIndex,
            ZoneNo = 0,
            TypeCode = "sensor_fault",
            TypeText = "传感器故障",
            DetailText = $"传感器{firstFaultIndex + 1} 后续数据连续无效，位置 {position:F1}m",
            SourceKey = $"sensor_fault:sensor:{firstFaultIndex}"
        };
        return true;
    }

    private static bool TryBuildZonePoints(
        SnapshotModel snapshot,
        AlarmChannelSettingsModel settings,
        ZoneParameterItem zone,
        float tempCorrection,
        out List<ZonePoint> points)
    {
        points = new List<ZonePoint>();

        if (snapshot.SensorPositionsM.Length > 0 && snapshot.SensorTemperaturesC.Length > 0)
        {
            float[] sensorTemperatures = ResolveAlarmSensorTemperatures(snapshot, settings);
            float[] resolvedSensorPositions = settings.SensorPositionsM.Length > 0 &&
                                              settings.SensorPositionsM.Length <= sensorTemperatures.Length
                ? settings.SensorPositionsM
                : snapshot.SensorPositionsM;
            int sensorCount = Math.Min(resolvedSensorPositions.Length, sensorTemperatures.Length);
            for (int i = 0; i < sensorCount; i++)
            {
                float position = resolvedSensorPositions[i];
                float temperature = sensorTemperatures[i];
                if (!float.IsFinite(position) || !float.IsFinite(temperature))
                {
                    continue;
                }

                if (position < zone.StartPos || position > zone.EndPos)
                {
                    continue;
                }

                points.Add(new ZonePoint(i, position, temperature + tempCorrection));
            }

            return points.Count > 0;
        }

        int pointCount = Math.Min(snapshot.PositionsM.Length, snapshot.TemperaturesC.Length);
        if (pointCount <= 0)
        {
            return false;
        }

        float[] sensorPositions = settings.SensorPositionsM;
        float matchTolerance = GetSensorMatchTolerance(sensorPositions);
        Dictionary<int, (ZonePoint Point, float Distance)> mappedPoints = new();

        for (int i = 0; i < pointCount; i++)
        {
            float position = snapshot.PositionsM[i];
            float temperature = snapshot.TemperaturesC[i];
            if (!float.IsFinite(position) || !float.IsFinite(temperature))
            {
                continue;
            }

            if (position < zone.StartPos || position > zone.EndPos)
            {
                continue;
            }

            int sensorIndex = i;
            float distance = float.MaxValue;
            if (TryResolveSensorIndexByPosition(position, sensorPositions, matchTolerance, out int matchedSensorIndex, out float matchedDistance))
            {
                sensorIndex = matchedSensorIndex;
                distance = matchedDistance;
            }

            if (sensorPositions.Length == 0)
            {
                points.Add(new ZonePoint(sensorIndex, position, temperature + tempCorrection));
                continue;
            }

            var zonePoint = new ZonePoint(sensorIndex, position, temperature + tempCorrection);
            if (!mappedPoints.TryGetValue(sensorIndex, out var existing) || distance < existing.Distance)
            {
                mappedPoints[sensorIndex] = (zonePoint, distance);
            }
        }

        if (sensorPositions.Length > 0)
        {
            points = mappedPoints.Values
                .Select(x => x.Point)
                .OrderBy(x => x.Index)
                .ThenBy(x => x.Position)
                .ToList();
        }

        return points.Count > 0;
    }

    private static float[] ResolveAlarmSensorTemperatures(SnapshotModel snapshot, AlarmChannelSettingsModel settings)
    {
        if (snapshot.SensorWavelengthsNm.Length > 0 &&
            settings.TempSensitivityPmPerC.Length > 0 &&
            settings.ReferenceTemperaturesC.Length > 0 &&
            settings.ReferenceWavelengthsNm.Length > 0)
        {
            int sensorCount = Math.Min(
                settings.TempSensitivityPmPerC.Length,
                Math.Min(settings.ReferenceTemperaturesC.Length, settings.ReferenceWavelengthsNm.Length));

            if (sensorCount > 0)
            {
                float[] alignedWavelengths = AlignWavelengthsToAlarmSettings(snapshot, settings, sensorCount);
                float[] recalculated = Enumerable.Repeat(float.NaN, sensorCount).ToArray();

                for (int i = 0; i < sensorCount; i++)
                {
                    if (i >= alignedWavelengths.Length)
                    {
                        continue;
                    }

                    float wavelength = alignedWavelengths[i];
                    float sensitivityPm = settings.TempSensitivityPmPerC[i];
                    float referenceTemperature = settings.ReferenceTemperaturesC[i];
                    float referenceWavelength = settings.ReferenceWavelengthsNm[i];

                    if (!float.IsFinite(wavelength) ||
                        !float.IsFinite(sensitivityPm) ||
                        Math.Abs(sensitivityPm) <= 0.0001f ||
                        !float.IsFinite(referenceTemperature) ||
                        !float.IsFinite(referenceWavelength))
                    {
                        continue;
                    }

                    recalculated[i] = referenceTemperature + ((wavelength - referenceWavelength) * 1000f / sensitivityPm);
                }

                return recalculated;
            }
        }

        return snapshot.SensorTemperaturesC;
    }

    private static float[] AlignWavelengthsToAlarmSettings(
        SnapshotModel snapshot,
        AlarmChannelSettingsModel settings,
        int targetCount)
    {
        float[] aligned = Enumerable.Repeat(float.NaN, targetCount).ToArray();
        bool[] used = new bool[snapshot.SensorWavelengthsNm.Length];
        float[] sourcePositions = snapshot.SensorPositionsM.Length > 0 ? snapshot.SensorPositionsM : snapshot.PositionsM;

        for (int targetIndex = 0; targetIndex < targetCount; targetIndex++)
        {
            int sourceIndex = FindBestAlarmSensorMatch(snapshot, settings, sourcePositions, targetIndex, used);
            if (sourceIndex < 0)
            {
                continue;
            }

            used[sourceIndex] = true;
            aligned[targetIndex] = snapshot.SensorWavelengthsNm[sourceIndex];
        }

        return aligned;
    }

    private static int FindBestAlarmSensorMatch(
        SnapshotModel snapshot,
        AlarmChannelSettingsModel settings,
        float[] sourcePositions,
        int targetIndex,
        bool[] used)
    {
        float targetPosition = targetIndex < settings.SensorPositionsM.Length
            ? settings.SensorPositionsM[targetIndex]
            : float.NaN;
        float expectedWavelength = targetIndex < settings.ReferenceWavelengthsNm.Length
            ? settings.ReferenceWavelengthsNm[targetIndex]
            : float.NaN;

        int bestIndex = -1;
        float bestScore = float.PositiveInfinity;
        for (int sourceIndex = 0; sourceIndex < snapshot.SensorWavelengthsNm.Length; sourceIndex++)
        {
            if (sourceIndex < used.Length && used[sourceIndex])
            {
                continue;
            }

            float wavelength = snapshot.SensorWavelengthsNm[sourceIndex];
            if (!float.IsFinite(wavelength) || wavelength <= 0)
            {
                continue;
            }

            float score = 0f;
            if (float.IsFinite(expectedWavelength) && expectedWavelength > 0)
            {
                score += Math.Abs(wavelength - expectedWavelength) * 100f;
            }
            else
            {
                score += sourceIndex == targetIndex ? 0f : 50f;
            }

            if (sourceIndex < sourcePositions.Length &&
                float.IsFinite(sourcePositions[sourceIndex]) &&
                float.IsFinite(targetPosition))
            {
                score += Math.Abs(sourcePositions[sourceIndex] - targetPosition) * 2f;
            }
            else
            {
                score += Math.Abs(sourceIndex - targetIndex) * 0.25f;
            }

            if (score < bestScore)
            {
                bestScore = score;
                bestIndex = sourceIndex;
            }
        }

        return bestIndex;
    }

    private static bool TryResolveSensorIndexByPosition(float position, float[] sensorPositions, float tolerance, out int sensorIndex, out float distance)
    {
        sensorIndex = -1;
        distance = float.MaxValue;
        if (sensorPositions.Length == 0)
        {
            return false;
        }

        int nearestIndex = FindNearestSensorIndex(position, sensorPositions, out float nearestDistance);
        if (nearestIndex < 0 || nearestDistance > tolerance)
        {
            return false;
        }

        sensorIndex = nearestIndex;
        distance = nearestDistance;
        return true;
    }

    private static int FindNearestSensorIndex(float position, float[] sensorPositions, out float nearestDistance)
    {
        nearestDistance = float.MaxValue;
        int nearestIndex = -1;
        for (int i = 0; i < sensorPositions.Length; i++)
        {
            float sensorPosition = sensorPositions[i];
            if (!float.IsFinite(sensorPosition))
            {
                continue;
            }

            float distance = Math.Abs(sensorPosition - position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = i;
            }
        }

        return nearestIndex;
    }

    private static float GetSensorMatchTolerance(float[] sensorPositions)
    {
        if (sensorPositions.Length <= 1)
        {
            return 0.5f;
        }

        float minSpacing = float.MaxValue;
        for (int i = 1; i < sensorPositions.Length; i++)
        {
            float previous = sensorPositions[i - 1];
            float current = sensorPositions[i];
            if (!float.IsFinite(previous) || !float.IsFinite(current))
            {
                continue;
            }

            float spacing = Math.Abs(current - previous);
            if (spacing > 0.0001f && spacing < minSpacing)
            {
                minSpacing = spacing;
            }
        }

        if (!float.IsFinite(minSpacing) || minSpacing == float.MaxValue)
        {
            return 0.5f;
        }

        return Math.Max(0.15f, minSpacing * 0.45f);
    }

    private bool TryEvaluateRateAlarms(int channel, long timestampMs, ZoneParameterItem zone, IReadOnlyList<ZonePoint> points, out IReadOnlyList<AlarmPointModel>? alarms)
    {
        var matchedAlarms = new List<AlarmPointModel>();
        double minWindowSeconds = GetRateMinWindowSeconds(zone.DiffTempAlarm);
        double maxWindowSeconds = GetRateMaxWindowSeconds(zone.DiffTempAlarm);
        foreach (ZonePoint point in points)
        {
            string trendKey = $"{channel}:{zone.ZoneNo}:{point.Index}";
            if (!_zoneTrendStates.TryGetValue(trendKey, out ZoneTrendState? trendState))
            {
                trendState = new ZoneTrendState();
                _zoneTrendStates[trendKey] = trendState;
            }
            trendState.Samples.AddLast(new TempSample(timestampMs, point.Temperature));
            while (trendState.Samples.First is not null &&
                   timestampMs - trendState.Samples.First.Value.TimestampMs > RateHistoryRetention.TotalMilliseconds)
            {
                trendState.Samples.RemoveFirst();
            }

            double sensorBestRiseRate = double.NegativeInfinity;
            TempSample? bestBaselineSample = null;
            double bestAgeSeconds = 0d;
            for (LinkedListNode<TempSample>? node = trendState.Samples.First; node is not null; node = node.Next)
            {
                double ageSeconds = (timestampMs - node.Value.TimestampMs) / 1000d;
                if (ageSeconds < minWindowSeconds)
                {
                    continue;
                }
                if (ageSeconds > maxWindowSeconds)
                {
                    continue;
                }

                double minutes = (timestampMs - node.Value.TimestampMs) / 60000d;
                if (minutes <= 0)
                {
                    continue;
                }

                double riseRate = (point.Temperature - node.Value.TemperatureC) / minutes;
                if (!double.IsFinite(riseRate) || riseRate <= sensorBestRiseRate)
                {
                    continue;
                }

                sensorBestRiseRate = riseRate;
                bestBaselineSample = node.Value;
                bestAgeSeconds = ageSeconds;
            }

            if (!double.IsFinite(sensorBestRiseRate) ||
                sensorBestRiseRate < zone.DiffTempAlarm)
            {
                trendState.RateCandidateCount = 0;
                continue;
            }

            if (ShouldSuppressSlowRiseRateAlarm(timestampMs, zone, point, trendState))
            {
                trendState.RateCandidateCount = 0;
                continue;
            }

            trendState.RateCandidateCount++;
            if (trendState.RateCandidateCount < RateAlarmConfirmSamples ||
                bestBaselineSample is null)
            {
                continue;
            }

            var metrics = new ZoneMetrics(
                zone.ZoneNo,
                point.Temperature,
                point.Position,
                point.Index);
            matchedAlarms.Add(CreateAlarm(
                metrics,
                zone,
                "rate_of_rise",
                "差温报警",
                point.Temperature,
                point.Position,
                point.Index,
                $"当前 {point.Temperature:F2}℃，基线 {bestBaselineSample.Value.TemperatureC:F2}℃，间隔 {bestAgeSeconds:F1}s，最大升温速率 {sensorBestRiseRate:F2}℃/min >= {zone.DiffTempAlarm:F2}℃/min",
                $"zone:{zone.ZoneNo}:rate_of_rise:sensor:{point.Index}"));
        }
        alarms = matchedAlarms;
        return matchedAlarms.Count > 0;
    }

    private static bool ShouldSuppressSlowRiseRateAlarm(long timestampMs, ZoneParameterItem zone, ZonePoint point, ZoneTrendState trendState)
    {
        const double slowRiseRateLimit = 2d;

        if (zone.AlarmLevel1 <= 0d)
        {
            return ShouldSuppressByFifteenMinuteRiseRate(timestampMs, point, trendState, slowRiseRateLimit);
        }

        if (zone.AlarmLevel1 <= 70d)
        {
            float? noActionTemperature = GetNoActionTemperature(zone.AlarmLevel1);
            if (noActionTemperature is not float noActionLimit || point.Temperature > noActionLimit)
            {
                return false;
            }

            if (trendState.Samples.First is not LinkedListNode<TempSample> firstNode)
            {
                return false;
            }

            double minutes = (timestampMs - firstNode.Value.TimestampMs) / 60000d;
            if (minutes <= 0)
            {
                return false;
            }

            double averageRiseRate = (point.Temperature - firstNode.Value.TemperatureC) / minutes;
            return double.IsFinite(averageRiseRate) && averageRiseRate <= slowRiseRateLimit;
        }

        return ShouldSuppressByFifteenMinuteRiseRate(timestampMs, point, trendState, slowRiseRateLimit);
    }

    private static bool ShouldSuppressByFifteenMinuteRiseRate(
        long timestampMs,
        ZonePoint point,
        ZoneTrendState trendState,
        double slowRiseRateLimit)
    {
        TempSample? fifteenMinuteBaseline = null;
        for (LinkedListNode<TempSample>? node = trendState.Samples.First; node is not null; node = node.Next)
        {
            double ageMinutes = (timestampMs - node.Value.TimestampMs) / 60000d;
            if (ageMinutes < 15d)
            {
                continue;
            }

            fifteenMinuteBaseline = node.Value;
            break;
        }

        if (fifteenMinuteBaseline is null)
        {
            return false;
        }

        double baselineMinutes = (timestampMs - fifteenMinuteBaseline.Value.TimestampMs) / 60000d;
        if (baselineMinutes <= 0)
        {
            return false;
        }

        double sustainedRiseRate = (point.Temperature - fifteenMinuteBaseline.Value.TemperatureC) / baselineMinutes;
        return double.IsFinite(sustainedRiseRate) && sustainedRiseRate <= slowRiseRateLimit;
    }

    private static double GetRateMinWindowSeconds(double threshold)
    {
        if (threshold >= 30d)
        {
            return 15d;
        }

        if (threshold >= 20d)
        {
            return 22.5d;
        }

        return 30d;
    }

    private static double GetRateMaxWindowSeconds(double threshold)
    {
        if (threshold >= 30d)
        {
            return 70d;
        }

        if (threshold >= 20d)
        {
            return 95d;
        }

        return 180d;
    }

    private static float? GetNoActionTemperature(double actionTemperature)
    {
        return actionTemperature switch
        {
            60d => 40f,
            70d => 45f,
            85d => 60f,
            105d => 75f,
            138d => 85f,
            180d => 108f,
            _ => null
        };
    }

    private static AlarmPointModel CreateAlarm(
        ZoneMetrics metrics,
        ZoneParameterItem zone,
        string typeCode,
        string typeName,
        float temperature,
        float position,
        int sensorIndex,
        string detailText,
        string sourceKey)
    {
        return new AlarmPointModel
        {
            PositionM = position,
            TemperatureC = temperature,
            SensorIndex = sensorIndex,
            ZoneNo = metrics.ZoneNo,
            TypeCode = typeCode,
            TypeText = typeName,
            DetailText = detailText,
            SourceKey = sourceKey
        };
    }

    private static SnapshotModel CloneSnapshotWithAlarms(SnapshotModel snapshot, AlarmPointModel[] alarms)
    {
        return new SnapshotModel
        {
            Timestamp = snapshot.Timestamp,
            TimestampMs = snapshot.TimestampMs,
            Channel = snapshot.Channel,
            PositionsM = snapshot.PositionsM,
            TemperaturesC = snapshot.TemperaturesC,
            SensorPositionsM = snapshot.SensorPositionsM,
            SensorTemperaturesC = snapshot.SensorTemperaturesC,
            SensorWavelengthsNm = snapshot.SensorWavelengthsNm,
            SpectrumXAxisNm = snapshot.SpectrumXAxisNm,
            SpectrumValues = snapshot.SpectrumValues,
            SpectrumSensorIndex = snapshot.SpectrumSensorIndex,
            SpectrumSensorPositionM = snapshot.SpectrumSensorPositionM,
            SpectrumSensorWavelengthNm = snapshot.SpectrumSensorWavelengthNm,
            SpectrumSensorTemperatureC = snapshot.SpectrumSensorTemperatureC,
            Alarms = alarms,
            MinTemp = snapshot.MinTemp,
            MaxTemp = snapshot.MaxTemp,
            AvgTemp = snapshot.AvgTemp,
            MaxPosM = snapshot.MaxPosM,
            StatusOk = snapshot.StatusOk
        };
    }

    private void EmitAlarmEvents(SnapshotModel snapshot)
    {
        var nextKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (AlarmPointModel alarm in snapshot.Alarms)
        {
            string key = BuildAlarmKey(snapshot.Channel, alarm);
            nextKeys.Add(key);

            bool isNew;
            lock (_alarmSync)
            {
                _activeAlarmKeys.Add(key);
                isNew = !_latchedAlarmKeys.Contains(key);
                if (isNew)
                {
                    _latchedAlarmKeys.Add(key);
                }
            }

            if (!isNew)
            {
                continue;
            }

            var record = new AlarmRecord
            {
                TimestampMs = snapshot.TimestampMs,
                Channel = snapshot.Channel,
                PositionM = alarm.PositionM,
                TemperatureC = alarm.TemperatureC,
                SensorIndex = alarm.SensorIndex,
                ZoneNo = alarm.ZoneNo,
                TypeCode = alarm.TypeCode,
                TypeText = alarm.TypeText,
                DetailText = alarm.DetailText,
                SourceKey = alarm.SourceKey
            };

            _store.EnqueueAlarm(record);
            AlarmRaised?.Invoke(record);
        }

        lock (_alarmSync)
        {
            foreach (string key in nextKeys)
            {
                _alarmRecoveryMissCounts.Remove(key);
            }

            foreach (string key in _activeAlarmKeys.ToArray())
            {
                if (nextKeys.Contains(key))
                {
                    continue;
                }

                int missCount = _alarmRecoveryMissCounts.TryGetValue(key, out int existingCount)
                    ? existingCount + 1
                    : 1;
                if (missCount >= AlarmRecoveryConfirmSamples)
                {
                    _activeAlarmKeys.Remove(key);
                    _alarmRecoveryMissCounts.Remove(key);
                    _suppressedAlarmKeys.Remove(key);
                    continue;
                }

                _alarmRecoveryMissCounts[key] = missCount;
            }
        }
    }

    private void StartSnapshotPolling()
    {
        if (!_disposed)
        {
            _pollTimer.Change(SnapshotPollInterval, SnapshotPollInterval);
        }
    }

    private void StopSnapshotPolling()
    {
        if (!_disposed)
        {
            _pollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private static string BuildAlarmKey(int channel, AlarmPointModel alarm)
    {
        return string.IsNullOrWhiteSpace(alarm.SourceKey)
            ? $"{channel}:{Math.Round(alarm.PositionM, 1).ToString(CultureInfo.InvariantCulture)}"
            : $"{channel}:{alarm.SourceKey}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopSnapshotPolling();
        _pollTimer.Dispose();
        _native.Dispose();
        _store.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static ZoneParameterItem CloneZone(ZoneParameterItem zone)
    {
        return new ZoneParameterItem
        {
            ZoneNo = zone.ZoneNo,
            Description = zone.Description,
            StartPos = zone.StartPos,
            EndPos = zone.EndPos,
            AlarmLevel1 = zone.AlarmLevel1,
            DiffTempAlarm = zone.DiffTempAlarm
        };
    }

    private readonly record struct ZonePoint(int Index, float Position, float Temperature);

    private readonly record struct ZoneMetrics(
        int ZoneNo,
        float MaxTemp,
        float MaxPos,
        int MaxIndex);

    private readonly record struct TempSample(long TimestampMs, float TemperatureC);

    private sealed class ZoneTrendState
    {
        public LinkedList<TempSample> Samples { get; } = new();
        public int RateCandidateCount { get; set; }
    }
}
