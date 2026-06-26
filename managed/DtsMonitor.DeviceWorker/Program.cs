using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DtsMonitor.App.Models;
using DtsMonitor.App.Services;

internal sealed class WorkerHost : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly string _deviceId;
    private readonly AppLogger _logger;
    private readonly MonitoringService _service;
    private readonly string _pidFilePath;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _snapshotPushTimer;
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private SnapshotModel? _pendingSnapshot;
    private SnapshotModel? _latestSnapshot;
    private CalibrationResultModel? _latestCalibrationResult;
    private CalibrationWaveDataModel? _latestCalibrationWaveData;
    private string _lastError = string.Empty;
    private int _alarmCount;

    public WorkerHost(string deviceId, string dbPath, string pidFilePath, AppLogger logger)
    {
        _deviceId = deviceId;
        _logger = logger;
        _service = new MonitoringService(dbPath);
        _pidFilePath = pidFilePath;
        _service.SnapshotUpdated += OnSnapshotUpdated;
        _service.AlarmRaised += OnAlarmRaised;
        _snapshotPushTimer = new Timer(_ => SafePushLatestSnapshot(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task RunAsync(string pipeName)
    {
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        WritePidFile();
        _logger.Info($"[{_deviceId}] waiting for pipe client: {pipeName}");
        await _pipe.WaitForConnectionAsync(_cts.Token);
        _logger.Info($"[{_deviceId}] pipe connected");

        _reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true
        };

        _snapshotPushTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        await PublishStatusChangedAsync();

        while (!_cts.IsCancellationRequested && _reader is not null)
        {
            string? line = await _reader.ReadLineAsync();
            if (line is null)
            {
                _logger.Info($"[{_deviceId}] pipe client disconnected");
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            DeviceIpcMessage? message = JsonSerializer.Deserialize<DeviceIpcMessage>(line, JsonOptions);
            if (message is null)
            {
                continue;
            }

            await HandleMessageAsync(message);
        }

        _cts.Cancel();
    }

    private async Task HandleMessageAsync(DeviceIpcMessage message)
    {
        await _commandGate.WaitAsync(_cts.Token);
        try
        {
            switch (message.MessageType)
            {
                case "Ping":
                    await SendResponseAsync(message, new CommandResultPayload<EmptyPayload> { Success = true, Payload = new EmptyPayload() });
                    break;
                case "GetStatus":
                    await SendResponseAsync(message, new CommandResultPayload<StatusResponsePayload>
                    {
                        Success = true,
                        Payload = new StatusResponsePayload { Status = BuildStatus() }
                    });
                    break;
                case "Connect":
                    {
                        ConnectCommandPayload payload = DeserializePayload<ConnectCommandPayload>(message);
                        int rc = _service.Connect(payload.Ip, payload.Use1GInit);
                        if (rc != 0)
                        {
                            _lastError = _service.GetLastError();
                        }

                        await SendCodeResponseAsync(message, rc);
                        await PublishStatusChangedAsync();
                        break;
                    }
                case "Disconnect":
                    {
                        int rc = _service.Disconnect();
                        if (rc != 0)
                        {
                            _lastError = _service.GetLastError();
                        }

                        await SendCodeResponseAsync(message, rc);
                        await PublishStatusChangedAsync();
                        break;
                    }
                case "ApplyConfig":
                    {
                        HardwareConfig payload = DeserializePayload<HardwareConfig>(message);
                        int rc = _service.ApplyConfig(payload);
                        if (rc != 0)
                        {
                            _lastError = _service.GetLastError();
                        }

                        await SendCodeResponseAsync(message, rc);
                        await PublishStatusChangedAsync();
                        break;
                    }
                case "UpdateAlarmSettings":
                    {
                        AlarmSettingsModel payload = DeserializePayload<AlarmSettingsModel>(message);
                        _service.UpdateAlarmSettings(payload);
                        await SendCodeResponseAsync(message, 0);
                        break;
                    }
                case "StartRun":
                    {
                        RunCommandPayload payload = DeserializePayload<RunCommandPayload>(message);
                        int rc = _service.StartAcquisition(payload.Channel, payload.AllChannelsLowSpeed);
                        if (rc != 0)
                        {
                            _lastError = _service.GetLastError();
                        }

                        await SendCodeResponseAsync(message, rc);
                        await PublishStatusChangedAsync();
                        break;
                    }
                case "StopRun":
                    {
                        ChannelCommandPayload payload = DeserializePayload<ChannelCommandPayload>(message);
                        int rc = _service.StopAcquisition(payload.Channel);
                        if (rc != 0)
                        {
                            _lastError = _service.GetLastError();
                        }

                        await SendCodeResponseAsync(message, rc);
                        await PublishStatusChangedAsync();
                        break;
                    }
                case "StartCalibration":
                    {
                        CalibrationCommandPayload payload = DeserializePayload<CalibrationCommandPayload>(message);
                        _latestCalibrationResult = null;
                        _latestCalibrationWaveData = null;
                        int rc = _service.StartCalibration(payload.Channel, payload.Threshold);
                        if (rc != 0)
                        {
                            _lastError = _service.GetLastError();
                        }

                        await SendCodeResponseAsync(message, rc);
                        await PublishStatusChangedAsync();
                        break;
                    }
                case "StopCalibration":
                    {
                        ChannelCommandPayload payload = DeserializePayload<ChannelCommandPayload>(message);
                        int rc = _service.StopCalibration(payload.Channel);
                        if (rc == 0)
                        {
                            _latestCalibrationResult = _service.TryReadLatestCalibrationResult();
                            if (_latestCalibrationResult is not null)
                            {
                                await PublishEventAsync("CalibrationUpdated", _latestCalibrationResult);
                            }
                        }
                        else
                        {
                            _lastError = _service.GetLastError();
                        }

                        await SendCodeResponseAsync(message, rc);
                        await PublishStatusChangedAsync();
                        break;
                    }
                case "ResetCurrentAlarms":
                    _service.ResetCurrentAlarms();
                    _alarmCount = 0;
                    await SendCodeResponseAsync(message, 0);
                    await PublishStatusChangedAsync();
                    break;
                case "SetSpectrumSensorIndex":
                    {
                        SpectrumSensorCommandPayload payload = DeserializePayload<SpectrumSensorCommandPayload>(message);
                        int rc = _service.SetSpectrumSensorIndex(payload.SensorIndex);
                        await SendCodeResponseAsync(message, rc);
                        break;
                    }
                case "QueryLatestSnapshot":
                    await SendResponseAsync(message, new CommandResultPayload<SnapshotModel>
                    {
                        Success = true,
                        Payload = _service.TryReadLatestSnapshotNow()
                    });
                    break;
                case "QueryLatestCalibrationResult":
                    await SendResponseAsync(message, new CommandResultPayload<CalibrationResultModel>
                    {
                        Success = true,
                        Payload = _latestCalibrationResult ?? _service.TryReadLatestCalibrationResult()
                    });
                    break;
                case "QueryLatestCalibrationWaveData":
                    await SendResponseAsync(message, new CommandResultPayload<CalibrationWaveDataModel>
                    {
                        Success = true,
                        Payload = _latestCalibrationWaveData ?? _service.TryReadLatestCalibrationWaveData()
                    });
                    break;
                case "SetAmplifierCurrents":
                    {
                        AmplifierCommandPayload payload = DeserializePayload<AmplifierCommandPayload>(message);
                        int rc = _service.SetAmplifierCurrents(payload.EdfaCurrentMa, payload.EdfaPaCurrentMa);
                        await SendCodeResponseAsync(message, rc);
                        break;
                    }
                case "GetAmplifierCurrent":
                    await SendResponseAsync(message, new CommandResultPayload<int>
                    {
                        Success = true,
                        Payload = _service.GetAmplifierCurrent()
                    });
                    break;
                case "RecalculateCalibrationPositions":
                    {
                        ThresholdCommandPayload payload = DeserializePayload<ThresholdCommandPayload>(message);
                        int rc = _service.RecalculateCalibrationPositions(payload.Threshold);
                        await SendCodeResponseAsync(message, rc);
                        break;
                    }
                case "Shutdown":
                    await SendCodeResponseAsync(message, 0);
                    _cts.Cancel();
                    break;
                default:
                    await SendResponseAsync(message, new CommandResultPayload<EmptyPayload>
                    {
                        Success = false,
                        ErrorCode = -1,
                        ErrorMessage = $"未知命令: {message.MessageType}"
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.Error($"[{_deviceId}] command {message.MessageType} failed: {ex}");
            await SendResponseAsync(message, new CommandResultPayload<EmptyPayload>
            {
                Success = false,
                ErrorCode = -1,
                ErrorMessage = ex.Message
            });
            await PublishEventAsync("ErrorOccurred", ex.Message);
            await PublishStatusChangedAsync();
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private void OnSnapshotUpdated(SnapshotModel snapshot)
    {
        _latestSnapshot = snapshot;
        _pendingSnapshot = snapshot;
    }

    private void OnAlarmRaised(AlarmRecord alarm)
    {
        _alarmCount++;
        _ = PublishEventAsync("AlarmRaised", alarm);
        _ = PublishStatusChangedAsync();
    }

    private void PushLatestSnapshot()
    {
        SnapshotModel? snapshot = Interlocked.Exchange(ref _pendingSnapshot, null);
        if (snapshot is null)
        {
            return;
        }

        _ = PublishEventAsync("SnapshotUpdated", snapshot);
        _ = PublishStatusChangedAsync();
    }

    private void SafePushLatestSnapshot()
    {
        try
        {
            PushLatestSnapshot();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.Error($"[{_deviceId}] snapshot publish failed: {ex}");
        }
    }

    private DeviceStatusSnapshot BuildStatus()
    {
        int nativeState = _service.GetState();
        int connectState = nativeState == 0 ? 0 : _service.GetConnect();
        DeviceState deviceState = nativeState switch
        {
            4 => DeviceState.Running,
            5 => DeviceState.Calibrating,
            _ when connectState == 1 => DeviceState.Connected,
            _ => DeviceState.Disconnected
        };

        if (!string.IsNullOrWhiteSpace(_lastError) && nativeState == 0 && connectState != 1)
        {
            deviceState = DeviceState.Error;
        }

        return new DeviceStatusSnapshot
        {
            WorkerState = WorkerState.Online,
            DeviceState = deviceState,
            NativeState = nativeState,
            ConnectState = connectState,
            CurrentAlarmCount = _alarmCount,
            LastSnapshotTimestampMs = _latestSnapshot?.TimestampMs,
            LastErrorMessage = _lastError
        };
    }

    private T DeserializePayload<T>(DeviceIpcMessage message)
    {
        if (message.Payload is null)
        {
            return Activator.CreateInstance<T>();
        }

        return message.Payload.Value.Deserialize<T>(JsonOptions) ?? Activator.CreateInstance<T>();
    }

    private Task SendCodeResponseAsync(DeviceIpcMessage request, int rc)
        => SendResponseAsync(request, new CommandResultPayload<EmptyPayload>
        {
            Success = rc == 0,
            ErrorCode = rc,
            ErrorMessage = rc == 0 ? string.Empty : (_service.GetLastError() ?? _lastError),
            Payload = new EmptyPayload()
        });

    private Task PublishStatusChangedAsync()
    {
        try
        {
            return PublishEventAsync("StatusChanged", BuildStatus());
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.Error($"[{_deviceId}] status publish failed: {ex}");
            throw;
        }
    }

    private Task PublishEventAsync<T>(string messageType, T payload)
        => WriteMessageAsync(new DeviceIpcMessage
        {
            MessageType = messageType,
            DeviceId = _deviceId,
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = JsonSerializer.SerializeToElement(payload, JsonOptions)
        });

    private Task SendResponseAsync<T>(DeviceIpcMessage request, T payload)
        => WriteMessageAsync(new DeviceIpcMessage
        {
            MessageType = request.MessageType,
            RequestId = request.RequestId,
            DeviceId = _deviceId,
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = JsonSerializer.SerializeToElement(payload, JsonOptions)
        });

    private async Task WriteMessageAsync(DeviceIpcMessage message)
    {
        if (_writer is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(message, JsonOptions);
        await _writeGate.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(json);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        _snapshotPushTimer.Dispose();
        _service.SnapshotUpdated -= OnSnapshotUpdated;
        _service.AlarmRaised -= OnAlarmRaised;
        _service.Dispose();
        _pipe?.Dispose();
        TryDeletePidFile();
        _commandGate.Dispose();
        _writeGate.Dispose();
        _cts.Dispose();
    }

    private void WritePidFile()
    {
        string? dir = Path.GetDirectoryName(_pidFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_pidFilePath, Environment.ProcessId.ToString());
    }

    private void TryDeletePidFile()
    {
        try
        {
            if (File.Exists(_pidFilePath))
            {
                File.Delete(_pidFilePath);
            }
        }
        catch
        {
        }
    }
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Dictionary<string, string> parsed = ParseArgs(args);
        string deviceId = parsed.TryGetValue("device-id", out string? did) ? did : "default";
        string pipeName = parsed.TryGetValue("pipe-name", out string? pname) ? pname : "hg_fbg_worker_default";
        string dbPath = parsed.TryGetValue("db-path", out string? db) ? db : Path.Combine(AppContext.BaseDirectory, "hg_fbg_monitor_default.db");
        string logPath = parsed.TryGetValue("log-path", out string? lp) ? lp : Path.Combine(AppContext.BaseDirectory, $"worker_{deviceId}.log");
        string pidFilePath = parsed.TryGetValue("pid-file", out string? pp) ? pp : Path.Combine(AppContext.BaseDirectory, $"worker_{deviceId}.pid");

        var logger = new AppLogger(logPath);
        try
        {
            using var host = new WorkerHost(deviceId, dbPath, pidFilePath, logger);
            await host.RunAsync(pipeName);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error($"fatal: {ex}");
            return 1;
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                continue;
            }

            result[arg[2..]] = args[++i];
        }

        return result;
    }
}
