using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DtsMonitor.App.Models;

namespace DtsMonitor.App.Services;

public sealed class DeviceSessionProxy : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private static readonly TimeSpan ExistingWorkerAttachTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WorkerStartupConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly DeviceDefinition _device;
    private readonly AppLogger _logger;
    private readonly SnapshotHistoryStore _store;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DeviceIpcMessage>> _pendingRequests = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stateSync = new();
    private readonly Timer _heartbeatTimer;
    private Process? _workerProcess;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _readLoopTask;
    private int _nativeState;
    private int _connectState;
    private string _lastError = string.Empty;
    private int _missedHeartbeats;
    private DeviceState _deviceState;
    private WorkerState _workerState = WorkerState.Offline;
    private bool _isStateChangingCommandInFlight;
    private int _currentAlarmCount;

    public DeviceSessionProxy(DeviceDefinition device, AppLogger logger)
    {
        _device = device;
        _logger = logger;
        _store = new SnapshotHistoryStore(device.DbPath);
        ViewState = new DeviceViewState
        {
            DeviceId = device.DeviceId,
            Name = device.Name,
            Ip = device.Ip
        };

        _heartbeatTimer = new Timer(_ => HeartbeatTick(), null, Timeout.Infinite, Timeout.Infinite);
        UpdateViewStateFlags();
    }

    public event Action<SnapshotModel>? SnapshotUpdated;
    public event Action<AlarmRecord>? AlarmRaised;

    public DeviceDefinition Device => _device;
    public DeviceViewState ViewState { get; }
    public SnapshotHistoryStore Store => _store;
    public bool HasStateChangingCommandInFlight => _isStateChangingCommandInFlight;

    public async Task StartAsync()
    {
        if (_pipe is not null && _pipe.IsConnected && !IsWorkerUnavailable())
        {
            return;
        }

        try
        {
            _reader?.Dispose();
            _writer?.Dispose();
            _pipe?.Dispose();
        }
        catch
        {
        }

        _reader = null;
        _writer = null;
        _pipe = null;

        SetWorkerState(WorkerState.Starting);

        string appBase = AppContext.BaseDirectory;
        string workerExePath = Path.Combine(appBase, "HG-FBG.DeviceWorker.exe");
        if (!File.Exists(workerExePath))
        {
            throw new FileNotFoundException($"Worker executable not found: {workerExePath}");
        }

        string logPath = Path.Combine(Path.GetDirectoryName(_device.UiStatePath) ?? appBase, $"worker_{_device.DeviceId}.log");
        string pidFilePath = Path.Combine(Path.GetDirectoryName(_device.UiStatePath) ?? appBase, $"worker_{_device.DeviceId}.pid");

        if (await TryAttachToExistingWorkerAsync(ExistingWorkerAttachTimeout).ConfigureAwait(false))
        {
            _logger.Info($"[{_device.DeviceId}] attached to existing worker pipe={_device.WorkerPipeName}");
            return;
        }

        bool recoveredStaleWorker = TryRecoverStaleWorker(workerExePath, pidFilePath);
        if (recoveredStaleWorker &&
            await TryAttachToExistingWorkerAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false))
        {
            _logger.Info($"[{_device.DeviceId}] attached to recovered worker pipe={_device.WorkerPipeName}");
            return;
        }

        Exception? startupError = null;
        try
        {
            StartWorkerProcess(workerExePath, logPath, pidFilePath);
            await ConnectToWorkerPipeAsync(WorkerStartupConnectTimeout).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            startupError = ex;
        }

        if (await TryAttachToExistingWorkerAsync(ExistingWorkerAttachTimeout).ConfigureAwait(false))
        {
            _logger.Info($"[{_device.DeviceId}] attached to existing worker after startup retry pipe={_device.WorkerPipeName}");
            return;
        }

        if (TryRecoverStaleWorker(workerExePath, pidFilePath))
        {
            StartWorkerProcess(workerExePath, logPath, pidFilePath);
            await ConnectToWorkerPipeAsync(WorkerStartupConnectTimeout).ConfigureAwait(false);
            return;
        }

        throw BuildWorkerStartupException(startupError, logPath);
    }

    private string GetWorkerExecutablePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "HG-FBG.DeviceWorker.exe");
    }

    private string GetWorkerPidFilePath()
    {
        string root = Path.GetDirectoryName(_device.UiStatePath) ?? AppContext.BaseDirectory;
        return Path.Combine(root, $"worker_{_device.DeviceId}.pid");
    }

    private async Task<bool> TryAttachToExistingWorkerAsync(TimeSpan timeout)
    {
        NamedPipeClientStream? existingPipe = null;
        try
        {
            existingPipe = new NamedPipeClientStream(".", _device.WorkerPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeoutCts = new CancellationTokenSource(timeout);
            await existingPipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            _pipe = existingPipe;
            await AttachConnectedPipeAsync().ConfigureAwait(false);
            return true;
        }
        catch
        {
            existingPipe?.Dispose();
            return false;
        }
    }

    private void StartWorkerProcess(string workerExePath, string logPath, string pidFilePath)
    {
        _workerProcess = Process.Start(new ProcessStartInfo
        {
            FileName = workerExePath,
            Arguments = $"--device-id \"{_device.DeviceId}\" --pipe-name \"{_device.WorkerPipeName}\" --db-path \"{_device.DbPath}\" --log-path \"{logPath}\" --pid-file \"{pidFilePath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(workerExePath) ?? AppContext.BaseDirectory
        });

        if (_workerProcess is null)
        {
            throw new InvalidOperationException("Failed to start device worker.");
        }

        _workerProcess.EnableRaisingEvents = true;
        _workerProcess.Exited += (_, _) =>
        {
            int exitCode;
            try
            {
                exitCode = _workerProcess.ExitCode;
            }
            catch
            {
                exitCode = -1;
            }

            _logger.Error($"[{_device.DeviceId}] worker exited unexpectedly pid={_workerProcess.Id}, exitCode={exitCode}");
            MarkWorkerDisconnected($"Worker exited, exitCode={exitCode}");
        };
        _logger.Info($"[{_device.DeviceId}] worker started pid={_workerProcess.Id}");
    }

    private async Task ConnectToWorkerPipeAsync(TimeSpan timeout)
    {
        _pipe?.Dispose();
        _pipe = new NamedPipeClientStream(".", _device.WorkerPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            await _pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            _pipe.Dispose();
            _pipe = null;
            throw new TimeoutException($"Timed out waiting for worker pipe '{_device.WorkerPipeName}'.", ex);
        }

        await AttachConnectedPipeAsync().ConfigureAwait(false);
    }

    private bool TryRecoverStaleWorker(string workerExePath, string pidFilePath)
    {
        if (TryTerminateWorkerByPidFile(pidFilePath, workerExePath, out int terminatedPid))
        {
            _logger.Info($"[{_device.DeviceId}] terminated stale worker pid={terminatedPid}");
            TryDeleteFile(pidFilePath);
            return true;
        }

        if (TryTerminateSingleCandidateWorker(workerExePath, out terminatedPid))
        {
            _logger.Info($"[{_device.DeviceId}] terminated fallback worker pid={terminatedPid}");
            return true;
        }

        return false;
    }

    private static bool TryTerminateWorkerByPidFile(string pidFilePath, string workerExePath, out int terminatedPid)
    {
        terminatedPid = 0;
        try
        {
            if (!File.Exists(pidFilePath))
            {
                return false;
            }

            string text = File.ReadAllText(pidFilePath).Trim();
            if (!int.TryParse(text, out int pid) || pid <= 0)
            {
                return false;
            }

            using Process process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            if (!IsProcessPathMatch(process, workerExePath))
            {
                return false;
            }

            process.Kill(entireProcessTree: false);
            process.WaitForExit(3000);
            terminatedPid = pid;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryTerminateSingleCandidateWorker(string workerExePath, out int terminatedPid)
    {
        terminatedPid = 0;
        try
        {
            Process[] candidates = Process.GetProcessesByName("HG-FBG.DeviceWorker")
                .Where(candidate => !candidate.HasExited && IsProcessPathMatch(candidate, workerExePath))
                .ToArray();
            if (candidates.Length != 1)
            {
                foreach (Process candidate in candidates)
                {
                    candidate.Dispose();
                }

                return false;
            }

            using Process process = candidates[0];
            terminatedPid = process.Id;
            process.Kill(entireProcessTree: false);
            process.WaitForExit(3000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProcessPathMatch(Process process, string workerExePath)
    {
        try
        {
            string? processPath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(processPath) &&
                   string.Equals(Path.GetFullPath(processPath), Path.GetFullPath(workerExePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private Exception BuildWorkerStartupException(Exception? startupError, string logPath)
    {
        string message = startupError switch
        {
            TimeoutException => $"设备 worker 启动超时，可能有残留 worker 占用通信管道：{_device.WorkerPipeName}",
            IOException => $"设备 worker 启动失败，通信管道不可用：{_device.WorkerPipeName}",
            _ => "设备 worker 启动失败。"
        };

        string diagnostic = TryReadWorkerLogTail(logPath);
        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            message = $"{message}{Environment.NewLine}{Environment.NewLine}{diagnostic}";
        }

        return new InvalidOperationException(message, startupError);
    }

    private static string TryReadWorkerLogTail(string logPath)
    {
        try
        {
            if (!File.Exists(logPath))
            {
                return string.Empty;
            }

            string[] lines = File.ReadAllLines(logPath);
            if (lines.Length == 0)
            {
                return string.Empty;
            }

            IEnumerable<string> tail = lines.Reverse().Take(3).Reverse();
            return string.Join(Environment.NewLine, tail);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private async Task AttachConnectedPipeAsync()
    {
        if (_pipe is null)
        {
            return;
        }

        _reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true
        };

        _readLoopTask = Task.Run(ReadLoopAsync, _cts.Token);
        _missedHeartbeats = 0;
        SetWorkerState(WorkerState.Online);
        _heartbeatTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        await RefreshStatusAsync().ConfigureAwait(false);
    }

    public int Connect(string ip, bool use1GInit = true)
        => SendCommandExpectCode("Connect", new ConnectCommandPayload { Ip = ip, Use1GInit = use1GInit }, TimeSpan.FromSeconds(5));

    public int Disconnect()
        => SendCommandExpectCode("Disconnect", new EmptyPayload(), TimeSpan.FromSeconds(5));

    public int GetConnect() => _connectState;
    public int GetState() => _nativeState;
    public string GetLastError() => _lastError;

    public int ApplyConfig(HardwareConfig config)
        => SendCommandExpectCode("ApplyConfig", config, TimeSpan.FromSeconds(5));

    public int StartCalibration(int channel, float threshold)
        => SendCommandExpectCode("StartCalibration", new CalibrationCommandPayload { Channel = channel, Threshold = threshold }, TimeSpan.FromSeconds(30));

    public int StopCalibration(int channel)
        => SendCommandExpectCode("StopCalibration", new ChannelCommandPayload { Channel = channel }, TimeSpan.FromSeconds(10));

    public CalibrationResultModel? TryReadLatestCalibrationResult()
        => SendCommandExpectPayload<CalibrationResultModel>("QueryLatestCalibrationResult", new EmptyPayload(), TimeSpan.FromSeconds(5));

    public CalibrationWaveDataModel? TryReadLatestCalibrationWaveData()
        => SendCommandExpectPayload<CalibrationWaveDataModel>("QueryLatestCalibrationWaveData", new EmptyPayload(), TimeSpan.FromSeconds(5));

    public int SetAmplifierCurrents(int edfaCurrentMa, int edfaPaCurrentMa)
        => SendCommandExpectCode("SetAmplifierCurrents", new AmplifierCommandPayload { EdfaCurrentMa = edfaCurrentMa, EdfaPaCurrentMa = edfaPaCurrentMa }, TimeSpan.FromSeconds(5));

    public int GetAmplifierCurrent()
        => SendCommandExpectPayload<int>("GetAmplifierCurrent", new EmptyPayload(), TimeSpan.FromSeconds(5));

    public int RecalculateCalibrationPositions(float threshold)
        => SendCommandExpectCode("RecalculateCalibrationPositions", new ThresholdCommandPayload { Threshold = threshold }, TimeSpan.FromSeconds(10));

    public int StartAcquisition(int channel, bool allChannelsLowSpeed)
        => SendCommandExpectCode("StartRun", new RunCommandPayload { Channel = channel, AllChannelsLowSpeed = allChannelsLowSpeed }, TimeSpan.FromSeconds(10));

    public int StopAcquisition(int channel)
        => SendCommandExpectCode("StopRun", new ChannelCommandPayload { Channel = channel }, TimeSpan.FromSeconds(10));

    public int SetSpectrumSensorIndex(int sensorIndex)
        => SendCommandExpectCode("SetSpectrumSensorIndex", new SpectrumSensorCommandPayload { SensorIndex = sensorIndex }, TimeSpan.FromSeconds(5));

    public SnapshotModel? TryReadLatestSnapshotNow()
        => SendCommandExpectPayload<SnapshotModel>("QueryLatestSnapshot", new EmptyPayload(), TimeSpan.FromSeconds(5));

    public void UpdateAlarmSettings(AlarmSettingsModel settings)
        => SendCommandExpectCode("UpdateAlarmSettings", settings, TimeSpan.FromSeconds(5));

    public void ResetCurrentAlarms()
        => SendCommandExpectCode("ResetCurrentAlarms", new EmptyPayload(), TimeSpan.FromSeconds(5));

    public async Task RefreshStatusAsync()
    {
        if (IsWorkerUnavailable())
        {
            await StartAsync().ConfigureAwait(false);
        }

        StatusResponsePayload? payload = await SendCommandExpectPayloadAsync<StatusResponsePayload>("GetStatus", new EmptyPayload(), TimeSpan.FromSeconds(5));
        if (payload is not null)
        {
            ApplyStatus(payload.Status);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested && _reader is not null)
            {
                string? line = await _reader.ReadLineAsync();
                if (line is null)
                {
                    _logger.Error($"[{_device.DeviceId}] worker pipe disconnected.");
                    MarkWorkerDisconnected("Worker pipe disconnected.");
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

                _missedHeartbeats = 0;

                if (!string.IsNullOrWhiteSpace(message.RequestId) &&
                    _pendingRequests.TryRemove(message.RequestId, out TaskCompletionSource<DeviceIpcMessage>? completion))
                {
                    completion.TrySetResult(message);
                    continue;
                }

                HandleEventMessage(message);
            }
        }
        catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error($"[{_device.DeviceId}] pipe read loop failed: {ex}");
            _lastError = ex.Message;
            ViewState.LastErrorMessage = _lastError;
            SetWorkerState(WorkerState.Faulted);
        }
    }

    private void HandleEventMessage(DeviceIpcMessage message)
    {
        switch (message.MessageType)
        {
            case "StatusChanged":
                if (TryDeserializePayload<DeviceStatusSnapshot>(message, out DeviceStatusSnapshot? status) && status is not null)
                {
                    ApplyStatus(status);
                }
                break;
            case "SnapshotUpdated":
                if (TryDeserializePayload<SnapshotModel>(message, out SnapshotModel? snapshot) && snapshot is not null)
                {
                    ViewState.LastSnapshotTime = snapshot.Timestamp;
                    SnapshotUpdated?.Invoke(snapshot);
                }
                break;
            case "AlarmRaised":
                if (TryDeserializePayload<AlarmRecord>(message, out AlarmRecord? alarm) && alarm is not null)
                {
                    _currentAlarmCount++;
                    ViewState.CurrentAlarmCount = _currentAlarmCount;
                    AlarmRaised?.Invoke(alarm);
                }
                break;
            case "CalibrationUpdated":
                if (TryDeserializePayload<CalibrationResultModel>(message, out CalibrationResultModel? calibration) && calibration is not null)
                {
                }
                break;
            case "ErrorOccurred":
                if (TryDeserializePayload<string>(message, out string? error) && !string.IsNullOrWhiteSpace(error))
                {
                    _lastError = error;
                    ViewState.LastErrorMessage = error;
                    SetDeviceState(DeviceState.Error);
                }
                break;
        }
    }

    private void ApplyStatus(DeviceStatusSnapshot status)
    {
        lock (_stateSync)
        {
            _nativeState = status.NativeState;
            _connectState = status.ConnectState;
            _lastError = status.LastErrorMessage ?? string.Empty;
            _currentAlarmCount = status.CurrentAlarmCount;
            _deviceState = status.DeviceState;
            _workerState = status.WorkerState;
        }

        ViewState.WorkerState = status.WorkerState;
        ViewState.DeviceState = status.DeviceState;
        ViewState.CurrentAlarmCount = status.CurrentAlarmCount;
        ViewState.LastErrorMessage = status.LastErrorMessage ?? string.Empty;
        ViewState.LastSnapshotTime = status.LastSnapshotTimestampMs.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(status.LastSnapshotTimestampMs.Value).LocalDateTime
            : null;
        UpdateViewStateFlags();
    }

    private void SetWorkerState(WorkerState state)
    {
        _workerState = state;
        if (state is WorkerState.Offline or WorkerState.Faulted)
        {
            _nativeState = 0;
            _connectState = 0;
            _deviceState = DeviceState.Disconnected;
            ViewState.DeviceState = DeviceState.Disconnected;
        }
        ViewState.WorkerState = state;
        UpdateViewStateFlags();
    }

    private void SetDeviceState(DeviceState state)
    {
        _deviceState = state;
        ViewState.DeviceState = state;
        UpdateViewStateFlags();
    }

    private void HeartbeatTick()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        if (_isStateChangingCommandInFlight)
        {
            _missedHeartbeats = 0;
            return;
        }

        if (_pipe is null || !_pipe.IsConnected)
        {
            SetWorkerState(WorkerState.Offline);
            return;
        }

        if (IsWorkerUnavailable())
        {
            MarkWorkerDisconnected("Worker process is not running.");
            return;
        }

        try
        {
            bool pong = SendCommandExpectPayload<EmptyPayload>("Ping", new EmptyPayload(), TimeSpan.FromSeconds(2), throwOnFailure: false) is not null;
            if (pong)
            {
                _missedHeartbeats = 0;
                return;
            }
        }
        catch
        {
        }

        _missedHeartbeats++;
        if (_missedHeartbeats >= 3)
        {
            SetWorkerState(WorkerState.Faulted);
        }
    }

    private void SetStateChangingCommandBusy(bool isBusy)
    {
        _isStateChangingCommandInFlight = isBusy;
        UpdateViewStateFlags();
    }

    private bool IsWorkerUnavailable()
    {
        if (_pipe is null || !_pipe.IsConnected)
        {
            return true;
        }

        try
        {
            return _workerProcess is not null && _workerProcess.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private void MarkWorkerDisconnected(string reason)
    {
        _lastError = reason;
        ViewState.LastErrorMessage = reason;
        SetWorkerState(WorkerState.Offline);

        try
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _pipe?.Dispose();
        }
        catch
        {
        }

        _writer = null;
        _reader = null;
        _pipe = null;
    }

    private void UpdateViewStateFlags()
    {
        bool workerOnline = _workerState == WorkerState.Online;
        bool connected = _connectState == 1 || _deviceState is DeviceState.Connected or DeviceState.Running or DeviceState.Calibrating;
        bool running = _nativeState == 4 || _deviceState == DeviceState.Running;
        bool calibrating = _nativeState == 5 || _deviceState == DeviceState.Calibrating;
        bool transitioning = _deviceState == DeviceState.Connecting;
        bool busy = _isStateChangingCommandInFlight || transitioning || running || calibrating;

        ViewState.IsBusy = busy;
        ViewState.CanConnect = !connected && !_isStateChangingCommandInFlight;
        ViewState.CanStart = workerOnline && connected && !running && !calibrating && !_isStateChangingCommandInFlight;
        ViewState.CanStop = workerOnline && connected && running && !_isStateChangingCommandInFlight;
    }

    private static bool IsStateChangingCommand(string messageType)
    {
        return messageType is "Connect"
            or "Disconnect"
            or "ApplyConfig"
            or "UpdateAlarmSettings"
            or "StartRun"
            or "StopRun"
            or "StartCalibration"
            or "StopCalibration"
            or "ResetCurrentAlarms"
            or "SetSpectrumSensorIndex"
            or "SetAmplifierCurrents"
            or "RecalculateCalibrationPositions";
    }

    private void TryRefreshStatusSync()
    {
        CommandResultPayload<StatusResponsePayload>? result =
            SendCommandExpectEnvelope<EmptyPayload, StatusResponsePayload>("GetStatus", new EmptyPayload(), TimeSpan.FromSeconds(5));
        if (result?.Success == true && result.Payload is not null)
        {
            ApplyStatus(result.Payload.Status);
        }
    }

    private int SendCommandExpectCode<TPayload>(string messageType, TPayload payload, TimeSpan timeout)
    {
        CommandResultPayload<EmptyPayload>? result = SendCommandExpectEnvelope<TPayload, EmptyPayload>(messageType, payload, timeout);
        if (result is null)
        {
            return -1;
        }

        if (!result.Success)
        {
            _lastError = result.ErrorMessage;
            ViewState.LastErrorMessage = result.ErrorMessage;
            return result.ErrorCode == 0 ? -1 : result.ErrorCode;
        }

        return 0;
    }

    private TPayload? SendCommandExpectPayload<TPayload>(string messageType, object payload, TimeSpan timeout, bool throwOnFailure = true)
    {
        CommandResultPayload<TPayload>? result = SendCommandExpectEnvelope<object, TPayload>(messageType, payload, timeout);
        if (result is null)
        {
            return default;
        }

        if (!result.Success)
        {
            _lastError = result.ErrorMessage;
            ViewState.LastErrorMessage = result.ErrorMessage;
            return default;
        }

        return result.Payload;
    }

    private Task<TPayload?> SendCommandExpectPayloadAsync<TPayload>(string messageType, object payload, TimeSpan timeout)
        => Task.FromResult(SendCommandExpectPayload<TPayload>(messageType, payload, timeout));

    private CommandResultPayload<TResponse>? SendCommandExpectEnvelope<TRequest, TResponse>(string messageType, TRequest payload, TimeSpan timeout)
    {
        if (_pipe is null || !_pipe.IsConnected || _writer is null)
        {
            return new CommandResultPayload<TResponse>
            {
                Success = false,
                ErrorCode = -1,
                ErrorMessage = "Worker not connected."
            };
        }

        string requestId = Guid.NewGuid().ToString("N");
        var message = new DeviceIpcMessage
        {
            MessageType = messageType,
            RequestId = requestId,
            DeviceId = _device.DeviceId,
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = JsonSerializer.SerializeToElement(payload, JsonOptions)
        };

        var tcs = new TaskCompletionSource<DeviceIpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = tcs;

        bool stateChangingCommand = IsStateChangingCommand(messageType);
        if (stateChangingCommand)
        {
            SetStateChangingCommandBusy(true);
        }

        try
        {
            string json = JsonSerializer.Serialize(message, JsonOptions);

            _writeGate.Wait();
            try
            {
                _writer.WriteLine(json);
            }
            finally
            {
                _writeGate.Release();
            }

            if (!tcs.Task.Wait(timeout))
            {
                _pendingRequests.TryRemove(requestId, out _);
                return new CommandResultPayload<TResponse>
                {
                    Success = false,
                    ErrorCode = -1,
                    ErrorMessage = $"{messageType} timeout."
                };
            }

            DeviceIpcMessage response = tcs.Task.Result;
            if (!TryDeserializePayload<CommandResultPayload<TResponse>>(response, out CommandResultPayload<TResponse>? result) || result is null)
            {
                return new CommandResultPayload<TResponse>
                {
                    Success = false,
                    ErrorCode = -1,
                    ErrorMessage = $"{messageType} invalid response."
                };
            }

            if (messageType == "Connect" && result.Success)
            {
                _device.LastConnectedUtc = DateTime.UtcNow;
            }

            if (stateChangingCommand && result.Success)
            {
                ApplyOptimisticCommandState(messageType);
                if (messageType == "ResetCurrentAlarms")
                {
                    _currentAlarmCount = 0;
                    ViewState.CurrentAlarmCount = 0;
                }

                TryRefreshStatusSync();
            }

            return result;
        }
        finally
        {
            if (stateChangingCommand)
            {
                SetStateChangingCommandBusy(false);
            }
        }
    }

    private void ApplyOptimisticCommandState(string messageType)
    {
        lock (_stateSync)
        {
            _workerState = WorkerState.Online;
            ViewState.WorkerState = WorkerState.Online;

            switch (messageType)
            {
                case "Connect":
                    _connectState = 1;
                    _nativeState = Math.Max(_nativeState, 1);
                    _deviceState = DeviceState.Connected;
                    break;
                case "ApplyConfig":
                case "UpdateAlarmSettings":
                case "SetAmplifierCurrents":
                case "RecalculateCalibrationPositions":
                    if (_connectState == 0)
                    {
                        _connectState = 1;
                    }
                    _nativeState = Math.Max(_nativeState, 2);
                    _deviceState = DeviceState.Connected;
                    break;
                case "StartCalibration":
                    _connectState = 1;
                    _nativeState = 5;
                    _deviceState = DeviceState.Calibrating;
                    break;
                case "StopCalibration":
                    _connectState = 1;
                    _nativeState = Math.Max(3, _nativeState == 5 ? 3 : _nativeState);
                    _deviceState = DeviceState.Connected;
                    break;
                case "StartRun":
                    _connectState = 1;
                    _nativeState = 4;
                    _deviceState = DeviceState.Running;
                    break;
                case "StopRun":
                    _connectState = 1;
                    _nativeState = Math.Max(3, _nativeState == 4 ? 3 : _nativeState);
                    _deviceState = DeviceState.Connected;
                    break;
                case "Disconnect":
                    _connectState = 0;
                    _nativeState = 0;
                    _deviceState = DeviceState.Disconnected;
                    break;
            }

            ViewState.DeviceState = _deviceState;
            ViewState.LastErrorMessage = string.Empty;
            _lastError = string.Empty;
            UpdateViewStateFlags();
        }
    }

    private static bool TryDeserializePayload<T>(DeviceIpcMessage message, out T? payload)
    {
        payload = default;
        if (message.Payload is null)
        {
            return false;
        }

        try
        {
            payload = message.Payload.Value.Deserialize<T>(JsonOptions);
            return payload is not null;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _heartbeatTimer.Dispose();

        try
        {
            if (_pipe is not null && _pipe.IsConnected && _writer is not null)
            {
                SendCommandExpectCode("Shutdown", new EmptyPayload(), TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
        }

        try
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _pipe?.Dispose();
        }
        catch
        {
        }

        _writer = null;
        _reader = null;
        _pipe = null;

        _cts.Cancel();

        try
        {
            if (_workerProcess is not null && !_workerProcess.HasExited)
            {
                if (!_workerProcess.WaitForExit(3000))
                {
                    _workerProcess.Kill(entireProcessTree: false);
                    _workerProcess.WaitForExit(3000);
                    _logger.Info($"[{_device.DeviceId}] worker killed during dispose pid={_workerProcess.Id}");
                }
            }
        }
        catch
        {
        }

        try
        {
            string workerExePath = GetWorkerExecutablePath();
            string pidFilePath = GetWorkerPidFilePath();
            if (TryTerminateWorkerByPidFile(pidFilePath, workerExePath, out int terminatedPid))
            {
                _logger.Info($"[{_device.DeviceId}] stale worker terminated during dispose pid={terminatedPid}");
            }
        }
        catch
        {
        }

        try
        {
            _readLoopTask?.Wait(1000);
        }
        catch
        {
        }

        _workerProcess?.Dispose();
        _cts.Dispose();
        _writeGate.Dispose();
    }
}
